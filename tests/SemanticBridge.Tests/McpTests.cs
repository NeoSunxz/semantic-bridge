using System.Net;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SemanticBridge.Cli;
using SemanticBridge.Cli.Mcp;
using SemanticBridge.Core.Results;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Tests;

public sealed class McpToolTests
{
    private static string Text(CallToolResult r) => ((TextContentBlock)Assert.Single(r.Content)).Text;

    [Fact]
    public async Task ListsExactlyTheFiveReadTools()
    {
        var (tools, _) = McpTestData.Tools();
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var listed = await mcp.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["get_model_schema", "get_refresh_status", "list_datasets", "list_workspaces", "run_dax"], listed.Select(t => t.Name).Order().ToArray());
        Assert.All(listed, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        var runDax = listed.Single(t => t.Name == "run_dax");
        var required = runDax.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("dataset", required);
        Assert.Contains("dax", required);
        Assert.DoesNotContain("workspace", required);
    }

    [Fact]
    public async Task RunDaxReturnsCompactJsonWithTruncationFlagAndNote()
    {
        var (tools, xmla) = McpTestData.Tools();
        xmla.OnQuery = () => new QueryResult
        {
            Columns = [new ColumnInfo("Sales[Amount]", "Int64")],
            Rows = [new object?[] { 1L }, new object?[] { 2L }],
            Truncated = true,
            TruncationReason = "Result exceeds max_rows (2); more rows exist.",
            Path = ResultPaths.Xmla,
            ElapsedMs = 7,
        };
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var result = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dataset"] = "model", ["dax"] = "EVALUATE Sales", ["max_rows"] = 2 }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(Text(result));
        var root = doc.RootElement;
        Assert.Equal("Sales[Amount]", root.GetProperty("columns")[0].GetProperty("name").GetString());
        Assert.Equal(2, root.GetProperty("row_count").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal("xmla", root.GetProperty("path").GetString());
        Assert.Equal(7, root.GetProperty("elapsed_ms").GetInt64());
        Assert.True(root.TryGetProperty("warnings", out _));
        Assert.Contains("aggregation", root.GetProperty("note").GetString());
        Assert.Equal(2, xmla.LastMaxRows);
        Assert.DoesNotContain("\n", Text(result));
    }

    [Fact]
    public async Task UntruncatedResultHasNoNote()
    {
        var (tools, xmla) = McpTestData.Tools();
        xmla.OnQuery = () => TestData.Xmla(3);
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var result = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dataset"] = "model", ["dax"] = "EVALUATE x" }, cancellationToken: TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(Text(result));
        Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("note", out _));
    }

    [Fact]
    public async Task ToolErrorIsReturnedAsIsErrorAndScrubbed()
    {
        var (tools, xmla) = McpTestData.Tools();
        xmla.OnQuery = () => throw new BridgeException(ErrorKind.Dax, "Query failed: password=super-secret-value; token super-secret-value rejected");
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var result = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dataset"] = "model", ["dax"] = "EVALUATE x" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        var text = Text(result);
        Assert.Contains("Query failed", text);
        Assert.DoesNotContain("super-secret-value", text);
    }

    [Fact]
    public async Task UnexpectedExceptionIsAlsoScrubbedToolError()
    {
        var (tools, xmla) = McpTestData.Tools();
        xmla.OnQuery = () => throw new InvalidOperationException("boom super-secret-value");
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var result = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dataset"] = "model", ["dax"] = "EVALUATE x" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.DoesNotContain("super-secret-value", Text(result));
    }

    [Fact]
    public async Task NonDaxAndBadViaAreToolErrors()
    {
        var (tools, _) = McpTestData.Tools();
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var sql = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dataset"] = "model", ["dax"] = "SELECT 1" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(sql.IsError);
        var via = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dataset"] = "model", ["dax"] = "EVALUATE x", ["via"] = "bogus" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(via.IsError);
        Assert.DoesNotContain("bogus", Text(via));
    }

