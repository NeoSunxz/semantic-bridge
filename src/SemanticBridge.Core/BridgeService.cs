using SemanticBridge.Core.Config;
using SemanticBridge.Core.Rest;
using SemanticBridge.Core.Results;
using SemanticBridge.Core.Safety;
using SemanticBridge.Core.Xmla;

namespace SemanticBridge.Core;

public sealed record ResolvedWorkspace(Workspace Workspace, string XmlaName);

/// <summary>
/// The single facade used by every front-end (CLI now, MCP later) so their semantics stay identical:
/// name/ID resolution, DAX guard, max_rows ceiling and path selection (REST only for workspaces without dedicated capacity).
/// </summary>
public sealed class BridgeService(ResolvedConfig config, PowerBiRestClient rest, IXmlaClient xmla)
{
    public ResolvedConfig Config => config;

    public Task<IReadOnlyList<Workspace>> ListWorkspacesAsync(CancellationToken ct) => rest.GetWorkspacesAsync(ct);

    public async Task<IReadOnlyList<Dataset>> ListDatasetsAsync(string? workspace, CancellationToken ct)
    {
        var ws = await ResolveWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        return await rest.GetDatasetsAsync(ws.Workspace.Id, ct).ConfigureAwait(false);
    }

    /// <summary>Resolves a workspace by name or ID (falling back to default_workspace) and computes its XMLA name.</summary>
    public async Task<ResolvedWorkspace> ResolveWorkspaceAsync(string? workspace, CancellationToken ct)
    {
        var input = !string.IsNullOrWhiteSpace(workspace) ? workspace : config.DefaultWorkspace;
        if (string.IsNullOrWhiteSpace(input))
            throw new BridgeException(ErrorKind.Usage, "No workspace given. Pass -w <name or ID> or set default_workspace in the profile.");
        var all = await rest.GetWorkspacesAsync(ct).ConfigureAwait(false);
        var ws = NameResolver.Resolve(all, input, "Workspace", w => w.Id, w => w.Name);
        // The XMLA endpoint addresses a workspace by name; duplicate names need the "Name - <guid>" form.
        var duplicate = all.Count(w => string.Equals(w.Name, ws.Name, StringComparison.OrdinalIgnoreCase)) > 1;
        return new ResolvedWorkspace(ws, duplicate ? $"{ws.Name} - {ws.Id}" : ws.Name);
    }

    public async Task<ModelSchema> GetSchemaAsync(string? workspace, string dataset, string? tableFilter, QueryVia? via, CancellationToken ct)
    {
        // Only an explicit --via rest is a usage error; a config default of rest still reads metadata over XMLA.
        if (via == QueryVia.Rest)
            throw new BridgeException(ErrorKind.Usage, RestSchemaUnsupported + " Use --via xmla (or omit --via).");

        var (ws, ds) = await ResolveAsync(workspace, dataset, ct).ConfigureAwait(false);
        if (via != QueryVia.Xmla && ws.Workspace.IsOnDedicatedCapacity == false)
            throw new BridgeException(ErrorKind.Usage,
                $"Workspace '{ws.Workspace.Name}' is not on a dedicated capacity, so it has no XMLA endpoint. " + RestSchemaUnsupported
                + " Use 'query' for DAX on this workspace.");
        return await xmla.GetSchemaAsync(ws.XmlaName, ds.Name, tableFilter, ct).ConfigureAwait(false);
    }

    private const string RestSchemaUnsupported =
        "Model metadata needs the XMLA endpoint (Premium, PPU, Embedded or Fabric capacity): per the Power BI REST docs (Datasets - Execute Queries), INFO functions are not supported by executeQueries.";

    public async Task<QueryResult> QueryAsync(string? workspace, string dataset, string dax, int? requestedMaxRows, QueryVia? via, CancellationToken ct)
    {
        DaxGuard.Validate(dax);
        var (maxRows, warnings) = EffectiveMaxRows(requestedMaxRows);
        var effective = via ?? config.DefaultVia;
        var (ws, ds) = await ResolveAsync(workspace, dataset, ct).ConfigureAwait(false);

        // auto: REST only when the workspace is known to have no XMLA endpoint. Never retry over REST after an XMLA failure.
        if (effective == QueryVia.Auto && ws.Workspace.IsOnDedicatedCapacity == false)
        {
            warnings.Add("Used the REST API: workspace not on dedicated capacity (no XMLA endpoint).");
            effective = QueryVia.Rest;
        }

        var result = effective == QueryVia.Rest
            ? await rest.ExecuteQueryAsync(ws.Workspace.Id, ds.Id, dax, maxRows, ct).ConfigureAwait(false)
            : await xmla.QueryAsync(ws.XmlaName, ds.Name, dax, maxRows, ct).ConfigureAwait(false);
        return warnings.Count == 0 ? result : result.WithWarnings(warnings);
    }

    /// <summary>Applies the per-call request to the configured default and ceiling; a capped request is reported, never silent.</summary>
    public (int MaxRows, List<string> Warnings) EffectiveMaxRows(int? requested)
    {
        var warnings = new List<string>();
        if (requested is null) return (config.MaxRows, warnings);
        if (requested < 1) throw new BridgeException(ErrorKind.Usage, "max rows must be at least 1.");
        if (requested > config.MaxRowsCeiling)
        {
            warnings.Add($"Requested max rows {requested} exceeds max_rows_ceiling ({config.MaxRowsCeiling}); using {config.MaxRowsCeiling}.");
            return (config.MaxRowsCeiling, warnings);
        }
        return (requested.Value, warnings);
    }

    private async Task<(ResolvedWorkspace Workspace, Dataset Dataset)> ResolveAsync(string? workspace, string dataset, CancellationToken ct)
    {
        var ws = await ResolveWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var datasets = await rest.GetDatasetsAsync(ws.Workspace.Id, ct).ConfigureAwait(false);
        var ds = NameResolver.Resolve(datasets, dataset, "Dataset", d => d.Id, d => d.Name);
        return (ws, ds);
    }
}
