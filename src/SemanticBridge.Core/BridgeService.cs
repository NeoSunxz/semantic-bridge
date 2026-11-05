using System.Globalization;
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
public sealed class BridgeService(ResolvedConfig config, PowerBiRestClient rest, IXmlaClient xmla,
    IDescriptionWriter? descriptions = null, WriteGate? gate = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const int MaxRefreshHistory = 100;
    private const int HistoryWindow = 20;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(30);
    private readonly WriteGate _gate = gate ?? new WriteGate(config.AllowWrite);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

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

    // ---- writes (all gated) ------------------------------------------------

    /// <summary>Reads current descriptions and builds the old-to-new change list. Changes nothing.</summary>
    public async Task<DescriptionPlan> PlanDescriptionsAsync(string? workspace, string dataset, IReadOnlyList<DescriptionUpdate> updates, CancellationToken ct)
    {
        _gate.EnsureOpen();
        if (updates.Count == 0) throw new BridgeException(ErrorKind.Usage, "Nothing to update: no targets given.");
        var writer = descriptions ?? throw new InvalidOperationException("No description writer is configured.");
        var (ws, ds) = await ResolveAsync(workspace, dataset, ct).ConfigureAwait(false);
        if (ws.Workspace.IsOnDedicatedCapacity == false)
            throw new BridgeException(ErrorKind.Usage, $"Workspace '{ws.Workspace.Name}' is not on a dedicated capacity, so it has no XMLA endpoint; descriptions cannot be updated there.");
        var model = await writer.ListAsync(ws.XmlaName, ds.Name, ct).ConfigureAwait(false);
        var (changes, unchanged) = DescriptionTargets.Plan(model, updates);
        return new DescriptionPlan { Workspace = ws.Workspace.Name, Dataset = ds.Name, XmlaWorkspace = ws.XmlaName, Changes = changes, Unchanged = unchanged };
    }

    /// <summary>Applies a plan's changes in one save. Does nothing when the plan has no changes.</summary>
    public async Task ApplyDescriptionsAsync(DescriptionPlan plan, CancellationToken ct)
    {
        _gate.EnsureOpen();
        if (plan.Changes.Count == 0) return;
        var writer = descriptions ?? throw new InvalidOperationException("No description writer is configured.");
        await writer.ApplyAsync(plan.XmlaWorkspace, plan.Dataset, plan.Changes, ct).ConfigureAwait(false);
    }

    /// <summary>Plan and apply in one call, for callers that confirm elsewhere (MCP: the agent asks the user first).</summary>
    public async Task<DescriptionPlan> UpdateDescriptionsAsync(string? workspace, string dataset, IReadOnlyList<DescriptionUpdate> updates, CancellationToken ct)
    {
        var plan = await PlanDescriptionsAsync(workspace, dataset, updates, ct).ConfigureAwait(false);
        await ApplyDescriptionsAsync(plan, ct).ConfigureAwait(false);
        return plan;
    }

    /// <summary>
    /// Queues a refresh. With <paramref name="trackForWait"/> the refresh history is read first, so a later
    /// <see cref="WaitForRefreshAsync"/> can tell this run from earlier ones when the service returns no request ID.
    /// </summary>
    public async Task<RefreshTrigger> RefreshAsync(string? workspace, string dataset, CancellationToken ct, bool trackForWait = false)
    {
        _gate.EnsureOpen();
        var (ws, ds) = await ResolveAsync(workspace, dataset, ct).ConfigureAwait(false);
        IReadOnlySet<string>? known = null;
        if (trackForWait)
        {
            var before = await rest.GetRefreshesAsync(ws.Workspace.Id, ds.Id, HistoryWindow, ct).ConfigureAwait(false);
            known = before.Select(e => e.RequestId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        var triggeredAt = DateTimeOffset.UtcNow;
        var requestId = await rest.TriggerRefreshAsync(ws.Workspace.Id, ds.Id, ct).ConfigureAwait(false);
        return new RefreshTrigger(ws.Workspace.Id, ds.Id, ws.Workspace.Name, ds.Name, requestId, triggeredAt, known);
    }

    /// <summary>Refresh history, newest first. A read: not gated.</summary>
    public async Task<IReadOnlyList<RefreshEntry>> GetRefreshStatusAsync(string? workspace, string dataset, int? top, CancellationToken ct)
    {
        var count = top ?? 5;
        if (count is < 1 or > MaxRefreshHistory) throw new BridgeException(ErrorKind.Usage, $"top must be between 1 and {MaxRefreshHistory}.");
        var (ws, ds) = await ResolveAsync(workspace, dataset, ct).ConfigureAwait(false);
        return await rest.GetRefreshesAsync(ws.Workspace.Id, ds.Id, count, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls the refresh history with backoff (2 s doubling to 30 s) until the refresh is finished or the timeout is used up.
    /// The timeout counts the waiting between polls, not the time the polls themselves take.
    /// Without a request ID only an entry that was not in the pre-trigger history (and did not start before the trigger) counts;
    /// if none can be identified the result has no entry, never another run's.
    /// </summary>
    public async Task<RefreshWaitResult> WaitForRefreshAsync(RefreshTrigger trigger, TimeSpan timeout, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        var interval = TimeSpan.FromSeconds(2);
        while (true)
        {
            var entries = await rest.GetRefreshesAsync(trigger.WorkspaceId, trigger.DatasetId, HistoryWindow, ct).ConfigureAwait(false);
            // Without a request ID, accept only entries that are new since the snapshot and not started before the trigger (allowing for clock skew).
            var mine = trigger.RequestId is { } id
                ? entries.FirstOrDefault(e => string.Equals(e.RequestId, id, StringComparison.OrdinalIgnoreCase))
                : entries.FirstOrDefault(e => trigger.KnownRequestIds?.Contains(e.RequestId) != true
                    && DateTimeOffset.TryParse(e.StartTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var started)
                    && started >= trigger.TriggeredAt - ClockSkew);
            if (mine is { IsFinished: true }) return new RefreshWaitResult(mine, TimedOut: false);
            if (waited >= timeout) return new RefreshWaitResult(mine, TimedOut: true);
            var pause = timeout - waited < interval ? timeout - waited : interval;
            await _delay(pause, ct).ConfigureAwait(false);
            waited += pause;
            interval = interval * 2 > MaxPollInterval ? MaxPollInterval : interval * 2;
        }
    }
}

public sealed record RefreshTrigger(string WorkspaceId, string DatasetId, string WorkspaceName, string DatasetName, string? RequestId, DateTimeOffset TriggeredAt,
    IReadOnlySet<string>? KnownRequestIds = null);

public sealed record RefreshWaitResult(RefreshEntry? Entry, bool TimedOut);
