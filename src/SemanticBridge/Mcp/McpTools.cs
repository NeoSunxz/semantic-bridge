using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SemanticBridge.Cli.Output;
using SemanticBridge.Core;
using SemanticBridge.Core.Config;
using SemanticBridge.Core.Diagnostics;
using SemanticBridge.Core.Results;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Cli.Mcp;

/// <summary>
/// The read-only MCP tools. Thin adapters over <see cref="BridgeService"/>, the same facade the CLI uses.
/// Failures come back as tool errors (isError) with scrubbed engine text so the agent can correct itself.
/// </summary>
public sealed class McpTools(BridgeService service, SecretScrubber scrubber)
{
    public const string TruncationNote =
        "The result is TRUNCATED: more rows exist than were returned. Do not rely on it as complete. Re-run with aggregation or filters (SUMMARIZECOLUMNS, TOPN, FILTER) instead of reading raw rows.";

    [McpServerTool(Name = "list_workspaces", ReadOnly = true)]
    [Description("List the Power BI workspaces visible to the service principal (id, name, is_on_dedicated_capacity). Start here, then call list_datasets.")]
    public Task<CallToolResult> ListWorkspaces(CancellationToken ct) =>
        Run(async () => JsonOutput.Serialize(await service.ListWorkspacesAsync(ct).ConfigureAwait(false)), ct);

    [McpServerTool(Name = "list_datasets", ReadOnly = true)]
    [Description("List the semantic models (datasets) in a workspace. Next step: get_model_schema.")]
    public Task<CallToolResult> ListDatasets(
        [Description("Workspace name or ID. Optional when the profile has a default workspace.")] string? workspace = null,
        CancellationToken ct = default) =>
        Run(async () => JsonOutput.Serialize(await service.ListDatasetsAsync(workspace, ct).ConfigureAwait(false)), ct);

    [McpServerTool(Name = "get_model_schema", ReadOnly = true)]
    [Description("Get a model's tables, columns, measures (with their DAX) and relationships. Read it before writing DAX so names are exact: Table[Column], [Measure]. Needs the XMLA endpoint (Premium/PPU/Fabric workspaces). Then call run_dax.")]
    public Task<CallToolResult> GetModelSchema(
        [Description("Dataset name or ID.")] string dataset,
        [Description("Workspace name or ID. Optional when the profile has a default workspace.")] string? workspace = null,
        [Description("Only this table (and its relationships). Use it on large models.")] string? table = null,
        CancellationToken ct = default) =>
        Run(async () => JsonOutput.Serialize(await service.GetSchemaAsync(workspace, dataset, table, null, ct).ConfigureAwait(false)), ct);

    [McpServerTool(Name = "run_dax", ReadOnly = true)]
    [Description("Run a read-only DAX query (EVALUATE, optionally with DEFINE) against a dataset. Get the schema first so table/column/measure names are exact. Prefer aggregations (SUMMARIZECOLUMNS, TOPN) over raw rows. Column names are returned as the engine reports them. If truncated=true the result is incomplete: aggregate or filter and rerun. Check `warnings` (e.g. max_rows capped). Only DAX, not SQL or MDX.")]
    public Task<CallToolResult> RunDax(
        [Description("Dataset name or ID.")] string dataset,
        [Description("The DAX query, starting with EVALUATE or DEFINE.")] string dax,
        [Description("Workspace name or ID. Optional when the profile has a default workspace.")] string? workspace = null,
        [Description("Row limit for this query. Defaults to the profile's max_rows and is capped by its ceiling (reported in warnings).")] int? max_rows = null,
        [Description("Path: auto (default), xmla or rest. auto uses XMLA and uses REST only for workspaces without an XMLA endpoint.")] string? via = null,
        CancellationToken ct = default) =>
        Run(async () =>
        {
            var viaValue = string.IsNullOrWhiteSpace(via) ? (QueryVia?)null : ConfigLoader.ParseVia(via, "via");
            var result = await service.QueryAsync(workspace, dataset, dax, max_rows, viaValue, ct).ConfigureAwait(false);
            return SerializeQuery(result);
        }, ct);

    internal static string SerializeQuery(QueryResult result)
    {
        var node = JsonNode.Parse(JsonOutput.Serialize(result))!.AsObject();
        if (result.Truncated) node["note"] = TruncationNote;
        return node.ToJsonString(JsonOutput.Options);
    }

    private async Task<CallToolResult> Run(Func<Task<string>> body, CancellationToken ct)
    {
        try
        {
            var json = await body().ConfigureAwait(false);
            return new CallToolResult { Content = [new TextContentBlock { Text = scrubber.Scrub(json) }] };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Not cancelled by the client: an HTTP or engine timeout. Tell the agent instead of failing the protocol request.
            return Error("The operation timed out before it completed. Try a narrower query (aggregate or filter) or retry later.");
        }
        catch (Exception ex)
        {
            var message = scrubber.Scrub(ex is BridgeException ? ex.Message : $"unexpected {ex.GetType().Name}: {ex.Message}");
            if (ErrorHints.For(message) is { } hint) message += " Hint: " + hint;
            return Error(message);
        }
    }

    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
