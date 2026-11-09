using System.Text.Json;
using ModelContextProtocol.Protocol;
using SemanticBridge.Cli;
using SemanticBridge.Cli.Mcp;
using SemanticBridge.Core;
using SemanticBridge.Core.Config;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Tests;

public sealed class DefaultDatasetConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbridge-defaults-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string yaml)
    {
        var path = Path.Combine(_dir, "config.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private static Func<string, string?> Env(params (string, string)[] vars)
    {
        var map = vars.ToDictionary(v => v.Item1, v => v.Item2);
        return name => map.GetValueOrDefault(name);
    }

    private const string File1 = "profiles:\n  p:\n    tenant_id: t\n    client_id: c\n    default_workspace: FileWs\n    default_dataset: FileDs\n";

    [Fact]
    public void FileValuesAreUsedAndEnvOverridesThem()
    {
        var fromFile = ConfigLoader.Load(new ConfigLoadOptions(Write(File1)), Env());
        Assert.Equal("FileWs", fromFile.DefaultWorkspace);
        Assert.Equal("FileDs", fromFile.DefaultDataset);

        var fromEnv = ConfigLoader.Load(new ConfigLoadOptions(Write(File1)), Env((ConfigLoader.EnvWorkspace, "EnvWs"), (ConfigLoader.EnvDataset, "EnvDs")));
        Assert.Equal("EnvWs", fromEnv.DefaultWorkspace);
        Assert.Equal("EnvDs", fromEnv.DefaultDataset);
        Assert.Equal("env SBRIDGE_WORKSPACE", fromEnv.Sources["default_workspace"]);
        Assert.Equal("env SBRIDGE_DATASET", fromEnv.Sources["default_dataset"]);
    }

    [Fact]
    public void ConfigTemplateParsesAndAcceptsTheNewKey()
    {
        var file = ConfigLoader.ParseText(ConfigTemplate.Text.Replace("# default_dataset:", "default_dataset:"), "template");
        Assert.Equal("Sales", file.Profiles!["default"]!.DefaultDataset);
    }

    [Fact]
    public void WorkspaceAndDatasetVariablesAloneDoNotEnableEnvOnlyMode()
    {
        var env = Env(("HOME", _dir), ("APPDATA", _dir), (ConfigLoader.EnvWorkspace, "W"), (ConfigLoader.EnvDataset, "D"));
        var ex = Assert.Throws<BridgeException>(() => ConfigLoader.Load(new ConfigLoadOptions(), env));
        Assert.Equal(ErrorKind.Config, ex.Kind);

        var withCreds = Env(("HOME", _dir), ("APPDATA", _dir), (ConfigLoader.EnvTenantId, "t"), (ConfigLoader.EnvClientId, "c"),
            (ConfigLoader.EnvClientSecret, "s"), (ConfigLoader.EnvWorkspace, "W"), (ConfigLoader.EnvDataset, "D"));
        var cfg = ConfigLoader.Load(new ConfigLoadOptions(), withCreds);
        Assert.Equal("W", cfg.DefaultWorkspace);
        Assert.Equal("D", cfg.DefaultDataset);
    }
}

public sealed class DefaultDatasetBehaviourTests : IDisposable
{
    private const string Secret = "cli-fake-secret-value";
    private readonly string _dir = Directory.CreateTempSubdirectory("sbridge-defaults-cli-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Config(bool withDefaultDataset)
    {
        var path = Path.Combine(_dir, $"config-{withDefaultDataset}.yaml");
        var dataset = withDefaultDataset ? "    default_dataset: Model\n" : "";
        File.WriteAllText(path, $"profiles:\n  p:\n    tenant_id: t\n    client_id: c\n    client_secret: {Secret}\n    default_workspace: Sales\n    allow_write: true\n{dataset}");
        return path;
    }

    private async Task<(int Code, string Out, string Err)> Run(bool withDefaultDataset, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var handler = WriteTestData.RefreshApi(["Completed"]);
        var code = await CliApp.RunAsync([.. args, "--config", Config(withDefaultDataset)], o, e, new StringReader(""),
            n => n is "HOME" or "APPDATA" ? _dir : null, () => true, CancellationToken.None,
            (cfg, gate) => WriteTestData.Service(cfg, handler, new FakeDescriptionWriter(), gate));
        return (code, o.ToString(), e.ToString());
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("refresh-status")]
    [InlineData("refresh", "--yes")]
    [InlineData("describe", "--table", "Sales", "text", "--yes")]
    public async Task MissingDatasetWithoutDefaultIsAUsageError(params string[] args)
    {
        var r = await Run(false, args);
        Assert.Equal(2, r.Code);
        Assert.Contains("No dataset given: pass a dataset (-d in the CLI, `dataset` in MCP) or set default_dataset (or SBRIDGE_DATASET).", r.Err);
        Assert.DoesNotContain(Secret, r.Out + r.Err);
    }

    [Fact]
    public async Task RefreshWithoutDatasetUsesTheDefaultAndNamesIt()
    {
        var r = await Run(true, "refresh", "--yes");
        Assert.Equal(0, r.Code);
        Assert.Contains("dataset: Model", r.Out);
        Assert.Contains("workspace: Sales", r.Out);
    }

    [Fact]
    public async Task DescribeWithoutDatasetUsesTheDefaultAndNamesIt()
    {
        var r = await Run(true, "describe", "--table", "Sales", "New text", "--yes");
        Assert.Equal(0, r.Code);
        Assert.Contains("dataset 'Model'", r.Err);
    }

    [Fact]
    public async Task RefreshStatusWithoutDatasetUsesTheDefault()
    {
        var r = await Run(true, "refresh-status");
        Assert.Equal(0, r.Code);
    }

    [Fact]
    public async Task ServiceFallsBackToTheDefaultDataset()
    {
        var service = new BridgeService(TestData.Config(defaultWorkspace: "Sales", defaultDataset: "Model"), TestData.Rest(TestData.PowerBi()), new FakeXmla { OnQuery = () => TestData.Xmla() });
        var result = await service.QueryAsync(null, null, "EVALUATE x", null, null, CancellationToken.None);
        Assert.Single(result.Rows);
    }
}

public sealed class DefaultDatasetMcpTests
{
    private static string Text(CallToolResult r) => ((TextContentBlock)Assert.Single(r.Content)).Text;

    [Fact]
    public async Task ReadToolWithoutDatasetUsesTheDefault()
    {
        var (tools, xmla) = McpTestData.Tools(TestData.Config(defaultWorkspace: "Sales", defaultDataset: "Model"));
        xmla.OnQuery = () => TestData.Xmla(2);
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var result = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dax"] = "EVALUATE x" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(Text(result));
        Assert.Equal(2, doc.RootElement.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task ReadToolWithoutDatasetAndWithoutDefaultIsAToolError()
    {
        var (tools, _) = McpTestData.Tools();
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var result = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dax"] = "EVALUATE x" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("No dataset given", Text(result));
    }

    [Fact]
    public async Task OnlyTheWriteToolsRequireDataset()
    {
        var scrubber = new SecretScrubber();
        var gate = new WriteGate(true);
        var service = WriteTestData.Service(WriteTestData.WriteConfig(), WriteTestData.RefreshApi(["Completed"]), gate: gate);
        await using var mcp = await InProcessMcp.StartAsync(McpToolRegistry.Create(new McpTools(service, scrubber), gate, TextWriter.Null));
        var listed = await mcp.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        foreach (var tool in listed.Where(t => t.JsonSchema.TryGetProperty("properties", out var p) && p.TryGetProperty("dataset", out _)))
        {
            var required = tool.JsonSchema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()).ToArray() : [];
            var isWrite = tool.Name is "update_description" or "refresh_dataset";
            Assert.Equal(isWrite, required.Contains("dataset"));
        }
        Assert.Equal(7, listed.Count);
    }
}
