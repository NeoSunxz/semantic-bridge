using SemanticBridge.Cli;
using SemanticBridge.Cli.Output;
using SemanticBridge.Core.Config;
using SemanticBridge.Core.Results;

namespace SemanticBridge.Tests;

public sealed class FormatterTests
{
    [Fact]
    public void CsvFollowsRfc4180()
    {
        var csv = CsvFormatter.Render(["a,b", "plain"], [["x\"y", null], ["line\nbreak", 1.5], ["ok", true]]);
        Assert.Equal("\"a,b\",plain\r\n\"x\"\"y\",\r\n\"line\nbreak\",1.5\r\nok,true\r\n", csv);
    }

    [Fact]
    public void CsvUsesInvariantCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Contains("1.5", CsvFormatter.Render(["n"], [[1.5]]));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void TableAlignsColumnsAndNeverShortensCells()
    {
        var longText = new string('z', 80);
        var text = TableFormatter.Render(["Name", "[N]"], [["a", 5L], [longText, 12345L], [null, null]]);
        var lines = text.TrimEnd().Split(Environment.NewLine);
        Assert.Equal(5, lines.Length);
        Assert.Contains(longText, text);
        Assert.Contains("NULL", lines[4]);
        Assert.Equal(lines[0].IndexOf('|'), lines[2].IndexOf('|'));
    }

    [Fact]
    public void JsonQueryResultHasTheDocumentedFields()
    {
        var json = JsonOutput.Serialize(new QueryResult
        {
            Columns = [new ColumnInfo("Sales[Amount]", "Double")],
            Rows = [[1.5], [null]],
            Truncated = true,
            TruncationReason = "why",
            Path = "xmla",
            ElapsedMs = 12,
            Warnings = ["w"],
        });
        Assert.Equal("""{"columns":[{"name":"Sales[Amount]","type":"Double"}],"rows":[[1.5],[null]],"row_count":2,"truncated":true,"truncation_reason":"why","path":"xmla","elapsed_ms":12,"warnings":["w"]}""", json);
    }
}

public sealed class CliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbridge-cli-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<(int Code, string Out, string Err)> Run(string[] args, params (string, string)[] env)
    {
        var map = env.ToDictionary(e => e.Item1, e => e.Item2);
        map.TryAdd("HOME", _dir);
        map.TryAdd("APPDATA", _dir);
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync(args, o, e, new StringReader(string.Empty), n => map.GetValueOrDefault(n), () => false, CancellationToken.None);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task ConfigInitRefusesToOverwriteWithoutForce()
    {
        var path = Path.Combine(_dir, "sub", "config.yaml");
        Assert.Equal(0, (await Run(["config", "init", "--config", path])).Code);
        Assert.Contains("WARNING", File.ReadAllText(path));
        File.WriteAllText(path, "# mine");
        var again = await Run(["config", "init", "--config", path]);
        Assert.Equal(2, again.Code);
        Assert.Equal("# mine", File.ReadAllText(path));
        Assert.Equal(0, (await Run(["config", "init", "--config", path, "--force"])).Code);
        Assert.Contains("WARNING", File.ReadAllText(path));
    }

    [Fact]
    public async Task ConfigInitCreatesTheFilePrivateAlsoWhenForced()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "private.yaml");
        Assert.Equal(0, (await Run(["config", "init", "--config", path])).Code);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Assert.Equal(0, (await Run(["config", "init", "--config", path, "--force"])).Code);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task CancellationExitsWith130()
    {
        var path = Path.Combine(_dir, "config.yaml");
        File.WriteAllText(path, "profiles:\n  p:\n    tenant_id: t\n    client_id: c\n    client_secret: s\n");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync(["workspaces", "--config", path], o, e, new StringReader(string.Empty), n => n is "HOME" or "APPDATA" ? _dir : null, () => false, cts.Token);
        Assert.Equal(130, code);
        Assert.Contains("cancelled", e.ToString());
    }

    [Fact]
    public async Task ConfigShowMasksTheSecretAndShowsSources()
    {
        var path = Path.Combine(_dir, "config.yaml");
        File.WriteAllText(path, "profiles:\n  p:\n    cloud: china\n    tenant_id: t\n    client_id: c\n    client_secret: plain-file-secret\n");
        var shown = await Run(["config", "show", "--config", path], (ConfigLoader.EnvTenantId, "env-tenant"));
        Assert.Equal(0, shown.Code);
        Assert.DoesNotContain("plain-file-secret", shown.Out + shown.Err);
        Assert.Contains("***", shown.Out);
        Assert.Contains("env SBRIDGE_TENANT_ID", shown.Out);
        Assert.Contains("preset china", shown.Out);

        var fromEnv = await Run(["config", "show", "--config", path], (ConfigLoader.EnvClientSecret, "env-only-secret"));
        Assert.DoesNotContain("env-only-secret", fromEnv.Out + fromEnv.Err);
    }

    [Fact]
    public async Task MissingCredentialsIsAUsageErrorWithoutSecrets()
    {
        var result = await Run(["workspaces"], (ConfigLoader.EnvTenantId, "t"), (ConfigLoader.EnvClientId, "c"));
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Out);
        Assert.Contains("client_secret", result.Err);
    }

    [Fact]
    public async Task NoConfigAtAllIsAUsageError()
    {
        var result = await Run(["workspaces"]);
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Out);
    }

    [Fact]
    public async Task BadUsageExitsWithTwo()
    {
        Assert.Equal(2, (await Run(["query", "--bogus"])).Code);
        Assert.Equal(2, (await Run(["query", "-w", "x", "-d", "y", "-f", "xml"])).Code);
    }

    [Fact]
    public async Task QueryInputIsValidatedBeforeAnyNetworkCall()
    {
        var env = new[] { (ConfigLoader.EnvTenantId, "t"), (ConfigLoader.EnvClientId, "c"), (ConfigLoader.EnvClientSecret, "s3cret-value") };
        var notDax = await Run(["query", "-w", "x", "-d", "y", "DROP TABLE x"], env);
        Assert.Equal(2, notDax.Code);
        Assert.Contains("EVALUATE", notDax.Err);
        Assert.DoesNotContain("s3cret-value", notDax.Err);
        var none = await Run(["query", "-w", "x", "-d", "y"], env);
        Assert.Equal(2, none.Code);
    }

    [Fact]
    public async Task SchemaViaRestFailsWithExplanation()
    {
        var env = new[] { (ConfigLoader.EnvTenantId, "t"), (ConfigLoader.EnvClientId, "c"), (ConfigLoader.EnvClientSecret, "s3cret-value") };
        var result = await Run(["schema", "-w", "x", "-d", "y", "--via", "rest"], env);
        Assert.Equal(2, result.Code);
        Assert.Contains("executeQueries", result.Err);
        Assert.Contains("INFO", result.Err);
        Assert.DoesNotContain("hint:", result.Err);
    }

    [Fact]
    public async Task VersionIsPrinted()
    {
        var result = await Run(["--version"]);
        Assert.Equal(0, result.Code);
        Assert.Matches(@"^\d+\.\d+\.\d+", result.Out.Trim());
    }
}
