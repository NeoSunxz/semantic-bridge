using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using SemanticBridge.Cli;
using SemanticBridge.Cli.Mcp;
using SemanticBridge.Cli.Output;
using SemanticBridge.Core;
using SemanticBridge.Core.Config;
using SemanticBridge.Core.Diagnostics;
using SemanticBridge.Core.Results;
using SemanticBridge.Core.Safety;
using SemanticBridge.Core.Xmla;

namespace SemanticBridge.Tests;

internal static class FixesData
{
    public static ResolvedConfig WriteConfigWithDefaults() => new()
    {
        ProfileName = "test",
        ProfileSource = "test",
        Cloud = Cloud.Global,
        TenantId = "tenant",
        ClientId = "client",
        ClientSecret = "super-secret-value",
        Endpoints = CloudPresets.Global,
        AllowWrite = true,
        DefaultWorkspace = "Sales",
        DefaultDataset = "Model",
    };

    public static string Text(CallToolResult r) => ((TextContentBlock)Assert.Single(r.Content)).Text;
}

// ---- 1. explicit empty dataset/workspace ------------------------------------

public sealed class EmptyDatasetWorkspaceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbridge-empty-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void OnlyANullInputUsesTheDefault()
    {
        var cfg = FixesData.WriteConfigWithDefaults();
        Assert.Equal("Model", cfg.ResolveDatasetInput(null));
        Assert.Equal("Sales", cfg.ResolveWorkspaceInput(null));
        Assert.Equal("Other", cfg.ResolveDatasetInput("Other"));
        foreach (var empty in new[] { "", "   " })
        {
            var ds = Assert.Throws<BridgeException>(() => cfg.ResolveDatasetInput(empty));
            Assert.Equal(ErrorKind.Usage, ds.Kind);
            Assert.Equal("Dataset name or ID is empty.", ds.Message);
            var ws = Assert.Throws<BridgeException>(() => cfg.ResolveWorkspaceInput(empty));
            Assert.Equal(ErrorKind.Usage, ws.Kind);
            Assert.Equal("Workspace name or ID is empty.", ws.Message);
        }
    }

    [Fact]
    public void MissingWorkspaceMessageMentionsTheEnvVar()
    {
        var cfg = TestData.Config();
        var ex = Assert.Throws<BridgeException>(() => cfg.ResolveWorkspaceInput(null));
        Assert.Contains("set default_workspace (or SBRIDGE_WORKSPACE)", ex.Message);
    }

    [Theory]
    [InlineData("update_description", "")]
    [InlineData("update_description", "   ")]
    [InlineData("refresh_dataset", "")]
    [InlineData("refresh_dataset", "   ")]
    public async Task McpWriteToolsRejectAnEmptyDatasetEvenWithADefault(string tool, string dataset)
    {
        var writer = new FakeDescriptionWriter();
        var handler = WriteTestData.RefreshApi(["Completed"]);
        var gate = new WriteGate(true);
        var service = WriteTestData.Service(FixesData.WriteConfigWithDefaults(), handler, writer, gate);
        var tools = McpToolRegistry.Create(new McpTools(service, new SecretScrubber()), gate, TextWriter.Null);
        await using var mcp = await InProcessMcp.StartAsync(tools);
        var args = new Dictionary<string, object?> { ["dataset"] = dataset };
        if (tool == "update_description")
        {
            args["target_type"] = "table";
            args["target"] = "Sales";
            args["description"] = "text";
        }
        var result = await mcp.Client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("Dataset name or ID is empty.", FixesData.Text(result));
        Assert.Empty(handler.Requests);
        Assert.Equal(0, writer.ListCalls);
        Assert.Empty(writer.Applied);
    }

    [Theory]
    [InlineData("list_datasets", "workspace", "Workspace name or ID is empty.")]
    [InlineData("run_dax", "dataset", "Dataset name or ID is empty.")]
    [InlineData("run_dax", "workspace", "Workspace name or ID is empty.")]
    [InlineData("get_refresh_status", "dataset", "Dataset name or ID is empty.")]
    public async Task McpReadToolsRejectAnEmptyValueEvenWithADefault(string tool, string argument, string expected)
    {
        var cfg = TestData.Config(defaultWorkspace: "Sales", defaultDataset: "Model");
        var scrubber = new SecretScrubber();
        var handler = TestData.PowerBi();
        var xmla = new FakeXmla { OnQuery = () => TestData.Xmla() };
        var tools = new McpTools(new BridgeService(cfg, TestData.Rest(handler, cfg, scrubber), xmla), scrubber);
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var args = new Dictionary<string, object?> { [argument] = " " };
        if (tool == "run_dax") args["dax"] = "EVALUATE x";
        var result = await mcp.Client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains(expected, FixesData.Text(result));
        Assert.Equal(0, xmla.QueryCalls);
        Assert.Empty(handler.Requests);
    }

    private async Task<(int Code, string Out, string Err)> Cli(params string[] args)
    {
        var path = Path.Combine(_dir, "config.yaml");
        File.WriteAllText(path, "profiles:\n  p:\n    tenant_id: t\n    client_id: c\n    client_secret: s3cret-value\n    default_workspace: Sales\n    default_dataset: Model\n    allow_write: true\n");
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync([.. args, "--config", path], o, e, new StringReader(""), n => n is "HOME" or "APPDATA" ? _dir : null, () => true, CancellationToken.None,
            (cfg, gate) => WriteTestData.Service(cfg, WriteTestData.RefreshApi(["Completed"]), new FakeDescriptionWriter(), gate));
        return (code, o.ToString(), e.ToString());
    }

    [Theory]
    [InlineData("query", "EVALUATE x", "-d", "")]
    [InlineData("query", "EVALUATE x", "-d", "  ")]
    [InlineData("schema", "-d", "")]
    [InlineData("refresh-status", "-d", "")]
    [InlineData("refresh", "-d", "", "--yes")]
    [InlineData("describe", "--table", "Sales", "text", "-d", "", "--yes")]
    public async Task CliEmptyDatasetExitsTwo(params string[] args)
    {
        var r = await Cli(args);
        Assert.Equal(2, r.Code);
        Assert.Contains("Dataset name or ID is empty.", r.Err);
        Assert.DoesNotContain("s3cret-value", r.Out + r.Err);
    }

    [Theory]
    [InlineData("datasets", "-w", "")]
    [InlineData("refresh-status", "-w", "  ")]
    [InlineData("config", "test", "-w", "")]
    public async Task CliEmptyWorkspaceExitsTwo(params string[] args)
    {
        var r = await Cli(args);
        Assert.Equal(2, r.Code);
        Assert.Contains("Workspace name or ID is empty.", r.Err);
    }
}

