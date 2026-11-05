using System.Net;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using SemanticBridge.Cli;
using SemanticBridge.Cli.Mcp;
using SemanticBridge.Core;
using SemanticBridge.Core.Config;
using SemanticBridge.Core.Safety;
using SemanticBridge.Core.Xmla;

namespace SemanticBridge.Tests;

internal sealed class FakeDescriptionWriter : IDescriptionWriter
{
    public List<ModelObjectInfo> Model { get; set; } =
    [
        new(TargetKind.Table, "Sales", "Sales", "Old table text"),
        new(TargetKind.Column, "Sales", "Amount", null),
        new(TargetKind.Column, "Sales", "Odd]]Name", "x"),
        new(TargetKind.Measure, "Sales", "Total", "Same"),
    ];

    public Exception? ListFails { get; set; }
    public int ListCalls { get; private set; }
    public List<IReadOnlyList<DescriptionChange>> Applied { get; } = [];
    public string? LastXmlaWorkspace { get; private set; }

    public Task<IReadOnlyList<ModelObjectInfo>> ListAsync(string workspace, string dataset, CancellationToken ct)
    {
        ListCalls++;
        LastXmlaWorkspace = workspace;
        if (ListFails is not null) throw ListFails;
        return Task.FromResult<IReadOnlyList<ModelObjectInfo>>(Model);
    }

    public Task ApplyAsync(string workspace, string dataset, IReadOnlyList<DescriptionChange> changes, CancellationToken ct)
    {
        Applied.Add(changes);
        return Task.CompletedTask;
    }
}

internal static class WriteTestData
{
    public static ResolvedConfig WriteConfig(bool allowWrite = true) => new()
    {
        ProfileName = "test",
        ProfileSource = "test",
        Cloud = Cloud.Global,
        TenantId = "tenant",
        ClientId = "client",
        ClientSecret = "super-secret-value",
        Endpoints = CloudPresets.Global,
        AllowWrite = allowWrite,
        DefaultWorkspace = "Sales",
    };

