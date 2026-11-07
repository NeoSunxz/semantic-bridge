using System.Net;
using System.Net.Http.Headers;
using SemanticBridge.Core.Rest;
using SemanticBridge.Core.Results;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Tests;

public sealed class RestClientTests
{
    private static string Groups(int from, int count) =>
        "{\"value\":[" + string.Join(",", Enumerable.Range(from, count).Select(i => $"{{\"id\":\"id-{i}\",\"name\":\"ws {i}\"}}")) + "]}";

    [Fact]
    public async Task WorkspacesArePagedUntilAShortPage()
    {
        var handler = new FakeHandler((req, _) =>
        {
            var query = req.RequestUri!.Query;
            return query.Contains("$skip=0") ? FakeHandler.Json(HttpStatusCode.OK, Groups(0, PowerBiRestClient.PageSize))
                : query.Contains($"$skip={PowerBiRestClient.PageSize}") ? FakeHandler.Json(HttpStatusCode.OK, Groups(PowerBiRestClient.PageSize, 3))
                : FakeHandler.Json(HttpStatusCode.BadRequest, "{}");
        });
        var all = await TestData.Rest(handler).GetWorkspacesAsync(CancellationToken.None);
        Assert.Equal(PowerBiRestClient.PageSize + 3, all.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Contains("$top=", r.Url));
    }

    [Fact]
    public async Task RequestsCarryTheBearerToken()
    {
        AuthenticationHeaderValue? header = null;
        var handler = new FakeHandler((req, _) => { header = req.Headers.Authorization; return FakeHandler.Json(HttpStatusCode.OK, "{\"value\":[]}"); });
        await TestData.Rest(handler).GetWorkspacesAsync(CancellationToken.None);
        Assert.Equal("Bearer", header?.Scheme);
        Assert.Equal("fake-token-value-123", header?.Parameter);
    }

    [Fact]
    public async Task ExecuteQuerySendsIncludeNullsAndParsesRowsExactly()
    {
        var handler = TestData.PowerBi("""{"results":[{"tables":[{"rows":[{"Sales[Amount]":1.5,"[Count]":2,"Sales[Name]":"a"},{"Sales[Amount]":null,"[Count]":3,"Sales[Name]":null}]}]}]}""");
        var result = await TestData.Rest(handler).ExecuteQueryAsync(TestData.WorkspaceId, TestData.DatasetId, "EVALUATE x", 100, CancellationToken.None);
        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Contains("\"includeNulls\":true", post.Body);
        Assert.Equal(["Sales[Amount]", "[Count]", "Sales[Name]"], result.Columns.Select(c => c.Name));
        Assert.Equal(["Double", "Int64", "String"], result.Columns.Select(c => c.Type));
        Assert.Equal(2, result.RowCount);
        Assert.Null(result.Rows[1][0]);
        Assert.False(result.Truncated);
        Assert.Equal(ResultPaths.Rest, result.Path);
    }

    [Fact]
    public async Task Http200WithErrorObjectAndPartialRowsIsTruncated()
    {
        var handler = TestData.PowerBi("""{"results":[{"error":{"code":"LimitExceeded","message":"Too many values"},"tables":[{"rows":[{"[x]":1},{"[x]":2}]}]}]}""");
        var result = await TestData.Rest(handler).ExecuteQueryAsync(TestData.WorkspaceId, TestData.DatasetId, "EVALUATE x", 100, CancellationToken.None);
        Assert.True(result.Truncated);
        Assert.Contains("Too many values", result.TruncationReason);
        Assert.Contains("HTTP 200", result.TruncationReason);
        Assert.Equal(2, result.RowCount);
    }

    [Fact]
    public async Task Http200WithErrorAndNoRowsIsAnError()
    {
        var handler = TestData.PowerBi("""{"error":{"code":"DAX","message":"bad column"}}""");
        var ex = await Assert.ThrowsAsync<BridgeException>(() => TestData.Rest(handler).ExecuteQueryAsync(TestData.WorkspaceId, TestData.DatasetId, "EVALUATE x", 100, CancellationToken.None));
        Assert.Contains("bad column", ex.Message);
    }

    [Fact]
    public async Task RowCapAppliesToRestRowsToo()
    {
        var rows = string.Join(",", Enumerable.Range(0, 10).Select(i => $"{{\"[x]\":{i}}}"));
        var handler = TestData.PowerBi($$"""{"results":[{"tables":[{"rows":[{{rows}}]}]}]}""");
        var result = await TestData.Rest(handler).ExecuteQueryAsync(TestData.WorkspaceId, TestData.DatasetId, "EVALUATE x", 4, CancellationToken.None);
        Assert.Equal(4, result.RowCount);
        Assert.True(result.Truncated);
        Assert.Contains("max_rows (4)", result.TruncationReason);
    }