// ---- 2. ErrorHints -----------------------------------------------------------

public sealed class ErrorHintStructuredMatchingTests
{
    [Theory]
    [InlineData("The query refers to [m401] which is not defined.")]
    [InlineData("Column 'm403' cannot be found.")]
    [InlineData("Function XMLA_read is unknown.")]
    [InlineData("Query failed (RootActivityId: 7f3a4012-4010-4401-9401-0c0e4017401a).")]
    public void DaxErrorsWithLookalikeTextGetNoHint(string message)
    {
        Assert.Null(ErrorHints.For(message));
        Assert.Null(ErrorHints.For(new BridgeException(ErrorKind.Dax, message), message));
        Assert.Null(ErrorHints.For(new BridgeException(ErrorKind.Rest, message), message));
        Assert.Null(ErrorHints.For(new BridgeException(ErrorKind.Xmla, message), message));
    }

    [Fact]
    public void DaxErrorKindNeverGetsAHintEvenWithTheWordUnauthorized()
    {
        var ex = new BridgeException(ErrorKind.Dax, "Measure [Unauthorized] and [Forbidden] are not defined.");
        Assert.Null(ErrorHints.For(ex, ex.Message));
    }

    [Fact]
    public void RestStatusCodesGetTheirHints()
    {
        var unauthorized = new BridgeException(ErrorKind.Rest, "Power BI REST API returned HTTP 401 Unauthorized.", httpStatus: 401);
        Assert.Contains("token was rejected", ErrorHints.For(unauthorized, unauthorized.Message));
        var forbidden = new BridgeException(ErrorKind.Rest, "Power BI REST API returned HTTP 403 Forbidden.", httpStatus: 403);
        Assert.Contains("Access denied", ErrorHints.For(forbidden, forbidden.Message));
        // The status alone is enough, whatever the body says.
        Assert.Contains("Access denied", ErrorHints.For("denied", ErrorKind.Rest, 403));
    }

