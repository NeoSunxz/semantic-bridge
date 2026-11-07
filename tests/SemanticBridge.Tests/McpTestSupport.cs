using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SemanticBridge.Cli.Mcp;
using SemanticBridge.Core;
using SemanticBridge.Core.Config;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Tests;

/// <summary>An MCP server and client connected by in-memory pipes.</summary>
internal sealed class InProcessMcp : IAsyncDisposable
{
    private readonly McpServer _server;
    private readonly Task _serverRun;

    private InProcessMcp(McpServer server, Task serverRun, McpClient client)
    {
        _server = server;
        _serverRun = serverRun;
        Client = client;
    }

    public McpClient Client { get; }

    public static async Task<InProcessMcp> StartAsync(IEnumerable<McpServerTool> tools)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var t in tools) collection.Add(t);
        var server = McpServer.Create(
            new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()),
            new McpServerOptions { ToolCollection = collection });
        var run = server.RunAsync();
        var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()), cancellationToken: TestContext.Current.CancellationToken);
        return new InProcessMcp(server, run, client);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _server.DisposeAsync();
        try { await _serverRun; }
        catch (OperationCanceledException) { }
    }
}

internal static class McpTestData
{
    public static (McpTools Tools, FakeXmla Xmla) Tools(ResolvedConfig? config = null)
    {
        var scrubber = new SecretScrubber();
        var cfg = config ?? TestData.Config(defaultWorkspace: "Sales");
        scrubber.Register(cfg.ClientSecret);
        var xmla = new FakeXmla();
        var service = new BridgeService(cfg, TestData.Rest(TestData.PowerBi(), cfg, scrubber), xmla);
        return (new McpTools(service, scrubber), xmla);
    }

    public static IReadOnlyList<McpServerTool> ReadTools(McpTools tools) => McpToolRegistry.Create(tools, new WriteGate(false), TextWriter.Null);
}