    [Fact]
    public async Task Retries429HonouringRetryAfterThenSucceeds()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var handler = new FakeHandler((_, _) =>
        {
            calls++;
            if (calls < 3)
            {
                var r = FakeHandler.Json(HttpStatusCode.TooManyRequests, "{}");
                r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return r;
            }
            return FakeHandler.Json(HttpStatusCode.OK, "{\"value\":[]}");
        });
        var client = new PowerBiRestClient(new HttpClient(handler), TestData.Config(), new FakeTokenProvider(), new SecretScrubber(), (d, _) => { delays.Add(d); return Task.CompletedTask; });
        await client.GetWorkspacesAsync(CancellationToken.None);
        Assert.Equal(3, calls);
        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7)], delays);
    }

    [Fact]
    public async Task RetriesAreBounded()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var ex = await Assert.ThrowsAsync<BridgeException>(() => TestData.Rest(handler).GetWorkspacesAsync(CancellationToken.None));
        Assert.Equal(4, handler.Requests.Count);
        Assert.Contains("503", ex.Message);
    }

    [Fact]
    public async Task ClientErrorsAreNotRetriedAndAreScrubbed()
    {
        var scrubber = new SecretScrubber();
        scrubber.Register("leaky-secret-value");
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden","message":"denied for leaky-secret-value Bearer abc.def.ghi"}}"""));
        var ex = await Assert.ThrowsAsync<BridgeException>(() => TestData.Rest(handler, scrubber: scrubber).GetWorkspacesAsync(CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.Contains("403", ex.Message);
        Assert.DoesNotContain("leaky-secret-value", ex.Message);
        Assert.DoesNotContain("abc.def.ghi", ex.Message);
    }

    [Fact]
    public async Task SecretStraddlingTheTruncationLimitIsScrubbedFirst()
    {
        const string secret = "straddling-secret-value";
        var scrubber = new SecretScrubber();
        scrubber.Register(secret);
        var plain = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.Forbidden, new string('x', 490) + secret + " tail"));
        var ex1 = await Assert.ThrowsAsync<BridgeException>(() => TestData.Rest(plain, scrubber: scrubber).GetWorkspacesAsync(CancellationToken.None));
        Assert.DoesNotContain("straddling", ex1.Message);
        var body = "{\"error\":{\"code\":\"Forbidden\",\"message\":\"" + new string('x', 480) + secret + " tail\"}}";
        var json = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.Forbidden, body));
        var ex2 = await Assert.ThrowsAsync<BridgeException>(() => TestData.Rest(json, scrubber: scrubber).GetWorkspacesAsync(CancellationToken.None));
        Assert.DoesNotContain("straddling", ex2.Message);
    }

    [Fact]
    public async Task DaxErrorsOn400AreClassifiedAsDax()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.BadRequest,
            """{"error":{"code":"DatasetExecuteQueriesError","pbi.error":{"details":[{"detail":{"value":"Column 'X' not found"}}]}}}"""));
        var ex = await Assert.ThrowsAsync<BridgeException>(() => TestData.Rest(handler).ExecuteQueryAsync("w", "d", "EVALUATE x", 10, CancellationToken.None));
        Assert.Equal(ErrorKind.Dax, ex.Kind);
        Assert.Contains("Column 'X' not found", ex.Message);
    }
}

public sealed class NameResolverTests
{
    private static readonly IReadOnlyList<Workspace> Items =
    [
        new("aaaaaaaa-0000-0000-0000-000000000001", "Sales", null),
        new("aaaaaaaa-0000-0000-0000-000000000002", "Finance", null),
        new("aaaaaaaa-0000-0000-0000-000000000003", "finance", null),
    ];

    private static Workspace Resolve(string input) => NameResolver.Resolve(Items, input, "Workspace", w => w.Id, w => w.Name);

    [Fact]
    public void MatchesByNameCaseInsensitively() => Assert.Equal("Sales", Resolve("sALES").Name);

    [Fact]
    public void MatchesById() => Assert.Equal("Finance", Resolve("AAAAAAAA-0000-0000-0000-000000000002").Name);

    [Fact]
    public void IdWinsOverAmbiguousNames() => Assert.Equal("finance", Resolve("aaaaaaaa-0000-0000-0000-000000000003").Name);

    [Fact]
    public void AmbiguousNameListsCandidatesWithIds()
    {
        var ex = Assert.Throws<BridgeException>(() => Resolve("FINANCE"));
        Assert.Equal(ErrorKind.Ambiguous, ex.Kind);
        Assert.Contains("aaaaaaaa-0000-0000-0000-000000000002", ex.Message);
        Assert.Contains("aaaaaaaa-0000-0000-0000-000000000003", ex.Message);
    }

    [Fact]
    public void NotFoundIsAnError()
    {
        var ex = Assert.Throws<BridgeException>(() => Resolve("Nope"));
        Assert.Equal(ErrorKind.NotFound, ex.Kind);
        Assert.Contains("Nope", ex.Message);
    }
}