    [Fact]
    public async Task TheRestClientSetsTheStatusOnItsErrors()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.Forbidden, """{"error":{"code":"PowerBINotAuthorizedException"}}"""));
        var ex = await Assert.ThrowsAsync<BridgeException>(() => TestData.Rest(handler).GetWorkspacesAsync(CancellationToken.None));
        Assert.Equal(403, ex.HttpStatus);
        Assert.Contains("Access denied", ErrorHints.For(ex, ex.Message));
    }

    [Theory]
    [InlineData("XMLA connection failed: the XMLA endpoint is disabled for this capacity")]
    [InlineData("XMLA read/write is disabled")]
    [InlineData("XMLA is not enabled for this workspace")]
    public void RealXmlaUnavailableTextsGetTheHintForXmlaErrors(string message)
    {
        Assert.Contains("XMLA endpoint is not available", ErrorHints.For(message, ErrorKind.Xmla));
        Assert.Null(ErrorHints.For(message, ErrorKind.Dax));
    }

    [Fact]
    public void XmlaErrorsWithoutTheKeywordsGetNoXmlaHint()
    {
        Assert.Null(ErrorHints.For("XMLA connection failed: the server closed the connection", ErrorKind.Xmla));
        // Keywords alone, on an error that is not an XMLA error, do not count.
        Assert.Null(ErrorHints.For("XMLA is not enabled for this workspace", ErrorKind.Rest));
    }

    [Fact]
    public void AadstsCodesAndMicrosoftPhrasesStillGetHints()
    {
        Assert.Contains("secret is invalid", ErrorHints.For("AADSTS7000215: Invalid client secret provided."));
        Assert.Contains("Azure AD rejected", ErrorHints.For("AADSTS50126: Error validating credentials."));
        Assert.Contains("token was rejected", ErrorHints.For("The remote server returned an error: (401) Unauthorized."));
        Assert.Contains("Access denied", ErrorHints.For("The remote server returned an error: (403) Forbidden."));
        Assert.Contains("could not be resolved", ErrorHints.For("No such host is known"));
    }
}

// ---- 3. integer range --------------------------------------------------------

public sealed class IntegerRangeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbridge-int-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement;

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private const string IntSchema = """{"type":"object","properties":{"max_rows":{"type":["integer","null"]}}}""";

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("99999999999")]
    public void IntegerOutsideInt32IsAWrongValue(string value)
    {
        var problem = ArgumentCheckedTool.Check(Schema(IntSchema), Args($$"""{"max_rows":{{value}}}"""));
        Assert.NotNull(problem);
        Assert.Contains("Wrong type for argument(s): max_rows (expected integer", problem);
        Assert.Contains("32-bit", problem);
        Assert.DoesNotContain(value, problem);
    }

    [Fact]
    public void Int32BoundsAndNullPass()
    {
        Assert.Null(ArgumentCheckedTool.Check(Schema(IntSchema), Args("""{"max_rows":2147483647}""")));
        Assert.Null(ArgumentCheckedTool.Check(Schema(IntSchema), Args("""{"max_rows":-2147483648}""")));
        Assert.Null(ArgumentCheckedTool.Check(Schema(IntSchema), Args("""{"max_rows":null}""")));
    }

    [Fact]
    public void ANonNumberStillSaysExpectedInteger()
    {
        var problem = ArgumentCheckedTool.Check(Schema(IntSchema), Args("""{"max_rows":"ten"}"""));
        Assert.Equal("Wrong type for argument(s): max_rows (expected integer). Expected arguments: max_rows.", problem);
    }

    [Theory]
    [InlineData("run_dax", "max_rows")]
    [InlineData("get_refresh_status", "top")]
    public async Task McpToolsAnswerWithAClearMessage(string tool, string argument)
    {
        var (tools, _) = McpTestData.Tools(TestData.Config(defaultWorkspace: "Sales", defaultDataset: "Model"));
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var args = new Dictionary<string, object?> { [argument] = 2147483648L };
        if (tool == "run_dax") args["dax"] = "EVALUATE x";
        var result = await mcp.Client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        var text = FixesData.Text(result);
        Assert.Contains($"Wrong type for argument(s): {argument} (expected integer", text);
        Assert.DoesNotContain("An error occurred invoking", text);
    }

    [Fact]
    public async Task CliNumberTooLargeIsOutOfRangeNotMalformed()
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync(["query", "-w", "x", "-d", "y", "--max-rows", "99999999999", "EVALUATE x"], o, e, new StringReader(""),
            n => n is "HOME" or "APPDATA" ? _dir : null, () => false, CancellationToken.None);
        Assert.Equal(2, code);
        Assert.Contains("Invalid value '99999999999' for --max-rows: the number is out of range.", e.ToString());
        Assert.DoesNotContain("whole number", e.ToString());
    }
}