    [Fact]
    public async Task ListToolsUseTheDefaultWorkspaceAndSameServiceAsTheCli()
    {
        var (tools, _) = McpTestData.Tools();
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var ws = await mcp.Client.CallToolAsync("list_workspaces", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("\"name\":\"Sales\"", Text(ws));
        var ds = await mcp.Client.CallToolAsync("list_datasets", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("\"name\":\"Model\"", Text(ds));
    }

    [Fact]
    public async Task GetModelSchemaPassesTheTableFilter()
    {
        var (tools, xmla) = McpTestData.Tools();
        xmla.OnSchema = () => new SemanticBridge.Core.Xmla.ModelSchema { Tables = [], Relationships = [] };
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var result = await mcp.Client.CallToolAsync("get_model_schema", new Dictionary<string, object?> { ["dataset"] = "model", ["table"] = "Sales" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, result.IsError);
        Assert.Contains("\"tables\":[]", Text(result));
    }
}

public sealed class McpToolExtraTests
{
    private static string Text(CallToolResult r) => ((TextContentBlock)Assert.Single(r.Content)).Text;

    [Fact]
    public async Task BackendTimeoutWithoutClientCancellationIsAScrubbedToolError()
    {
        var (tools, xmla) = McpTestData.Tools();
        xmla.OnQuery = () => throw new TaskCanceledException("request timed out super-secret-value");
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var result = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dataset"] = "model", ["dax"] = "EVALUATE x" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("timed out", Text(result));
        Assert.DoesNotContain("super-secret-value", Text(result));
    }

    [Fact]
    public async Task MaxRowsAboveTheCeilingIsCappedAndReportedInWarnings()
    {
        var (tools, xmla) = McpTestData.Tools(TestData.Config(defaultWorkspace: "Sales", ceiling: 50));
        xmla.OnQuery = () => TestData.Xmla(3);
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var result = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dataset"] = "model", ["dax"] = "EVALUATE x", ["max_rows"] = 500 }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(Text(result));
        var warning = Assert.Single(doc.RootElement.GetProperty("warnings").EnumerateArray()).GetString();
        Assert.Contains("max_rows_ceiling (50)", warning);
        Assert.Equal(50, xmla.LastMaxRows);
    }

    [Fact]
    public async Task MaxRowsBelowOneIsAToolErrorWithoutCliFlagWording()
    {
        var (tools, _) = McpTestData.Tools();
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var result = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dataset"] = "model", ["dax"] = "EVALUATE x", ["max_rows"] = 0 }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("max rows must be at least 1", Text(result));
        Assert.DoesNotContain("--max-rows", Text(result));
    }

    [Fact]
    public async Task ViaDefaultsToAutoAndUsesXmla()
    {
        var (tools, xmla) = McpTestData.Tools();
        xmla.OnQuery = () => TestData.Xmla(1);
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var result = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dataset"] = "model", ["dax"] = "EVALUATE x" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(1, xmla.QueryCalls);
    }

    [Fact]
    public async Task InvalidViaIsAToolErrorListingTheAcceptedValues()
    {
        var (tools, xmla) = McpTestData.Tools();
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var result = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dataset"] = "model", ["dax"] = "EVALUATE x", ["via"] = "odata" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("auto, xmla or rest", Text(result));
        Assert.Equal(0, xmla.QueryCalls);
    }
}

public sealed class McpToolRegistryTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void ReadToolsAreAlwaysThereAndWriteToolsFollowTheGate(bool allowWrite, bool http, bool allowHttpWrite)
    {
        var (tools, _) = McpTestData.Tools();
        var set = McpToolRegistry.Create(tools, new WriteGate(allowWrite, http, allowHttpWrite), TextWriter.Null);
        Assert.Equal(allowWrite && (!http || allowHttpWrite) ? 7 : 5, set.Count);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void ClosedGateIsLoggedOnlyOverHttp(bool allowWrite, bool http, bool allowHttpWrite, bool expectLog)
    {
        var (tools, _) = McpTestData.Tools();
        var log = new StringWriter();
        McpToolRegistry.Create(tools, new WriteGate(allowWrite, http, allowHttpWrite), log);
        Assert.Equal(expectLog, log.ToString().Length > 0);
    }

    [Fact]
    public void DisabledOverHttpLogsWhy()
    {
        var (tools, _) = McpTestData.Tools();
        var log = new StringWriter();
        McpToolRegistry.Create(tools, new WriteGate(true, true, false), log);
        Assert.Contains("--allow-http-write", log.ToString());
        Assert.Contains("HTTP", log.ToString());
    }
}

public sealed class McpHttpTests
{
    private static async Task<(Microsoft.AspNetCore.Builder.WebApplication App, Uri Endpoint, StringWriter Log)> StartAsync(string? token, string host = "127.0.0.1", bool noAuth = false)
    {
        var (tools, _) = McpTestData.Tools();
        var log = new StringWriter();
        var app = McpHost.BuildHttp(McpTestData.ReadTools(tools), new HttpOptions(host, 0, token, noAuth), log);
        await app.StartAsync(TestContext.Current.CancellationToken);
        // A wildcard bind is reached through loopback; keep the real port.
        var port = new Uri(app.Urls.First()).Port;
        return (app, host.Contains(':') ? new Uri(app.Urls.First() + "/mcp") : new Uri($"http://127.0.0.1:{port}/mcp"), log);
    }

    private static HttpRequestMessage Initialize(Uri endpoint, string? bearer)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (bearer is not null) req.Headers.Authorization = new("Bearer", bearer);
        return req;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    [InlineData("")]
    public async Task MissingOrWrongTokenGets401(string? bearer)
    {
        var (app, endpoint, _) = await StartAsync("right-token");
        await using var _ = app;
        using var http = new HttpClient();
        using var resp = await http.SendAsync(Initialize(endpoint, bearer), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task CorrectTokenAllowsInitializeAndListsTools()
    {
        var (app, endpoint, log) = await StartAsync("right-token-0123456789");
        await using var _ = app;
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer right-token-0123456789" },
        }), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(5, (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Count);
        Assert.DoesNotContain("WARNING", log.ToString());
    }

    [Fact]
    public async Task WithoutTokenServerWorksAndWarnsOnStderr()
    {
        var (app, endpoint, log) = await StartAsync(null);
        await using var _ = app;
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = endpoint }), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(5, (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Count);
        Assert.Contains("SBRIDGE_HTTP_TOKEN", log.ToString());
        Assert.Contains("WARNING", log.ToString());
    }

    [Fact]
    public async Task ForeignOriginGets403EvenWithTheRightToken()
    {
        var (app, endpoint, _) = await StartAsync("right-token");
        await using var _ = app;
        using var http = new HttpClient();
        var req = Initialize(endpoint, "right-token");
        req.Headers.Add("Origin", "http://evil.example");
        using var resp = await http.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task LoopbackOriginIsAllowed()
    {
        var (app, endpoint, _) = await StartAsync(null);
        await using var _ = app;
        using var http = new HttpClient();
        var req = Initialize(endpoint, null);
        req.Headers.Add("Origin", "http://localhost:3000");
        using var resp = await http.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task ForeignHostHeaderOnLoopbackBindGets403()
    {
        var (app, endpoint, _) = await StartAsync(null);
        await using var _ = app;
        using var http = new HttpClient();
        var req = Initialize(endpoint, null);
        req.Headers.Host = "evil.example";
        using var resp = await http.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task OnNonLoopbackBindOnlyAMismatchedOriginIsRejected()
    {
        var (app, endpoint, _) = await StartAsync("right-token", host: "0.0.0.0");
        await using var _ = app;
        using var http = new HttpClient();
        var mismatched = Initialize(endpoint, "right-token");
        mismatched.Headers.Add("Origin", "http://evil.example");
        using (var resp = await http.SendAsync(mismatched, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var anyHost = Initialize(endpoint, "right-token");
        anyHost.Headers.Host = "mcp.internal.example";
        using (var resp = await http.SendAsync(anyHost, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var matching = Initialize(endpoint, "right-token");
        matching.Headers.Host = "mcp.internal.example";
        matching.Headers.Add("Origin", "https://mcp.internal.example");
        using (var resp = await http.SendAsync(matching, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task NonLoopbackWithoutTokenIsRefusedUnlessNoAuth()
    {
        var ex = await Assert.ThrowsAsync<BridgeException>(() => StartAsync(null, host: "0.0.0.0"));
        Assert.Equal(ErrorKind.Usage, ex.Kind);
        Assert.Contains("--no-auth", ex.Message);

        var (app, _, log) = await StartAsync(null, host: "0.0.0.0", noAuth: true);
        await using var _ = app;
        Assert.Contains("WARNING", log.ToString());
        Assert.Contains("0.0.0.0", log.ToString());
        Assert.Contains("non-loopback", log.ToString());
    }

    [Fact]
    public async Task ShortTokenWarnsWithoutLeakingItAndSurroundingWhitespaceIsIgnored()
    {
        var (app, endpoint, log) = await StartAsync("  short  ");
        await using var _ = app;
        Assert.Contains("shorter than 16", log.ToString());
        Assert.DoesNotContain("short", log.ToString().Replace("shorter", string.Empty));
        using var http = new HttpClient();
        using var ok = await http.SendAsync(Initialize(endpoint, "short"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var bad = await http.SendAsync(Initialize(endpoint, "  "), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }

    [Fact]
    public async Task LongTokenDoesNotWarn()
    {
        var (app, _, log) = await StartAsync("a-sufficiently-long-token");
        await using var _ = app;
        Assert.DoesNotContain("shorter than", log.ToString());
    }

    [Fact]
    public async Task WhitespaceOnlyTokenCountsAsUnset()
    {
        var (app, _, log) = await StartAsync("   ");
        await using var _ = app;
        Assert.Contains("is not set", log.ToString());
    }

    [Fact]
    public async Task OversizedBodyIsRejected()
    {
        var (app, endpoint, _) = await StartAsync(null);
        await using var _ = app;
        using var http = new HttpClient();
        var req = Initialize(endpoint, null);
        req.Content = new StringContent(new string('x', 1_100_000), System.Text.Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
    }

    [Fact]
    public async Task IPv6HostIsBracketedInTheListenUrl()
    {
        var (app, _, _) = await StartAsync(null, host: "::1");
        await using var _ = app;
        Assert.All(app.Urls, u => Assert.StartsWith("http://[::1]:", u));
    }

    [Fact]
    public async Task BindsToLoopbackOnly()
    {
        var (app, _, _) = await StartAsync(null);
        await using var _ = app;
        Assert.All(app.Urls, u => Assert.StartsWith("http://127.0.0.1:", u));
    }
}

public sealed class McpCliTests
{
    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static Func<string, string?> Env(params (string, string)[] extra)
    {
        var map = new Dictionary<string, string>
        {
            ["SBRIDGE_TENANT_ID"] = "00000000-0000-0000-0000-000000000001",
            ["SBRIDGE_CLIENT_ID"] = "00000000-0000-0000-0000-000000000002",
            ["SBRIDGE_CLIENT_SECRET"] = "fake-secret-value-xyz",
            ["HOME"] = Path.GetTempPath(),
            ["APPDATA"] = Path.GetTempPath(),
        };
        foreach (var (k, v) in extra) map[k] = v;
        return n => map.GetValueOrDefault(n);
    }

    [Fact]
    public async Task NonLoopbackHttpWithoutTokenExitsWithUsageError()
    {
        var err = new StringWriter();
        var code = await CliApp.RunAsync(["mcp", "--http", "--host", "0.0.0.0", "--port", FreePort().ToString()], new StringWriter(), err, new StringReader(""), Env(), () => false, CancellationToken.None);
        Assert.Equal(CliApp.ExitUsage, code);
        Assert.Contains("SBRIDGE_HTTP_TOKEN", err.ToString());
        Assert.Contains("--no-auth", err.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NoAuthFlagOrStrictEnvAllowsPublicBindWithLoudWarning(bool useFlag)
    {
        var port = FreePort();
        var args = useFlag ? new[] { "mcp", "--http", "--host", "0.0.0.0", "--port", port.ToString(), "--no-auth" } : ["mcp", "--http", "--host", "0.0.0.0", "--port", port.ToString()];
        var env = useFlag ? Env() : Env(("SBRIDGE_HTTP_NO_AUTH", "1"));
        var (code, err) = await RunUntilListening(args, env, _ => Task.CompletedTask);
        Assert.Equal(CliApp.ExitOk, code);
        Assert.Contains("WARNING", err);
        Assert.Contains("0.0.0.0", err);
    }

    [Fact]
    public async Task NoAuthEnvOtherThanOneDoesNotCount()
    {
        var err = new StringWriter();
        var code = await CliApp.RunAsync(["mcp", "--http", "--host", "0.0.0.0", "--port", FreePort().ToString()], new StringWriter(), err, new StringReader(""), Env(("SBRIDGE_HTTP_NO_AUTH", "true")), () => false, CancellationToken.None);
        Assert.Equal(CliApp.ExitUsage, code);
    }

    [Fact]
    public async Task HttpTokenNeverAppearsInThe401BodyOrLogs()
    {
        const string token = "tok-very-secret-9f3a1c";
        var port = FreePort();
        string body = "";
        HttpStatusCode status = 0;
        var (_, err) = await RunUntilListening(["mcp", "--http", "--port", port.ToString()], Env(("SBRIDGE_HTTP_TOKEN", token)), async _ =>
        {
            using var http = new HttpClient();
            var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new("Bearer", token + "x");
            using var resp = await http.SendAsync(req);
            status = resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync();
        });
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.DoesNotContain(token, body);
        Assert.DoesNotContain(token, err);
    }

    private static async Task<(int Code, string Err)> RunUntilListening(string[] args, Func<string, string?> env, Func<string, Task> whileRunning)
    {
        var sink = new StringWriter();
        var err = TextWriter.Synchronized(sink);
        string Snapshot() { lock (err) return sink.ToString(); }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CliApp.RunAsync(args, new StringWriter(), err, new StringReader(""), env, () => false, cts.Token);
        while (!Snapshot().Contains("listening on") && !run.IsCompleted) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted, "server exited early: " + Snapshot());
        await whileRunning(Snapshot());
        await cts.CancelAsync();
        var code = await run;
        return (code, Snapshot());
    }
}

public sealed class McpStdioProcessTests
{
    private const string FakeSecret = "fake-secret-value-xyz";

    [Fact]
    public async Task StdinEofStillDeliversAllResponsesAndNothingElseOnStdout()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "sbridge.dll");
        Assert.True(File.Exists(dll), $"sbridge.dll was not found next to the test assembly ({dll}); build the SemanticBridge project so it is copied to the test output.");
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("mcp");
        psi.Environment["SBRIDGE_TENANT_ID"] = "00000000-0000-0000-0000-000000000001";
        psi.Environment["SBRIDGE_CLIENT_ID"] = "00000000-0000-0000-0000-000000000002";
        psi.Environment["SBRIDGE_CLIENT_SECRET"] = FakeSecret;
        using var proc = System.Diagnostics.Process.Start(psi)!;
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""".AsMemory(), ct);
            await proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""".AsMemory(), ct);
            await proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""".AsMemory(), ct);
            proc.StandardInput.Close();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
            await proc.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.Equal(0, proc.ExitCode);
            Assert.DoesNotContain(FakeSecret, stdout);
            Assert.DoesNotContain(FakeSecret, stderr);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.True(lines.Length == 2, $"stdout lines: {lines.Length}; stderr: {stderr}");
            Assert.All(lines, l => Assert.Equal("2.0", JsonDocument.Parse(l).RootElement.GetProperty("jsonrpc").GetString()));
            Assert.Contains(lines, l => l.Contains("\"id\":1"));
            Assert.Contains(lines, l => l.Contains("run_dax"));
        }
        finally
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
    }
}
