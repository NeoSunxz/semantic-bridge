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
using SemanticBridge.Core.Xmla;

namespace SemanticBridge.Cli.Mcp;

/// <summary>
/// The MCP tools. Thin adapters over <see cref="BridgeService"/>, the same facade the CLI uses. The write tools
/// (update_description, refresh_dataset) are only registered by <see cref="McpToolRegistry"/> when the write gate is open.
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

    [McpServerTool(Name = "get_refresh_status", ReadOnly = true)]
    [Description("Get a dataset's recent refresh history (newest first): request_id, refresh_type, status (Completed, Failed, Unknown = still running, ...), start/end time and the error if it failed. Use it after refresh_dataset.")]
    public Task<CallToolResult> GetRefreshStatus(
        [Description("Dataset name or ID.")] string dataset,
        [Description("Workspace name or ID. Optional when the profile has a default workspace.")] string? workspace = null,
        [Description("How many entries to return (1-100, default 5).")] int? top = null,
        CancellationToken ct = default) =>
        Run(async () => JsonOutput.Serialize(await service.GetRefreshStatusAsync(workspace, dataset, top, ct).ConfigureAwait(false)), ct);

    [McpServerTool(Name = "update_description", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("WRITE: change the description of a table, column or measure on the LIVE model; it affects every report built on it and takes effect immediately. "
        + "Before calling, show the user the target and the new text and get their explicit confirmation; never call it on your own initiative. "
        + "Requires the XMLA endpoint to be Read Write and Write permission on the dataset. The result reports the old and new description (and `unchanged` if the text was already set).")]
    public Task<CallToolResult> UpdateDescription(
        [Description("Dataset name or ID.")] string dataset,
        [Description("table, column or measure.")] string target_type,
        [Description("The table name; for a column Table[Column] (quote table names with brackets: 'My [Table]'[Col]); for a measure its name.")] string target,
        [Description("The new description text. An empty string clears it.")] string description,
        [Description("Workspace name or ID. Optional when the profile has a default workspace.")] string? workspace = null,
        CancellationToken ct = default) =>
        Run(async () =>
        {
            var parsed = (target_type ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "table" => DescriptionTargets.Table(target),
                "column" => DescriptionTargets.ParseColumn(target),
                "measure" => DescriptionTargets.Measure(target),
                _ => throw new BridgeException(ErrorKind.Usage, "target_type must be table, column or measure."),
            };
            var plan = await service.UpdateDescriptionsAsync(workspace, dataset, [new DescriptionUpdate(parsed, description ?? string.Empty)], ct).ConfigureAwait(false);
            var node = JsonNode.Parse(JsonOutput.Serialize(plan))!.AsObject();
            node["applied"] = plan.Changes.Count > 0;
            return node.ToJsonString(JsonOutput.Options);
        }, ct);

    [McpServerTool(Name = "refresh_dataset", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("WRITE: queue a refresh of the dataset (uses capacity and changes the data every report shows). Before calling, get the user's explicit confirmation; never call it on your own initiative. "
        + "Returns accepted=true and a request_id when the service provides one; the refresh runs asynchronously, so follow it with get_refresh_status.")]
    public Task<CallToolResult> RefreshDataset(
        [Description("Dataset name or ID.")] string dataset,
        [Description("Workspace name or ID. Optional when the profile has a default workspace.")] string? workspace = null,
        CancellationToken ct = default) =>
        Run(async () =>
        {
            var trigger = await service.RefreshAsync(workspace, dataset, ct).ConfigureAwait(false);
            return JsonOutput.Serialize(new { accepted = true, workspace = trigger.WorkspaceName, dataset = trigger.DatasetName, request_id = trigger.RequestId });
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