// ---- 4. several EVALUATE over REST --------------------------------------------

public sealed class RestMultipleResultSetTests
{
    [Fact]
    public async Task Http400WithTheResultsetCodeBecomesATruncatedEmptyResult()
    {
        var rest = TestData.Rest(new FakeHandler((_, _) =>
            FakeHandler.Json(HttpStatusCode.BadRequest, """{"error":{"code":"DaxResultsetCountNotSupported","message":"Only one resultset is supported."}}""")));
        var result = await rest.ExecuteQueryAsync(TestData.WorkspaceId, TestData.DatasetId, "EVALUATE a EVALUATE b", 100, CancellationToken.None);
        Assert.True(result.Truncated);
        Assert.Equal(ResultSets.MultipleNotice, result.TruncationReason);
        Assert.Empty(result.Rows);
        Assert.Empty(result.Columns);
        Assert.Equal(ResultPaths.Rest, result.Path);
    }

    [Fact]
    public async Task ABadRequestThatOnlyQuotesTheCodeStaysAnError()
    {
        var rest = TestData.Rest(new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.BadRequest,
            """{"error":{"code":"DaxQueryFailed","message":"Do not confuse this with DaxResultsetCountNotSupported."}}""")));
        var ex = await Assert.ThrowsAsync<BridgeException>(() => rest.ExecuteQueryAsync(TestData.WorkspaceId, TestData.DatasetId, "EVALUATE x", 100, CancellationToken.None));
        Assert.Equal(ErrorKind.Dax, ex.Kind);
        Assert.Equal("DaxQueryFailed", ex.Code);
    }

    [Fact]
    public async Task OtherBadRequestsStayErrors()
    {
        var rest = TestData.Rest(new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.BadRequest, """{"error":{"code":"DaxQueryFailed","message":"bad"}}""")));
        var ex = await Assert.ThrowsAsync<BridgeException>(() => rest.ExecuteQueryAsync(TestData.WorkspaceId, TestData.DatasetId, "EVALUATE x", 100, CancellationToken.None));
        Assert.Equal(ErrorKind.Dax, ex.Kind);
    }

    [Fact]
    public void Http200ErrorWithTheCodeKeepsPartialRowsAndAddsTheNotice()
    {
        var result = TestData.Rest(TestData.PowerBi()).ParseQueryResult(
            """{"results":[{"error":{"code":"DaxResultsetCountNotSupported","message":"m"},"tables":[{"rows":[{"[x]":1}]}]}]}""", 100, 1);
        Assert.True(result.Truncated);
        Assert.Equal(ResultSets.MultipleNotice, result.TruncationReason);
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Http200ErrorWithTheCodeAndNoRowsIsEmptyAndTruncated()
    {
        var result = TestData.Rest(TestData.PowerBi()).ParseQueryResult(
            """{"results":[{"error":{"code":"DaxResultsetCountNotSupported","message":"m"}}]}""", 100, 1);
        Assert.True(result.Truncated);
        Assert.Equal(ResultSets.MultipleNotice, result.TruncationReason);
        Assert.Empty(result.Rows);
        Assert.Empty(result.Columns);
    }
}

// ---- 5. credentials in endpoint URLs -------------------------------------------

