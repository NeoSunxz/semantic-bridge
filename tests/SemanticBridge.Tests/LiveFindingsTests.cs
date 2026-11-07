using ModelContextProtocol.Protocol;
using SemanticBridge.Cli.Mcp;
using SemanticBridge.Cli.Output;
using SemanticBridge.Core.Diagnostics;
using SemanticBridge.Core.Safety;
using SemanticBridge.Core.Xmla;

namespace SemanticBridge.Tests;

public sealed class DisplayWidthTests
{
    [Theory]
    [InlineData("abc", 3)]
    [InlineData("人民路店", 8)]
    [InlineData("门店[门店ID]", 12)]
    [InlineData("ｱＡ", 3)]            // halfwidth katakana 1, fullwidth A 2
    [InlineData("ひらカナ한글", 12)]
    [InlineData("（）", 4)]            // fullwidth parentheses U+FF08/FF09
    [InlineData("\U00020000", 2)]      // surrogate pair: CJK Extension B
    [InlineData("é", 1)]         // combining acute adds no width
    [InlineData("", 0)]
    public void CountsTerminalColumns(string text, int expected) => Assert.Equal(expected, DisplayWidth.Of(text));

    [Fact]
    public void TableAlignsByDisplayWidthIncludingSeparator()
    {
        var text = TableFormatter.Render(["门店[门店ID]", "门店[门店]"], [["1", "人民路店"], ["22", "abc"]]);
        var lines = text.TrimEnd().Split(Environment.NewLine);
        var bar = DisplayWidth.Of(lines[0][..lines[0].IndexOf('|')]);
        Assert.Equal(bar, DisplayWidth.Of(lines[2][..lines[2].IndexOf('|')]));
        Assert.Equal(bar, DisplayWidth.Of(lines[3][..lines[3].IndexOf('|')]));
        Assert.Equal(bar, lines[1].IndexOf('+'));
        Assert.Equal(DisplayWidth.Of("门店[门店ID]") + 3 + DisplayWidth.Of("门店[门店]"), lines[1].Length);
    }
}

public sealed class EngineMessageTests
{
    [Fact]
    public void StripsMarkersAndCollapsesTechnicalDetails()
    {
        var raw = "Query (1, 10) Cannot find table '<oii>不存在的表</oii>'.\n\nTechnical Details:\nRootActivityId: 1537f781-aaaa\nDate (UTC): 11/7/2026 6:31:23 AM\n";
        Assert.Equal("Query (1, 10) Cannot find table '不存在的表'. (RootActivityId: 1537f781-aaaa)", EngineMessage.Clean(raw));
    }

    [Fact]
    public void StripsCconMarkersFromLiveSyntaxErrors()
    {
        Assert.Equal(
            "Query (1, 22) The syntax for ')' is incorrect. (EVALUATE ROW(\"x\", 1 +)). (RootActivityId: abc-1)",
            EngineMessage.Clean("Query (1, 22) The syntax for '<ccon>)</ccon>' is incorrect. (<ccon>EVALUATE ROW(\"x\", 1 +)</ccon>).\n\nTechnical Details:\nRootActivityId: abc-1\nDate (UTC): now"));
        Assert.Equal("Invalid token, Line 1, Offset 10, 不.", EngineMessage.Clean("Invalid token, Line 1, Offset 10, <ccon>不</ccon>."));
    }

    [Fact]
    public void KeepsComparisonOperatorsThatLookLikeTags()
    {
        const string message = "Bad expression: [x] <> 1 and a<b and c>d near <unknown>x</unknown>.";
        Assert.Equal(message, EngineMessage.Clean(message));
    }

    [Fact]
    public void DropsTheBlockWithoutAnId()
    {
        Assert.Equal("boom", EngineMessage.Clean("boom\r\nTechnical Details:\r\nDate (UTC): now"));
        Assert.Equal("plain text", EngineMessage.Clean("plain text"));
    }

    [Fact]
    public void TomErrorsAreCleanedBeforeAdviceIsAdded()
    {
        var mapped = TomDescriptionWriter.MapError(EngineMessage.Clean("The database is <oii>read-only</oii>.\nTechnical Details:\nRootActivityId: abc"));
        Assert.DoesNotContain("<oii>", mapped);
        Assert.DoesNotContain("Technical Details", mapped);
        Assert.EndsWith("(RootActivityId: abc)", mapped);
    }
}

public sealed class ErrorHintsScopeTests
{
    [Fact]
    public void RealXmlaUnavailableErrorsStillGetTheHint()
    {
        Assert.Contains("XMLA endpoint is not available", ErrorHints.For("XMLA connection failed: the XMLA endpoint is disabled for this capacity"));
    }
}

