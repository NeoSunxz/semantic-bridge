using System.Net;
using System.Text;
using SemanticBridge.Core.Auth;
using SemanticBridge.Core.Config;
using SemanticBridge.Core.Rest;
using SemanticBridge.Core.Results;
using SemanticBridge.Core.Safety;
using SemanticBridge.Core.Xmla;

namespace SemanticBridge.Tests;

internal sealed class FakeTokenProvider(string token = "fake-token-value-123") : ITokenProvider
{
    public Task<AccessTokenInfo> GetTokenAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AccessTokenInfo(token, DateTimeOffset.UtcNow.AddHours(1)));
}

internal sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.ToString(), body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class FakeXmla : IXmlaClient
{
    public Func<QueryResult>? OnQuery { get; set; }
    public Func<ModelSchema>? OnSchema { get; set; }
    public int QueryCalls { get; private set; }
    public int? LastMaxRows { get; private set; }
    public string? LastWorkspace { get; private set; }

    public Task<QueryResult> QueryAsync(string workspace, string dataset, string dax, int maxRows, CancellationToken ct)
    {
        QueryCalls++;
        LastMaxRows = maxRows;
        LastWorkspace = workspace;
        return Task.FromResult(OnQuery!());
    }

    public Task<ModelSchema> GetSchemaAsync(string workspace, string dataset, string? tableFilter, CancellationToken ct) => Task.FromResult(OnSchema!());

    public Task TestConnectionAsync(string workspace, string? dataset, CancellationToken ct) => Task.CompletedTask;
}

internal static class TestData
{
    public const string WorkspaceId = "11111111-1111-1111-1111-111111111111";
    public const string DatasetId = "22222222-2222-2222-2222-222222222222";

    public static ResolvedConfig Config(QueryVia via = QueryVia.Auto, int maxRows = 1000, int ceiling = 100_000, string? defaultWorkspace = null, string? defaultDataset = null) => new()
    {
        ProfileName = "test",
        ProfileSource = "test",
        Cloud = Cloud.Global,
        TenantId = "tenant",
        ClientId = "client",
        ClientSecret = "super-secret-value",
        Endpoints = CloudPresets.Global,
        MaxRows = maxRows,
        MaxRowsCeiling = ceiling,
        DefaultVia = via,
        DefaultWorkspace = defaultWorkspace,
        DefaultDataset = defaultDataset,
    };

    public static QueryResult Xmla(int rows = 1) => new()
    {
        Columns = [new ColumnInfo("[x]", "Int64")],
        Rows = [.. Enumerable.Range(0, rows).Select(i => new object?[] { (long)i })],
        Path = ResultPaths.Xmla,
    };

    /// <summary>A handler serving one workspace "Sales" with one dataset "Model" and an executeQueries response.</summary>
    public static FakeHandler PowerBi(string executeQueriesJson = """{"results":[{"tables":[{"rows":[{"[x]":1}]}]}]}""",
        string workspaces = $$"""{"value":[{"id":"{{WorkspaceId}}","name":"Sales","isOnDedicatedCapacity":true}]}""") =>
        new((req, _) =>
        {
            var url = req.RequestUri!.AbsolutePath;
            if (url.EndsWith("/groups", StringComparison.Ordinal)) return FakeHandler.Json(HttpStatusCode.OK, workspaces);
            if (url.EndsWith("/datasets", StringComparison.Ordinal))
                return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"id":"{{DatasetId}}","name":"Model","configuredBy":"a@b.c"}]}""");
            if (url.EndsWith("/executeQueries", StringComparison.Ordinal)) return FakeHandler.Json(HttpStatusCode.OK, executeQueriesJson);
            return FakeHandler.Json(HttpStatusCode.NotFound, "{}");
        });

    public static PowerBiRestClient Rest(FakeHandler handler, ResolvedConfig? config = null, SecretScrubber? scrubber = null) =>
        new(new HttpClient(handler), config ?? Config(), new FakeTokenProvider(), scrubber ?? new SecretScrubber(), (_, _) => Task.CompletedTask);
}