public sealed class EndpointCredentialTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbridge-endpoints-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private BridgeException Load(string overrideLine)
    {
        var keys = new Dictionary<string, string>
        {
            ["authority_host"] = "https://login.example",
            ["token_scope"] = "https://scope.example/.default",
            ["rest_base_url"] = "https://api.example/v1.0/myorg",
            ["xmla_base"] = "powerbi://api.example/v1.0/myorg/{workspace}",
        };
        var split = overrideLine.Trim().Split(": ", 2);
        keys[split[0]] = split[1];
        var path = Path.Combine(_dir, "config.yaml");
        File.WriteAllText(path, "profiles:\n  p:\n    tenant_id: t\n    client_id: c\n    client_secret: s\n    cloud: custom\n"
            + string.Concat(keys.Select(k => $"    {k.Key}: \"{k.Value}\"\n")));
        return Assert.Throws<BridgeException>(() => ConfigLoader.Load(new ConfigLoadOptions(path), _ => null));
    }

    [Theory]
    [InlineData("    authority_host: https://user:pw-leak@login.example", "authority_host")]
    [InlineData("    authority_host: https://user@login.example", "authority_host")]
    [InlineData("    rest_base_url: https://user:pw-leak@api.example/v1.0/myorg", "rest_base_url")]
    [InlineData("    token_scope: https://user:pw-leak@scope.example/.default", "token_scope")]
    [InlineData("    xmla_base: powerbi://user:pw-leak@api.example/v1.0/myorg/{workspace}", "xmla_base")]
    [InlineData("    xmla_base: powerbi://user:pw/leak@api.example/v1.0/myorg/{workspace}", "xmla_base")]
    [InlineData("    xmla_base: powerbi://user@api.example/v1.0/myorg/{workspace}", "xmla_base")]
    [InlineData("    authority_host: https://user:pw/leak@api.example", "authority_host")]
    [InlineData("    xmla_base: powerbi://api.example/v1.0/myorg/{workspace};Password=pw-leak", "xmla_base")]
    public void EmbeddedCredentialsAreAConfigErrorThatNamesOnlyTheKey(string line, string key)
    {
        var ex = Load(line);
        Assert.Equal(ErrorKind.Config, ex.Kind);
        Assert.True(ex.IsUsageError);
        Assert.Contains(key, ex.Message);
        Assert.DoesNotContain("pw-leak", ex.Message);
        Assert.DoesNotContain("leak", ex.Message);
        Assert.DoesNotContain("api.example", ex.Message);
    }

    [Theory]
    [InlineData("    rest_base_url: https://api.example/v1.0/myorg/a@b")]
    [InlineData("    authority_host: https://login.example/a@b")]
    [InlineData("    token_scope: https://scope.example/path@x/.default")]
    [InlineData("    xmla_base: powerbi://api.example/v1.0/myorg/{workspace}?x=a@b")]
    public void AtSignOutsideTheAuthorityIsAccepted(string line)
    {
        var keys = new Dictionary<string, string>
        {
            ["authority_host"] = "https://login.example",
            ["token_scope"] = "https://scope.example/.default",
            ["rest_base_url"] = "https://api.example/v1.0/myorg",
            ["xmla_base"] = "powerbi://api.example/v1.0/myorg/{workspace}",
        };
        var split = line.Trim().Split(": ", 2);
        keys[split[0]] = split[1];
        var path = Path.Combine(_dir, "at.yaml");
        File.WriteAllText(path, "profiles:\n  p:\n    tenant_id: t\n    client_id: c\n    cloud: custom\n" + string.Concat(keys.Select(k => $"    {k.Key}: \"{k.Value}\"\n")));
        Assert.Equal("p", ConfigLoader.Load(new ConfigLoadOptions(path), _ => null).ProfileName);
    }

    [Fact]
    public void PresetEndpointsStillLoad()
    {
        var path = Path.Combine(_dir, "ok.yaml");
        File.WriteAllText(path, "profiles:\n  p:\n    tenant_id: t\n    client_id: c\n    cloud: china\n");
        var cfg = ConfigLoader.Load(new ConfigLoadOptions(path), _ => null);
        Assert.Equal(CloudPresets.China.XmlaBase, cfg.Endpoints.XmlaBase);
    }
}

// ---- 6. small fixes -------------------------------------------------------------

