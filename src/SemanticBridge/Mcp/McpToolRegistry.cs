using ModelContextProtocol.Server;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Cli.Mcp;

/// <summary>
/// The single place that decides which MCP tools exist. Read tools are always registered; write tools only
/// when the <see cref="WriteGate"/> is open (allow_write, and over HTTP also --allow-http-write).
/// A closed gate means the agent never sees write tools at all.
/// </summary>
public static class McpToolRegistry
{
    public static IReadOnlyList<McpServerTool> Create(McpTools tools, WriteGate gate, TextWriter log)
    {
        var list = new List<McpServerTool>
        {
            McpServerTool.Create(tools.ListWorkspaces),
            McpServerTool.Create(tools.ListDatasets),
            McpServerTool.Create(tools.GetModelSchema),
            McpServerTool.Create(tools.RunDax),
            McpServerTool.Create(tools.GetRefreshStatus),
        };

        if (gate.IsOpen)
        {
            list.Add(McpServerTool.Create(tools.UpdateDescription));
            list.Add(McpServerTool.Create(tools.RefreshDataset));
            return list;
        }
        // Over stdio a closed gate is the default and needs no comment; over HTTP the opt-in is easy to miss, so say why.
        if (gate.OverHttp && gate.ClosedReason is { } reason) log.WriteLine("mcp: write tools are not registered. " + reason);
        return list;
    }
}