public sealed class InfoFallbackWarningTests
{
    [Fact]
    public void WarningIsCleanedAndScrubbed()
    {
        var scrubber = new SecretScrubber();
        scrubber.Register("super-secret-value");
        var client = new SemanticBridge.Core.Xmla.AdomdXmlaClient(null!, scrubber, 30);
        var warning = client.InfoFallbackWarning("bad <oii>x</oii> super-secret-value\nTechnical Details:\nRootActivityId: r1");
        Assert.Equal("INFO.* functions failed (bad x *** (RootActivityId: r1)); metadata was read from TMSCHEMA_* rowsets instead.", warning);
    }
}

public sealed class DatabaseLookupRetryTests
{
    private static BridgeException NotFound() => new(ErrorKind.NotFound, "Dataset 'x' was not found on the XMLA endpoint of this workspace.");

    [Fact]
    public async Task MissThenHitRetriesOnceAfterTheDelay()
    {
        var calls = 0;
        var result = await TomDescriptionWriter.FindWithRetryAsync<int>(() => Task.FromResult(++calls == 1 ? (int?)null : 7), TimeSpan.Zero, NotFound, TestContext.Current.CancellationToken);
        Assert.Equal(7, result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task HitFirstTimeDoesNotRetry()
    {
        var calls = 0;
        await TomDescriptionWriter.FindWithRetryAsync<int>(() => Task.FromResult((int?)(++calls)), TimeSpan.FromSeconds(30), NotFound, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TwoMissesFailWithTheOriginalMessageAfterExactlyTwoAttempts()
    {
        var calls = 0;
        var ex = await Assert.ThrowsAsync<BridgeException>(() =>
            TomDescriptionWriter.FindWithRetryAsync<int>(() => { calls++; return Task.FromResult((int?)null); }, TimeSpan.Zero, NotFound, TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
        Assert.Equal(ErrorKind.NotFound, ex.Kind);
        Assert.Contains("was not found on the XMLA endpoint of this workspace.", ex.Message);
    }

    [Fact]
    public async Task CancellationDuringTheDelayIsRespected()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var task = TomDescriptionWriter.FindWithRetryAsync<int>(() => { calls++; cts.Cancel(); return Task.FromResult((int?)null); }, TimeSpan.FromSeconds(30), NotFound, cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, calls);
    }
}

public sealed class McpArgumentValidationTests
{
    private static string Text(CallToolResult r) => ((TextContentBlock)Assert.Single(r.Content)).Text;

    [Fact]
    public async Task MissingAndUnknownArgumentsAreNamed()
    {
        var (tools, xmla) = McpTestData.Tools();
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var result = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dataset"] = "model", ["query"] = "EVALUATE secret-looking-value" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        var text = Text(result);
        Assert.Contains("Missing required argument(s): dax.", text);
        Assert.Contains("Unknown argument(s): query.", text);
        Assert.Contains("Expected arguments: dax, dataset, workspace, max_rows, via.", text);
        Assert.DoesNotContain("secret-looking-value", text);
        Assert.Equal(0, xmla.QueryCalls);
    }

    [Fact]
    public async Task WrongTypedArgumentIsNamedWithoutEchoingTheValue()
    {
        var (tools, _) = McpTestData.Tools();
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var result = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dataset"] = "model", ["dax"] = "EVALUATE x", ["max_rows"] = "lots-of-rows" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("Wrong type for argument(s): max_rows (expected integer).", Text(result));
        Assert.DoesNotContain("lots-of-rows", Text(result));
    }

    [Fact]
    public async Task WriteToolsNameTheirMissingArguments()
    {
        var (tools, _) = McpTestData.Tools();
        var all = McpToolRegistry.Create(tools, new WriteGate(true), TextWriter.Null);
        await using var mcp = await InProcessMcp.StartAsync(all);
        var result = await mcp.Client.CallToolAsync("update_description", new Dictionary<string, object?> { ["dataset"] = "model", ["description"] = "x" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("Missing required argument(s): target_type, target.", Text(result));
    }

    [Fact]
    public async Task ValidCallsAndOmittedOptionalArgumentsStillWork()
    {
        var (tools, xmla) = McpTestData.Tools();
        xmla.OnQuery = () => TestData.Xmla(1);
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        var result = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dataset"] = "model", ["dax"] = "EVALUATE x", ["max_rows"] = 5, ["via"] = null }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, result.IsError);
    }

    // The SDK itself cannot bind 5.0 or 1e2 to an int? parameter (it fails with the generic invoke error), so they are reported as wrong-typed.
    [Theory]
    [InlineData("5.5")]
    [InlineData("5.0")]
    [InlineData("1e2")]
    public async Task NumbersThatAreNotPlainIntegersAreRejectedForAnInteger(string json)
    {
        var (tools, xmla) = McpTestData.Tools();
        await using var mcp = await InProcessMcp.StartAsync(McpTestData.ReadTools(tools));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = await mcp.Client.CallToolAsync("run_dax", new Dictionary<string, object?> { ["dataset"] = "model", ["dax"] = "EVALUATE x", ["max_rows"] = doc.RootElement.Clone() }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("Wrong type for argument(s): max_rows", Text(result));
        Assert.Equal(0, xmla.QueryCalls);
    }
}