public sealed class SmallFixesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbridge-small-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Config(string extra = "")
    {
        var path = Path.Combine(_dir, "config.yaml");
        File.WriteAllText(path, "profiles:\n  p:\n    tenant_id: t\n    client_id: c\n    client_secret: s3cret-value\n    default_workspace: Sales\n    allow_write: true\n" + extra);
        return path;
    }

    private async Task<(int Code, string Out, string Err, FakeDescriptionWriter Writer)> Cli(params string[] args)
    {
        var writer = new FakeDescriptionWriter();
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync([.. args, "--config", Config()], o, e, new StringReader(""), n => n is "HOME" or "APPDATA" ? _dir : null, () => true, CancellationToken.None,
            (cfg, gate) => WriteTestData.Service(cfg, WriteTestData.RefreshApi(["Completed"]), writer, gate));
        return (code, o.ToString(), e.ToString(), writer);
    }

    // a) REST error markup
    [Fact]
    public async Task RestErrorTextLosesTheEngineMarkup()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.BadRequest,
            """{"error":{"code":"DaxQueryFailed","message":"Column <oii>Sales[Nope]</oii> not found.\nTechnical Details:\nRootActivityId: abc-1"}}"""));
        var ex = await Assert.ThrowsAsync<BridgeException>(() => TestData.Rest(handler).ExecuteQueryAsync(TestData.WorkspaceId, TestData.DatasetId, "EVALUATE x", 10, CancellationToken.None));
        Assert.DoesNotContain("<oii>", ex.Message);
        Assert.DoesNotContain("Technical Details", ex.Message);
        Assert.Contains("Sales[Nope]", ex.Message);
        Assert.EndsWith("(RootActivityId: abc-1)", ex.Message);
    }

    [Fact]
    public void Http200RestErrorTextLosesTheEngineMarkup()
    {
        var ex = Assert.Throws<BridgeException>(() => TestData.Rest(TestData.PowerBi()).ParseQueryResult(
            """{"results":[{"error":{"code":"X","message":"bad <oii>y</oii>"}}]}""", 10, 1));
        Assert.DoesNotContain("<oii>", ex.Message);
    }

    // b) --via is case-insensitive
    [Theory]
    [InlineData("REST")]
    [InlineData("Rest")]
    public async Task CliViaIsCaseInsensitive(string via)
    {
        var r = await Cli("schema", "-w", "Sales", "-d", "Model", "--via", via);
        Assert.Equal(2, r.Code);
        Assert.Contains("executeQueries", r.Err); // the explicit-rest schema refusal, i.e. the value was accepted
        Assert.DoesNotContain("Invalid value", r.Err);
    }

    [Theory]
    [InlineData("XMLA")]
    [InlineData("Xmla")]
    public async Task CliViaMixedCaseRunsTheXmlaPath(string via)
    {
        var xmla = new FakeXmla { OnQuery = () => TestData.Xmla(2) };
        var handler = TestData.PowerBi();
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync(["query", "-w", "Sales", "-d", "Model", "--via", via, "EVALUATE x", "--config", Config()], o, e, new StringReader(""),
            n => n is "HOME" or "APPDATA" ? _dir : null, () => true, CancellationToken.None,
            (cfg, gate) => new BridgeService(cfg, TestData.Rest(handler, cfg), xmla));
        Assert.Equal(0, code);
        Assert.Equal(1, xmla.QueryCalls);
        Assert.DoesNotContain(handler.Requests, r => r.Url.EndsWith("/executeQueries", StringComparison.Ordinal));
        Assert.Contains("path: xmla", e.ToString());
    }

    [Fact]
    public async Task CliViaStillRejectsUnknownValues()
    {
        var r = await Cli("schema", "-w", "Sales", "-d", "Model", "--via", "sql");
        Assert.Equal(2, r.Code);
        Assert.Contains("Invalid value 'sql' for --via: expected auto, xmla or rest.", r.Err);
    }

    // d) RowCap reason
    [Fact]
    public void RowCapOffersRaisingTheLimitOnlyBelowTheCeiling()
    {
        var below = new RowCap(1, 100);
        below.Add([1]);
        below.Add([2]);
        Assert.Contains("raise the row limit", below.Reason);

        var atCeiling = new RowCap(1, 1);
        atCeiling.Add([1]);
        atCeiling.Add([2]);
        Assert.DoesNotContain("raise", atCeiling.Reason);
        Assert.Contains("max_rows_ceiling", atCeiling.Reason);
        Assert.Contains("Aggregate or filter", atCeiling.Reason);

        var unknown = new RowCap(1);
        unknown.Add([1]);
        unknown.Add([2]);
        Assert.Contains("raise the row limit", unknown.Reason);
    }

    [Fact]
    public async Task RestTruncationAtTheCeilingDropsTheRaiseSuggestion()
    {
        var handler = TestData.PowerBi("""{"results":[{"tables":[{"rows":[{"[x]":1},{"[x]":2}]}]}]}""");
        var cfg = TestData.Config(maxRows: 1, ceiling: 1);
        var result = await TestData.Rest(handler, cfg).ExecuteQueryAsync(TestData.WorkspaceId, TestData.DatasetId, "EVALUATE x", 1, CancellationToken.None);
        Assert.True(result.Truncated);
        Assert.DoesNotContain("raise", result.TruncationReason);
        Assert.Contains("Aggregate or filter", result.TruncationReason);
    }

    // e) table escapes
    [Fact]
    public void TableEscapesControlCharactersAndBidiOverrides()
    {
        var text = TableFormatter.Render(["h\tx"], [["a\tb\rc\nd\u0001e\u202Ef\u2066g\u007F"]]);
        var lines = text.TrimEnd().Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.Equal("h\\tx", lines[0]);
        Assert.Equal("a\\tb\\rc\\nd\\u{0001}e\\u{202E}f\\u{2066}g\\u{007F}", lines[2]);
        Assert.False(text.Contains('\u202E'));
    }

    [Fact]
    public void PlainTextIsUnchangedAndCsvKeepsRawCharacters()
    {
        Assert.Equal("héllo 世界", TableFormatter.Escape("héllo 世界"));
        Assert.Contains("a\tb", CsvFormatter.Render(["h"], [["a\tb"]]));
    }

    // f) measure in brackets
    [Theory]
    [InlineData("[Total]", "Total")]
    [InlineData("Total", "Total")]
    [InlineData("  [总销售额] ", "总销售额")]
    [InlineData("[[x]]", "[x]")]
    public void MeasureNamesDropOneSurroundingPairOfBrackets(string input, string expected)
    {
        Assert.Equal(expected, DescriptionTargets.Measure(input).Name);
    }

    [Fact]
    public async Task CliDescribeAcceptsABracketedMeasure()
    {
        var r = await Cli("describe", "--measure", "[Total]", "Changed", "-d", "Model", "--yes");
        Assert.Equal(0, r.Code);
        Assert.Equal("Changed", Assert.Single(Assert.Single(r.Writer.Applied)).New);
    }

    [Fact]
    public async Task McpUpdateDescriptionAcceptsABracketedMeasure()
    {
        var writer = new FakeDescriptionWriter();
        var gate = new WriteGate(true);
        var service = WriteTestData.Service(WriteTestData.WriteConfig(), WriteTestData.RefreshApi([]), writer, gate);
        await using var mcp = await InProcessMcp.StartAsync(McpToolRegistry.Create(new McpTools(service, new SecretScrubber()), gate, TextWriter.Null));
        var result = await mcp.Client.CallToolAsync("update_description",
            new Dictionary<string, object?> { ["dataset"] = "Model", ["target_type"] = "measure", ["target"] = "[Total]", ["description"] = "New" },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, result.IsError);
        Assert.Single(writer.Applied);
    }

    // g) describe without changes
    [Fact]
    public async Task DescribeWithNoChangesPrintsHeaderThenUnchangedAndNoTable()
    {
        var r = await Cli("describe", "--measure", "Total", "Same", "-d", "Model", "--yes");
        Assert.Equal(0, r.Code);
        var lines = r.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("Workspace 'Sales', dataset 'Model':", lines[0]);
        Assert.StartsWith("unchanged:", lines[1]);
        Assert.Equal("No changes to apply.", lines[2]);
        Assert.Equal(string.Empty, r.Out);
        Assert.Empty(r.Writer.Applied);
    }

    [Fact]
    public async Task DescribeJsonWithNoChangesKeepsItsShape()
    {
        var r = await Cli("describe", "--measure", "Total", "Same", "-d", "Model", "--yes", "-f", "json");
        using var doc = JsonDocument.Parse(r.Out);
        Assert.False(doc.RootElement.GetProperty("applied").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("changes").GetArrayLength());
        Assert.Equal(1, doc.RootElement.GetProperty("unchanged").GetArrayLength());
    }

    // h) quoting
    [Fact]
    public async Task DescribeEscapesQuotesBackslashesAndWhitespaceInOldNewLines()
    {
        var r = await Cli("describe", "--table", "Sales", "say \"hi\" \\ x\ty\nz", "-d", "Model", "--yes");
        Assert.Equal(0, r.Code);
        Assert.Contains("\"Old table text\" -> \"say \\\"hi\\\" \\\\ x\\ty\\nz\"", r.Err);
    }

    [Fact]
    public async Task DescribeEscapesControlCharactersLikeTableCells()
    {
        var r = await Cli("describe", "--table", "Sales", "a\u0001b\u202Ec", "-d", "Model", "--yes");
        Assert.Equal(0, r.Code);
        Assert.Contains("\"a\\u{0001}b\\u{202E}c\"", r.Err);
        Assert.False(r.Err.Contains('\u202E'));
    }

    // j) case-insensitive duplicate profiles
    [Fact]
    public void ProfilesDifferingOnlyInCaseAreAConfigError()
    {
        var path = Path.Combine(_dir, "dup.yaml");
        File.WriteAllText(path, "profiles:\n  a:\n    tenant_id: t\n    client_id: c\n  A:\n    tenant_id: t2\n    client_id: c2\n");
        var ex = Assert.Throws<BridgeException>(() => ConfigLoader.Load(new ConfigLoadOptions(path), n => n == ConfigLoader.EnvProfile ? "A" : null));
        Assert.Equal(ErrorKind.Config, ex.Kind);
        Assert.Contains("differ only in case", ex.Message);
    }

    [Fact]
    public void DistinctProfilesStillLoad()
    {
        var path = Path.Combine(_dir, "ok.yaml");
        File.WriteAllText(path, "profiles:\n  a:\n    tenant_id: t\n    client_id: c\n  b:\n    tenant_id: t2\n    client_id: c2\n");
        Assert.Equal("b", ConfigLoader.Load(new ConfigLoadOptions(path, "B"), _ => null).ProfileName);
    }

    // k) --timeout needs --wait
    [Fact]
    public async Task RefreshTimeoutWithoutWaitIsAUsageError()
    {
        var r = await Cli("refresh", "-d", "Model", "--timeout", "5", "--yes");
        Assert.Equal(2, r.Code);
        Assert.Contains("--timeout needs --wait.", r.Err);
        Assert.DoesNotContain("accepted", r.Out);
    }

    [Fact]
    public async Task RefreshWithoutTimeoutOrWithWaitStillWorks()
    {
        Assert.Equal(0, (await Cli("refresh", "-d", "Model", "--yes")).Code);
        Assert.Equal(0, (await Cli("refresh", "-d", "Model", "--yes", "--wait", "--timeout", "30")).Code);
    }

    // l) tools without arguments
    [Fact]
    public void ToolWithoutArgumentsSaysSo()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;
        var problem = ArgumentCheckedTool.Check(schema, new Dictionary<string, JsonElement> { ["x"] = JsonDocument.Parse("1").RootElement });
        Assert.Equal("Unknown argument(s): x. This tool takes no arguments.", problem);
    }

    [Fact]
    public void ToolWithArgumentsStillListsThem()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"string"}}}""").RootElement;
        var problem = ArgumentCheckedTool.Check(schema, new Dictionary<string, JsonElement> { ["x"] = JsonDocument.Parse("1").RootElement });
        Assert.EndsWith("Expected arguments: a, b.", problem);
    }
}

// i) port already in use
public sealed class PortInUseTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbridge-port-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void BindFailuresAreRecognised()
    {
        var inUse = new IOException("Failed to bind to address http://127.0.0.1:1: address already in use.", new SocketException((int)SocketError.AddressAlreadyInUse));
        Assert.True(McpHost.IsBindFailure(inUse));
        Assert.True(McpHost.IsAddressInUse(inUse));
        var denied = new IOException("Failed to bind to address http://127.0.0.1:1: permission denied.");
        Assert.True(McpHost.IsBindFailure(denied));
        Assert.False(McpHost.IsAddressInUse(denied));
        Assert.False(McpHost.IsBindFailure(new IOException("disk full")));
    }

    private sealed class CaptureProvider(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, lines);

        public void Dispose() { }

        private sealed class Capture(string category, List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Add($"{category}|{logLevel}|{formatter(state, exception)}");
        }
    }

    [Fact]
    public void OnlyBindFailureLogsAreDroppedByTheHostLogFilter()
    {
        var lines = new List<string>();
        using var inner = LoggerFactory.Create(b => b.AddProvider(new CaptureProvider(lines)));
        using var provider = new BindFailureFilterProvider(inner);
        var host = provider.CreateLogger("Microsoft.Extensions.Hosting.Internal.Host");

        host.LogCritical(new IOException("Failed to bind to address http://127.0.0.1:1: address already in use."), "Hosting failed to start");
        host.LogCritical(new InvalidOperationException("background service crashed"), "BackgroundService failed");
        host.LogError("stop failed without exception");

        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain(lines, l => l.Contains("Hosting failed to start", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Host|Critical|BackgroundService failed", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Host|Error|stop failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OccupiedPortGivesOneErrorLineAndExitOne()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var path = Path.Combine(_dir, "config.yaml");
        File.WriteAllText(path, "profiles:\n  p:\n    tenant_id: t\n    client_id: c\n    client_secret: s3cret-value\n");
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync(["mcp", "--http", "--port", port.ToString(), "--config", path], o, e, new StringReader(""),
            n => n is "HOME" or "APPDATA" ? _dir : null, () => false, CancellationToken.None);
        Assert.Equal(1, code);
        Assert.Contains($"error: cannot listen on 127.0.0.1:{port}: address already in use", e.ToString());
        Assert.DoesNotContain("   at ", e.ToString());
        Assert.DoesNotContain("fail:", e.ToString());
        Assert.DoesNotContain("s3cret-value", e.ToString());
    }
}