    /// <summary>Workspace "Sales", dataset "Model"; refresh POST gives 202 with a Location header; GET refreshes serves the queued statuses in order (the last repeats).</summary>
    public static FakeHandler RefreshApi(IEnumerable<string> statuses, string requestId = "req-1", HttpStatusCode post = HttpStatusCode.Accepted, string postBody = "")
    {
        var queue = new Queue<string>(statuses);
        string last = "Unknown";
        return new FakeHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/groups", StringComparison.Ordinal))
                return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"id":"{{TestData.WorkspaceId}}","name":"Sales","isOnDedicatedCapacity":true}]}""");
            if (path.EndsWith("/datasets", StringComparison.Ordinal))
                return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"id":"{{TestData.DatasetId}}","name":"Model"}]}""");
            if (path.EndsWith("/refreshes", StringComparison.Ordinal) && req.Method == HttpMethod.Post)
            {
                var response = FakeHandler.Json(post, postBody);
                if (post == HttpStatusCode.Accepted) response.Headers.Location = new Uri($"https://api.example/refreshes/{requestId}");
                return response;
            }
            if (path.EndsWith("/refreshes", StringComparison.Ordinal))
            {
                if (queue.Count > 0) last = queue.Dequeue();
                var end = last == "Unknown" ? "" : "2026-01-01T00:05:00Z";
                var error = last == "Failed" ? ",\"serviceExceptionJson\":\"{\\\"errorCode\\\":\\\"ModelRefreshFailed\\\"}\"" : "";
                return FakeHandler.Json(HttpStatusCode.OK,
                    $$"""{"value":[{"requestId":"{{requestId}}","refreshType":"ViaApi","status":"{{last}}","startTime":"{{DateTimeOffset.UtcNow:o}}","endTime":"{{end}}"{{error}}}]}""");
            }
            return FakeHandler.Json(HttpStatusCode.NotFound, "{}");
        });
    }

    /// <summary>Like <see cref="RefreshApi"/> but the POST returns no request ID. History: one finished run from 60 s ago, plus a new run once <paramref name="newRunAfterPolls"/> GETs happened after the POST (null: never).</summary>
    public static FakeHandler NoRequestIdApi(int? newRunAfterPolls)
    {
        var posted = false;
        var getsAfterPost = 0;
        return new FakeHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/groups", StringComparison.Ordinal))
                return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"id":"{{TestData.WorkspaceId}}","name":"Sales","isOnDedicatedCapacity":true}]}""");
            if (path.EndsWith("/datasets", StringComparison.Ordinal))
                return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"id":"{{TestData.DatasetId}}","name":"Model"}]}""");
            if (req.Method == HttpMethod.Post)
            {
                posted = true;
                return FakeHandler.Json(HttpStatusCode.Accepted, "");
            }
            if (posted) getsAfterPost++;
            var old = $$"""{"requestId":"old-1","refreshType":"ViaApi","status":"Completed","startTime":"{{DateTimeOffset.UtcNow.AddSeconds(-60):o}}","endTime":"{{DateTimeOffset.UtcNow.AddSeconds(-30):o}}"}""";
            var fresh = newRunAfterPolls is { } n && getsAfterPost > n
                ? $$"""{"requestId":"new-2","refreshType":"ViaApi","status":"Completed","startTime":"{{DateTimeOffset.UtcNow:o}}","endTime":"{{DateTimeOffset.UtcNow:o}}"},"""
                : "";
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{{fresh}}{{old}}]}""");
        });
    }

    public static BridgeService Service(ResolvedConfig cfg, FakeHandler handler, FakeDescriptionWriter? writer = null, WriteGate? gate = null, List<TimeSpan>? delays = null) =>
        new(cfg, TestData.Rest(handler, cfg), new FakeXmla(), writer ?? new FakeDescriptionWriter(), gate,
            (d, _) => { delays?.Add(d); return Task.CompletedTask; });
}

public sealed class DescriptionTargetTests
{
    [Theory]
    [InlineData("Sales[Amount]", "Sales", "Amount")]
    [InlineData("  Sales[Amount]  ", "Sales", "Amount")]
    [InlineData("Sales Orders[Order Date]", "Sales Orders", "Order Date")]
    [InlineData("'Sales Orders'[Order Date]", "Sales Orders", "Order Date")]
    [InlineData("'It''s [odd]'[Col]", "It's [odd]", "Col")]
    [InlineData("Sales[Odd]]Name]", "Sales", "Odd]Name")]
    [InlineData("Sales[a[b]", "Sales", "a[b")]
    public void ParsesColumns(string input, string table, string column)
    {
        var t = DescriptionTargets.ParseColumn(input);
        Assert.Equal(TargetKind.Column, t.Kind);
        Assert.Equal(table, t.Table);
        Assert.Equal(column, t.Name);
    }

    [Theory]
    [InlineData("Amount")]
    [InlineData("[Amount]")]
    [InlineData("Sales[]")]
    [InlineData("Sales[Amount")]
    [InlineData("'Sales[Amount]")]
    [InlineData("'Sales'Amount]")]
    [InlineData("")]
    public void RejectsMalformedColumns(string input)
    {
        var ex = Assert.Throws<BridgeException>(() => DescriptionTargets.ParseColumn(input));
        Assert.Equal(ErrorKind.Usage, ex.Kind);
        Assert.Contains("Table[Column]", ex.Message);
    }

    private static readonly IReadOnlyList<ModelObjectInfo> Model =
    [
        new(TargetKind.Table, "Sales", "Sales", null),
        new(TargetKind.Table, "Date", "Date", null),
        new(TargetKind.Column, "Sales", "Amount", "a"),
        new(TargetKind.Column, "Date", "Amount", "b"),
        new(TargetKind.Measure, "Sales", "Total", null),
        new(TargetKind.Measure, "Date", "Total", null),
        new(TargetKind.Measure, "Sales", "Margin", "m"),
    ];

    [Fact]
    public void AmbiguousMeasureListsTheTables()
    {
        var ex = Assert.Throws<BridgeException>(() => DescriptionTargets.Resolve(Model, DescriptionTargets.Measure("Total")));
        Assert.Equal(ErrorKind.Ambiguous, ex.Kind);
        Assert.Contains("Sales", ex.Message);
        Assert.Contains("Date", ex.Message);
    }

    [Fact]
    public void ResolvesCaseInsensitivelyAndFillsInTheMeasureTable()
    {
        var found = DescriptionTargets.Resolve(Model, DescriptionTargets.Measure("margin"));
        Assert.Equal(("Sales", "Margin"), (found.Table, found.Name));
        Assert.Equal("a", DescriptionTargets.Resolve(Model, DescriptionTargets.ParseColumn("sales[amount]")).Description);
    }

    [Fact]
    public void MissingTargetListsCloseNames()
    {
        var ex = Assert.Throws<BridgeException>(() => DescriptionTargets.Resolve(Model, DescriptionTargets.Measure("Marg")));
        Assert.Equal(ErrorKind.NotFound, ex.Kind);
        Assert.Contains("Sales[Margin]", ex.Message);
    }

    [Fact]
    public void PlanSkipsUnchangedAndReportsOldAndNew()
    {
        var (changes, unchanged) = DescriptionTargets.Plan(Model,
        [
            new DescriptionUpdate(DescriptionTargets.Measure("Margin"), "m"),
            new DescriptionUpdate(DescriptionTargets.ParseColumn("Sales[Amount]"), "new text"),
            new DescriptionUpdate(DescriptionTargets.Table("Sales"), ""),
        ]);
        var change = Assert.Single(changes, c => c.Name == "Amount");
        Assert.Equal(("a", "new text"), (change.Old, change.New));
        // A null old description equals an empty new one, so clearing nothing is "unchanged" too.
        Assert.Equal(["Margin", "Sales"], unchanged.Select(u => u.Name).Order().ToArray());
    }

    [Fact]
    public void ListingATargetTwiceIsRejected()
    {
        var ex = Assert.Throws<BridgeException>(() => DescriptionTargets.Plan(Model,
        [
            new DescriptionUpdate(DescriptionTargets.Table("Sales"), "a"),
            new DescriptionUpdate(DescriptionTargets.Table("sales"), "b"),
        ]));
        Assert.Contains("more than once", ex.Message);
    }
}

public sealed class DescriptionBatchTests
{
    [Fact]
    public void ParsesYaml()
    {
        var items = DescriptionBatch.Parse("""
            - table: Sales
              description: Fact table
            - column: "Sales[Amount]"
              description: Net amount
            - measure: Total
              description: ""
            """, json: false);
        Assert.Equal(3, items.Count);
        Assert.Equal(TargetKind.Table, items[0].Target.Kind);
        Assert.Equal(("Sales", "Amount"), (items[1].Target.Table, items[1].Target.Name));
        Assert.Equal("", items[2].Description);
    }

    [Fact]
    public void ParsesJson()
    {
        var items = DescriptionBatch.Parse("""[{"measure":"Total","description":"Sum"},{"column":"'A B'[C]","description":"x"}]""", json: true);
        Assert.Equal(TargetKind.Measure, items[0].Target.Kind);
        Assert.Equal("A B", items[1].Target.Table);
    }

    [Theory]
    [InlineData("""[{"table":"A","description":"x"},{"description":"secret-text-1"}]""", "Batch item 2 must have exactly one")]
    [InlineData("""[{"table":"A","measure":"B","description":"secret-text-1"}]""", "Batch item 1 must have exactly one")]
    [InlineData("""[{"table":"A"}]""", "Batch item 1 has no 'description'")]
    [InlineData("""[{"table":"A","description":"x"},{"column":"NoBrackets","description":"secret-text-1"}]""", "Batch item 2: A column must be given")]
    [InlineData("""[{"table":"A","descr":"secret-text-1"}]""", "Batch item 1 has an unknown key 'descr'")]
    public void ErrorsNameTheItemAndNeverEchoValues(string json, string expected)
    {
        var ex = Assert.Throws<BridgeException>(() => DescriptionBatch.Parse(json, json: true));
        Assert.Equal(ErrorKind.Usage, ex.Kind);
        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain("secret-text-1", ex.Message);
    }

    [Theory]
    [InlineData("not: a list", false)]
    [InlineData("[", true)]
    [InlineData("", false)]
    [InlineData("[]", true)]
    public void InvalidFilesAreUsageErrors(string text, bool json)
    {
        Assert.Equal(ErrorKind.Usage, Assert.Throws<BridgeException>(() => DescriptionBatch.Parse(text, json)).Kind);
    }
}

public sealed class WriteErrorMappingTests
{
    [Theory]
    [InlineData("The database is read-only", "Read Write")]
    [InlineData("Cannot be modified: enhanced metadata is required", "enhanced metadata")]
    [InlineData("The user does not have permission to alter the database", "Write permission")]
    public void MapsCommonFailuresToAdvice(string engine, string advice)
    {
        var mapped = TomDescriptionWriter.MapError(engine);
        Assert.Contains(advice, mapped);
        Assert.Contains(engine, mapped);
    }

    [Theory]
    [InlineData("Could not open the PBIX file")]
    [InlineData("Close Power BI Desktop and retry")]
    public void UnrelatedDesktopMentionsGetNoAdvice(string engine) => Assert.Equal(engine, TomDescriptionWriter.MapError(engine));

    [Fact]
    public void UnknownFailuresPassThroughAndConnectionStringsAreScrubbedFirst()
    {
        Assert.Equal("boom", TomDescriptionWriter.MapError("boom"));
        var scrubber = new SecretScrubber();
        scrubber.Register("super-secret-value");
        var scrubbed = scrubber.Scrub("fail: Data Source=powerbi://x;User ID=app:c@t;Password=super-secret-value;");
        var mapped = TomDescriptionWriter.MapError(scrubbed);
        Assert.DoesNotContain("super-secret-value", mapped);
        Assert.DoesNotContain("Data Source", mapped);
    }
}

public sealed class WriteServiceTests
{
    [Fact]
    public async Task ClosedGateRefusesBeforeAnyNetworkCall()
    {
        var handler = WriteTestData.RefreshApi([]);
        var writer = new FakeDescriptionWriter();
        var service = WriteTestData.Service(WriteTestData.WriteConfig(allowWrite: false), handler, writer);
        var update = new[] { new DescriptionUpdate(DescriptionTargets.Table("Sales"), "x") };
        Assert.Equal(ErrorKind.WriteDisabled, (await Assert.ThrowsAsync<BridgeException>(() => service.PlanDescriptionsAsync(null, "Model", update, CancellationToken.None))).Kind);
        Assert.Equal(ErrorKind.WriteDisabled, (await Assert.ThrowsAsync<BridgeException>(() => service.RefreshAsync(null, "Model", CancellationToken.None))).Kind);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, writer.ListCalls);
    }

    [Fact]
    public async Task HttpGateNeedsTheSecondOptIn()
    {
        var handler = WriteTestData.RefreshApi([]);
        var service = WriteTestData.Service(WriteTestData.WriteConfig(), handler, gate: new WriteGate(true, overHttp: true, allowHttpWrite: false));
        await Assert.ThrowsAsync<BridgeException>(() => service.RefreshAsync(null, "Model", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PlanReportsOldAndNewAndApplyUsesTheXmlaWorkspaceName()
    {
        var writer = new FakeDescriptionWriter();
        var service = WriteTestData.Service(WriteTestData.WriteConfig(), WriteTestData.RefreshApi([]), writer);
        var plan = await service.UpdateDescriptionsAsync(null, "Model",
            [new DescriptionUpdate(DescriptionTargets.Table("Sales"), "New table text"), new DescriptionUpdate(DescriptionTargets.Measure("Total"), "Same")], CancellationToken.None);
        var change = Assert.Single(plan.Changes);
        Assert.Equal(("table", "Old table text", "New table text"), (change.Type, change.Old, change.New));
        Assert.Equal("Total", Assert.Single(plan.Unchanged).Name);
        Assert.Equal("Sales", writer.LastXmlaWorkspace);
        Assert.Single(Assert.Single(writer.Applied));
    }

    [Fact]
    public async Task NothingToApplyMeansNoSave()
    {
        var writer = new FakeDescriptionWriter();
        var service = WriteTestData.Service(WriteTestData.WriteConfig(), WriteTestData.RefreshApi([]), writer);
        var plan = await service.UpdateDescriptionsAsync(null, "Model", [new DescriptionUpdate(DescriptionTargets.Measure("Total"), "Same")], CancellationToken.None);
        Assert.Empty(plan.Changes);
        Assert.Empty(writer.Applied);
    }

    [Fact]
    public async Task WorkspaceWithoutDedicatedCapacityIsRefused()
    {
        var handler = new FakeHandler((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/groups", StringComparison.Ordinal)
            ? FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"id":"{{TestData.WorkspaceId}}","name":"Sales","isOnDedicatedCapacity":false}]}""")
            : FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"id":"{{TestData.DatasetId}}","name":"Model"}]}"""));
        var writer = new FakeDescriptionWriter();
        var service = WriteTestData.Service(WriteTestData.WriteConfig(), handler, writer);
        var ex = await Assert.ThrowsAsync<BridgeException>(() => service.PlanDescriptionsAsync(null, "Model", [new DescriptionUpdate(DescriptionTargets.Table("Sales"), "x")], CancellationToken.None));
        Assert.Contains("XMLA endpoint", ex.Message);
        Assert.Equal(0, writer.ListCalls);
    }

    [Fact]
    public async Task RefreshPostsNoNotificationAndReturnsTheRequestId()
    {
        var handler = WriteTestData.RefreshApi([]);
        var service = WriteTestData.Service(WriteTestData.WriteConfig(), handler);
        var trigger = await service.RefreshAsync(null, "Model", CancellationToken.None);
        Assert.Equal("req-1", trigger.RequestId);
        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains($"/groups/{TestData.WorkspaceId}/datasets/{TestData.DatasetId}/refreshes", post.Url);
        Assert.Equal("""{"notifyOption":"NoNotification"}""", post.Body);
    }

    [Fact]
    public async Task WaitPollsWithBackoffUntilCompleted()
    {
        var delays = new List<TimeSpan>();
        var service = WriteTestData.Service(WriteTestData.WriteConfig(), WriteTestData.RefreshApi(["Unknown", "Unknown", "Unknown", "Unknown", "Completed"]), delays: delays);
        var trigger = await service.RefreshAsync(null, "Model", CancellationToken.None);
        var result = await service.WaitForRefreshAsync(trigger, TimeSpan.FromMinutes(10), CancellationToken.None);
        Assert.False(result.TimedOut);
        Assert.Equal("Completed", result.Entry!.Status);
        Assert.Equal([2, 4, 8, 16], delays.Select(d => (int)d.TotalSeconds));
    }

    [Fact]
    public async Task WaitStopsOnFailure()
    {
        var service = WriteTestData.Service(WriteTestData.WriteConfig(), WriteTestData.RefreshApi(["Unknown", "Failed"]));
        var result = await service.WaitForRefreshAsync(await service.RefreshAsync(null, "Model", CancellationToken.None), TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal("Failed", result.Entry!.Status);
        Assert.Contains("ModelRefreshFailed", result.Entry.Error);
    }

    [Fact]
    public async Task WaitTimesOutAndSaysSo()
    {
        var delays = new List<TimeSpan>();
        var service = WriteTestData.Service(WriteTestData.WriteConfig(), WriteTestData.RefreshApi(["Unknown"]), delays: delays);
        var result = await service.WaitForRefreshAsync(await service.RefreshAsync(null, "Model", CancellationToken.None), TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(result.TimedOut);
        Assert.Equal("Unknown", result.Entry!.Status);
        Assert.Equal(TimeSpan.FromSeconds(10), delays.Aggregate(TimeSpan.Zero, (a, b) => a + b));
    }

    [Fact]
    public async Task WaitWithoutRequestIdNeverAcceptsAnEarlierRun()
    {
        var service = WriteTestData.Service(WriteTestData.WriteConfig(), WriteTestData.NoRequestIdApi(null));
        var trigger = await service.RefreshAsync(null, "Model", CancellationToken.None, trackForWait: true);
        Assert.Null(trigger.RequestId);
        var result = await service.WaitForRefreshAsync(trigger, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(result.TimedOut);
        Assert.Null(result.Entry);
    }

    [Fact]
    public async Task WaitWithoutRequestIdFindsTheNewRun()
    {
        var service = WriteTestData.Service(WriteTestData.WriteConfig(), WriteTestData.NoRequestIdApi(2));
        var trigger = await service.RefreshAsync(null, "Model", CancellationToken.None, trackForWait: true);
        var result = await service.WaitForRefreshAsync(trigger, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.False(result.TimedOut);
        Assert.Equal("new-2", result.Entry!.RequestId);
    }

    [Fact]
    public async Task LongRefreshErrorIsCutAndSaysSo()
    {
        var text = new string('x', 600);
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.OK,
            $$"""{"value":[{"requestId":"r","refreshType":"ViaApi","status":"Failed","startTime":"2026-01-01T00:00:00Z","endTime":"2026-01-01T00:01:00Z","serviceExceptionJson":"{{text}}"}]}"""));
        var e = Assert.Single(await TestData.Rest(handler).GetRefreshesAsync("w", "d", 1, CancellationToken.None));
        Assert.EndsWith("(truncated)", e.Error);
        var short1 = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.OK,
            """{"value":[{"requestId":"r","status":"Failed","serviceExceptionJson":"short"}]}"""));
        Assert.Equal("short", Assert.Single(await TestData.Rest(short1).GetRefreshesAsync("w", "d", 1, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task RefreshStatusParsesHistoryAndChecksTop()
    {
        var service = WriteTestData.Service(WriteTestData.WriteConfig(allowWrite: false), WriteTestData.RefreshApi(["Completed"]));
        var entries = await service.GetRefreshStatusAsync(null, "Model", 3, CancellationToken.None);
        var e = Assert.Single(entries);
        Assert.Equal(("req-1", "ViaApi", "Completed"), (e.RequestId, e.RefreshType, e.Status));
        Assert.True(e.IsFinished);
        await Assert.ThrowsAsync<BridgeException>(() => service.GetRefreshStatusAsync(null, "Model", 0, CancellationToken.None));
    }
}

public sealed class RefreshRestTests
{
    [Fact]
    public async Task TooManyRequestsGivesAClearMessageAfterBoundedRetries()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.TooManyRequests, "{}"));
        var ex = await Assert.ThrowsAsync<BridgeException>(() => TestData.Rest(handler).TriggerRefreshAsync("w", "d", CancellationToken.None));
        Assert.Equal(4, handler.Requests.Count);
        Assert.Contains("429", ex.Message);
        Assert.Contains("wait and retry", ex.Message);
    }

    [Fact]
    public async Task AlreadyInProgressIsExplained()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.BadRequest, """{"error":{"code":"InvalidRequest","message":"Refresh is already in progress"}}"""));
        var ex = await Assert.ThrowsAsync<BridgeException>(() => TestData.Rest(handler).TriggerRefreshAsync("w", "d", CancellationToken.None));
        Assert.Contains("already in progress", ex.Message);
        Assert.Contains("refresh-status", ex.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ServerErrorsOnThePostAreNotRetried()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"));
        await Assert.ThrowsAsync<BridgeException>(() => TestData.Rest(handler).TriggerRefreshAsync("w", "d", CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MissingRequestIdIsNull()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.Accepted, ""));
        Assert.Null(await TestData.Rest(handler).TriggerRefreshAsync("w", "d", CancellationToken.None));
    }
}

public sealed class CliWriteTests : IDisposable
{
    private const string Secret = "cli-fake-secret-value";
    private readonly string _dir = Directory.CreateTempSubdirectory("sbridge-write-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Config(bool allowWrite)
    {
        var path = Path.Combine(_dir, $"config-{allowWrite}.yaml");
        File.WriteAllText(path, $"profiles:\n  p:\n    tenant_id: t\n    client_id: c\n    client_secret: {Secret}\n    default_workspace: Sales\n    allow_write: {(allowWrite ? "true" : "false")}\n");
        return path;
    }

    private sealed record Outcome(int Code, string Out, string Err, int FactoryCalls, FakeDescriptionWriter Writer, FakeHandler Handler);

    private async Task<Outcome> Run(bool allowWrite, string stdin, bool redirected, IEnumerable<string>? statuses, params string[] args)
    {
        var writer = new FakeDescriptionWriter();
        var handler = WriteTestData.RefreshApi(statuses ?? ["Completed"]);
        var calls = 0;
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync([.. args, "--config", Config(allowWrite)], o, e, new StringReader(stdin),
            n => n is "HOME" or "APPDATA" ? _dir : null, () => redirected, CancellationToken.None,
            (cfg, gate) =>
            {
                calls++;
                return WriteTestData.Service(cfg, handler, writer, gate);
            });
        return new Outcome(code, o.ToString(), e.ToString(), calls, writer, handler);
    }

    [Theory]
    [InlineData("describe", "-d", "Model", "--table", "Sales", "text", "--yes")]
    [InlineData("refresh", "-d", "Model", "--yes")]
    public async Task ClosedGateExitsTwoBeforeAnyService(params string[] args)
    {
        var r = await Run(false, "", false, null, args);
        Assert.Equal(2, r.Code);
        Assert.Contains("Writes are disabled", r.Err);
        Assert.Contains("allow_write", r.Err);
        Assert.Equal(0, r.FactoryCalls);
        Assert.Empty(r.Handler.Requests);
        Assert.DoesNotContain(Secret, r.Out + r.Err);
    }

    [Fact]
    public async Task YesAppliesAndPrintsOldAndNew()
    {
        var r = await Run(true, "", true, null, "describe", "-d", "Model", "--table", "Sales", "Brand new", "--yes");
        Assert.Equal(0, r.Code);
        var applied = Assert.Single(Assert.Single(r.Writer.Applied));
        Assert.Equal("Brand new", applied.New);
        Assert.Contains("Old table text", r.Out);
        Assert.Contains("Brand new", r.Out);
        Assert.DoesNotContain("Apply 1 change(s)?", r.Err);
    }

    [Fact]
    public async Task JsonFormatReportsAppliedChanges()
    {
        var r = await Run(true, "", true, null, "describe", "-d", "Model", "--column", "Sales[Amount]", "Net", "--yes", "-f", "json");
        Assert.Equal(0, r.Code);
        using var doc = JsonDocument.Parse(r.Out);
        Assert.True(doc.RootElement.GetProperty("applied").GetBoolean());
        var change = doc.RootElement.GetProperty("changes")[0];
        Assert.Equal(JsonValueKind.Null, change.GetProperty("old").ValueKind);
        Assert.Equal("Net", change.GetProperty("new").GetString());
    }

    [Fact]
    public async Task InteractiveYesAnswerApplies()
    {
        var r = await Run(true, "y\n", false, null, "describe", "-d", "Model", "--table", "Sales", "Brand new");
        Assert.Equal(0, r.Code);
        Assert.Contains("Apply 1 change(s)? [y/N]", r.Err);
        Assert.Single(r.Writer.Applied);
    }

    [Theory]
    [InlineData("n\n")]
    [InlineData("\n")]
    [InlineData("")]
    public async Task AnythingButYesAbortsWithExitOne(string answer)
    {
        var r = await Run(true, answer, false, null, "describe", "-d", "Model", "--table", "Sales", "Brand new");
        Assert.Equal(1, r.Code);
        Assert.Contains("aborted", r.Err);
        Assert.Empty(r.Writer.Applied);
    }

    [Fact]
    public async Task NonInteractiveStdinWithoutYesIsRefusedBeforeAnyService()
    {
        var r = await Run(true, "y\n", true, null, "describe", "-d", "Model", "--table", "Sales", "Brand new");
        Assert.Equal(2, r.Code);
        Assert.Contains("--yes", r.Err);
        Assert.Equal(0, r.FactoryCalls);
        var refresh = await Run(true, "y\n", true, null, "refresh", "-d", "Model");
        Assert.Equal(2, refresh.Code);
        Assert.Equal(0, refresh.FactoryCalls);
    }

    [Fact]
    public async Task UnchangedTextIsReportedAndNotWritten()
    {
        var r = await Run(true, "", true, null, "describe", "-d", "Model", "--measure", "Total", "Same", "--yes");
        Assert.Equal(0, r.Code);
        Assert.Contains("unchanged", r.Err);
        Assert.Contains("No changes", r.Err);
        Assert.Empty(r.Writer.Applied);
    }

    [Fact]
    public async Task BatchYamlFileAppliesAllInOneSave()
    {
        var file = Path.Combine(_dir, "updates.yaml");
        File.WriteAllText(file, "- table: Sales\n  description: T\n- column: \"Sales[Amount]\"\n  description: C\n");
        var r = await Run(true, "", true, null, "describe", "-d", "Model", "--file", file, "--yes");
        Assert.Equal(0, r.Code);
        Assert.Equal(2, Assert.Single(r.Writer.Applied).Count);
    }

    [Fact]
    public async Task OversizedBatchFileIsAUsageErrorNamingTheLimit()
    {
        var file = Path.Combine(_dir, "big.yaml");
        File.WriteAllText(file, "# " + new string('x', 1024 * 1024 + 10));
        var r = await Run(true, "", true, null, "describe", "-d", "Model", "--file", file, "--yes");
        Assert.Equal(2, r.Code);
        Assert.Contains("1 MB limit", r.Err);
        Assert.Equal(0, r.FactoryCalls);
    }

    [Theory]
    [InlineData("missing-dir/none.yaml")]
    [InlineData("bad\0name.yaml")]
    public async Task UnreadableBatchPathIsAUsageError(string name)
    {
        var r = await Run(true, "", true, null, "describe", "-d", "Model", "--file", Path.Combine(_dir, name), "--yes");
        Assert.Equal(2, r.Code);
        Assert.Contains("Cannot read batch file", r.Err);
        Assert.Equal(0, r.FactoryCalls);
    }

    [Fact]
    public async Task RefreshWaitWithUnidentifiableRunReportsUnknownAndExitsOne()
    {
        var handler = WriteTestData.NoRequestIdApi(null);
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync(["refresh", "-d", "Model", "--yes", "--wait", "--timeout", "4", "--config", Config(true)], o, e, new StringReader(""),
            n => n is "HOME" or "APPDATA" ? _dir : null, () => true, CancellationToken.None,
            (cfg, gate) => WriteTestData.Service(cfg, handler, new FakeDescriptionWriter(), gate));
        Assert.Equal(1, code);
        Assert.Contains("status: unknown", o.ToString());
        Assert.Contains("refresh-status", e.ToString());
    }

    [Fact]
    public async Task BadBatchItemIsAUsageErrorNamingTheItem()
    {
        var file = Path.Combine(_dir, "bad.json");
        File.WriteAllText(file, """[{"table":"Sales","description":"ok"},{"description":"leaky-value"}]""");
        var r = await Run(true, "", true, null, "describe", "-d", "Model", "--file", file, "--yes");
        Assert.Equal(2, r.Code);
        Assert.Contains("Batch item 2", r.Err);
        Assert.DoesNotContain("leaky-value", r.Err);
        Assert.Equal(0, r.FactoryCalls);
    }

    [Theory]
    [InlineData("describe", "-d", "Model", "text")]
    [InlineData("describe", "-d", "Model", "--table", "A", "--measure", "B", "text")]
    [InlineData("describe", "-d", "Model", "--table", "A")]
    [InlineData("describe", "-d", "Model", "--table", "A", "text", "--file", "x.yaml")]
    public async Task TargetUsageIsValidated(params string[] args)
    {
        var r = await Run(true, "", true, null, [.. args, "--yes"]);
        Assert.Equal(2, r.Code);
        Assert.Equal(0, r.FactoryCalls);
    }

    [Fact]
    public async Task EngineErrorsAreScrubbedOnTheWay()
    {
        var writer = new FakeDescriptionWriter { ListFails = new BridgeException(ErrorKind.Xmla, $"failed Data Source=powerbi://x;Initial Catalog=m;Password={Secret}; {Secret}") };
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync(["describe", "-d", "Model", "--table", "Sales", "x", "--yes", "--config", Config(true)], o, e, new StringReader(""),
            n => n is "HOME" or "APPDATA" ? _dir : null, () => true, CancellationToken.None,
            (cfg, gate) => WriteTestData.Service(cfg, WriteTestData.RefreshApi([]), writer, gate));
        Assert.Equal(1, code);
        Assert.DoesNotContain(Secret, o + e.ToString());
        Assert.DoesNotContain("Data Source", e.ToString());
    }

    [Fact]
    public async Task RefreshWaitCompletedExitsZeroAndPrintsStatus()
    {
        var r = await Run(true, "", true, ["Unknown", "Completed"], "refresh", "-d", "Model", "--yes", "--wait");
        Assert.Equal(0, r.Code);
        Assert.Contains("accepted: true", r.Out);
        Assert.Contains("request_id: req-1", r.Out);
        Assert.Contains("status: Completed", r.Out);
        Assert.Contains(r.Handler.Requests, q => q.Method == HttpMethod.Post && q.Body!.Contains("NoNotification"));
    }

    [Fact]
    public async Task RefreshWithoutWaitReturnsAfterAccepted()
    {
        var r = await Run(true, "", true, null, "refresh", "-d", "Model", "--yes");
        Assert.Equal(0, r.Code);
        Assert.DoesNotContain("status:", r.Out);
        Assert.DoesNotContain(r.Handler.Requests, q => q.Method == HttpMethod.Get && q.Url.Contains("/refreshes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshWaitFailedExitsOne()
    {
        var r = await Run(true, "", true, ["Failed"], "refresh", "-d", "Model", "--yes", "--wait");
        Assert.Equal(1, r.Code);
        Assert.Contains("status: Failed", r.Out);
        Assert.Contains("ModelRefreshFailed", r.Out);
    }

    [Fact]
    public async Task RefreshWaitTimeoutExitsOneAndSaysSo()
    {
        var r = await Run(true, "", true, ["Unknown"], "refresh", "-d", "Model", "--yes", "--wait", "--timeout", "4");
        Assert.Equal(1, r.Code);
        Assert.Contains("did not finish within 4 s", r.Err);
    }

    [Fact]
    public async Task RefreshDeclinedStartsNothing()
    {
        var r = await Run(true, "n\n", false, null, "refresh", "-d", "Model");
        Assert.Equal(1, r.Code);
        Assert.Contains("aborted", r.Err);
        Assert.Equal(0, r.FactoryCalls);
    }

    [Fact]
    public async Task RefreshStatusIsAReadAndNeedsNoWriteGate()
    {
        var r = await Run(false, "", true, ["Completed"], "refresh-status", "-d", "Model", "--top", "3");
        Assert.Equal(0, r.Code);
        Assert.Contains("req-1", r.Out);
        Assert.Contains("Completed", r.Out);
        Assert.Contains(r.Handler.Requests, q => q.Url.Contains("$top=3", StringComparison.Ordinal));
    }
}

public sealed class McpWriteToolTests
{
    private static IReadOnlyList<ModelContextProtocol.Server.McpServerTool> Registry(bool allowWrite, bool http, bool allowHttpWrite, BridgeService? service = null)
    {
        var scrubber = new SecretScrubber();
        scrubber.Register("super-secret-value");
        var gate = new WriteGate(allowWrite, http, allowHttpWrite);
        service ??= WriteTestData.Service(WriteTestData.WriteConfig(allowWrite), WriteTestData.RefreshApi(["Completed"]), gate: gate);
        return McpToolRegistry.Create(new McpTools(service, scrubber), gate, TextWriter.Null);
    }

    [Theory]
    [InlineData(false, false, false, 5)]
    [InlineData(true, false, false, 7)]
    [InlineData(true, true, false, 5)]
    [InlineData(true, true, true, 7)]
    [InlineData(false, true, true, 5)]
    public void ToolCountFollowsTheGate(bool allowWrite, bool http, bool allowHttpWrite, int expected)
    {
        var names = Registry(allowWrite, http, allowHttpWrite).Select(t => t.ProtocolTool.Name).ToArray();
        Assert.Equal(expected, names.Length);
        Assert.Equal(expected == 7, names.Contains("update_description"));
        Assert.Equal(expected == 7, names.Contains("refresh_dataset"));
        Assert.Contains("get_refresh_status", names);
    }

    [Fact]
    public async Task WriteToolDescriptionsDemandUserConfirmation()
    {
        await using var mcp = await InProcessMcp.StartAsync(Registry(true, false, false));
        var listed = await mcp.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        foreach (var name in new[] { "update_description", "refresh_dataset" })
            Assert.Contains("confirmation", listed.Single(t => t.Name == name).Description);
        Assert.True(listed.Single(t => t.Name == "get_refresh_status").ProtocolTool.Annotations?.ReadOnlyHint);
    }

    private static string Text(CallToolResult r) => ((TextContentBlock)Assert.Single(r.Content)).Text;

    [Fact]
    public async Task UpdateDescriptionReportsOldAndNew()
    {
        var writer = new FakeDescriptionWriter();
        var gate = new WriteGate(true);
        var service = WriteTestData.Service(WriteTestData.WriteConfig(), WriteTestData.RefreshApi([]), writer, gate);
        await using var mcp = await InProcessMcp.StartAsync(Registry(true, false, false, service));
        var result = await mcp.Client.CallToolAsync("update_description",
            new Dictionary<string, object?> { ["dataset"] = "Model", ["target_type"] = "column", ["target"] = "Sales[Amount]", ["description"] = "Net amount" },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError != true, Text(result));
        using var doc = JsonDocument.Parse(Text(result));
        Assert.True(doc.RootElement.GetProperty("applied").GetBoolean());
        var change = doc.RootElement.GetProperty("changes")[0];
        Assert.Equal(JsonValueKind.Null, change.GetProperty("old").ValueKind);
        Assert.Equal("Net amount", change.GetProperty("new").GetString());
        Assert.Single(writer.Applied);
    }

    [Fact]
    public async Task BadTargetTypeAndAmbiguityComeBackAsToolErrors()
    {
        await using var mcp = await InProcessMcp.StartAsync(Registry(true, false, false));
        var bad = await mcp.Client.CallToolAsync("update_description",
            new Dictionary<string, object?> { ["dataset"] = "Model", ["target_type"] = "index", ["target"] = "x", ["description"] = "d" },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(bad.IsError);
        Assert.Contains("table, column or measure", Text(bad));
    }

    [Fact]
    public async Task ServiceGateIsASecondLineOfDefence()
    {
        // Even if a write tool were registered by mistake, the service refuses while its own gate is closed.
        var writer = new FakeDescriptionWriter();
        var service = WriteTestData.Service(WriteTestData.WriteConfig(allowWrite: false), WriteTestData.RefreshApi([]), writer);
        var tools = McpToolRegistry.Create(new McpTools(service, new SecretScrubber()), new WriteGate(true), TextWriter.Null);
        await using var mcp = await InProcessMcp.StartAsync(tools);
        var result = await mcp.Client.CallToolAsync("refresh_dataset", new Dictionary<string, object?> { ["dataset"] = "Model" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("Writes are disabled", Text(result));
    }

    [Fact]
    public async Task RefreshDatasetReturnsAcceptedAndRequestId()
    {
        await using var mcp = await InProcessMcp.StartAsync(Registry(true, false, false));
        var result = await mcp.Client.CallToolAsync("refresh_dataset", new Dictionary<string, object?> { ["dataset"] = "Model" }, cancellationToken: TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(Text(result));
        Assert.True(doc.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal("req-1", doc.RootElement.GetProperty("request_id").GetString());
    }

    [Fact]
    public async Task GetRefreshStatusWorksWithWritesOff()
    {
        await using var mcp = await InProcessMcp.StartAsync(Registry(false, false, false));
        var result = await mcp.Client.CallToolAsync("get_refresh_status", new Dictionary<string, object?> { ["dataset"] = "Model", ["top"] = 2 }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, result.IsError);
        Assert.Contains("Completed", Text(result));
    }

    [Fact]
    public async Task ErrorsNeverContainTheSecret()
    {
        var writer = new FakeDescriptionWriter { ListFails = new BridgeException(ErrorKind.Xmla, "bad super-secret-value Password=super-secret-value") };
        var scrubber = new SecretScrubber();
        scrubber.Register("super-secret-value");
        var gate = new WriteGate(true);
        var service = WriteTestData.Service(WriteTestData.WriteConfig(), WriteTestData.RefreshApi([]), writer, gate);
        await using var mcp = await InProcessMcp.StartAsync(McpToolRegistry.Create(new McpTools(service, scrubber), gate, TextWriter.Null));
        var result = await mcp.Client.CallToolAsync("update_description",
            new Dictionary<string, object?> { ["dataset"] = "Model", ["target_type"] = "table", ["target"] = "Sales", ["description"] = "x" },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.DoesNotContain("super-secret-value", Text(result));
    }
}
