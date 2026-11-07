using Microsoft.AnalysisServices.Tabular;
using SemanticBridge.Core.Auth;
using SemanticBridge.Core.Config;
using SemanticBridge.Core.Diagnostics;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Core.Xmla;

/// <summary>Description reads and writes through TOM against the workspace's XMLA read-write endpoint.</summary>
public sealed class TomDescriptionWriter(ResolvedConfig config, ITokenProvider tokens, SecretScrubber scrubber, TimeSpan? lookupRetryDelay = null) : IDescriptionWriter
{
    /// <summary>Wait before the single retry when the database list misses right after a save (it can be briefly stale).</summary>
    private readonly TimeSpan _lookupRetryDelay = lookupRetryDelay ?? TimeSpan.FromSeconds(2);

    public async Task<IReadOnlyList<ModelObjectInfo>> ListAsync(string workspace, string dataset, CancellationToken ct)
    {
        var (server, database) = await ConnectToDatabaseAsync(workspace, dataset, ct).ConfigureAwait(false);
        using var _ = server;
        return await Task.Run(() => Guard(() =>
        {
            var model = database.Model;
            var list = new List<ModelObjectInfo>();
            foreach (var table in model.Tables)
            {
                list.Add(new ModelObjectInfo(TargetKind.Table, table.Name, table.Name, table.Description));
                foreach (var column in table.Columns.Where(c => c.Type != ColumnType.RowNumber))
                    list.Add(new ModelObjectInfo(TargetKind.Column, table.Name, column.Name, column.Description));
                foreach (var measure in table.Measures)
                    list.Add(new ModelObjectInfo(TargetKind.Measure, table.Name, measure.Name, measure.Description));
            }
            return (IReadOnlyList<ModelObjectInfo>)list;
        }), ct).ConfigureAwait(false);
    }

    async Task IDescriptionWriter.ApplyAsync(string workspace, string dataset, IReadOnlyList<DescriptionChange> changes, CancellationToken ct)
    {
        var (server, database) = await ConnectToDatabaseAsync(workspace, dataset, ct).ConfigureAwait(false);
        using var _ = server;
        await Task.Run(() => Guard(() =>
        {
            var model = database.Model;
            foreach (var change in changes)
            {
                var table = model.Tables.Find(change.Table) ?? throw new BridgeException(ErrorKind.NotFound, $"Table '{change.Table}' no longer exists in the model.");
                NamedMetadataObject target = change.Kind switch
                {
                    TargetKind.Table => table,
                    TargetKind.Column => table.Columns.Find(change.Name) ?? throw Gone(change),
                    _ => table.Measures.Find(change.Name) ?? throw Gone(change),
                };
                switch (target)
                {
                    case Table t: t.Description = change.New; break;
                    case Column c: c.Description = change.New; break;
                    case Measure m: m.Description = change.New; break;
                }
            }
            model.SaveChanges();
            return 0;
        }), ct).ConfigureAwait(false);
    }

    private static BridgeException Gone(DescriptionChange c) => new(ErrorKind.NotFound, $"{c.Display} no longer exists in the model.");

    private static BridgeException DatasetNotFound(string dataset) =>
        new(ErrorKind.NotFound, $"Dataset '{dataset}' was not found on the XMLA endpoint of this workspace.");

    /// <summary>Connects and finds the database; one miss is retried on a fresh connection, because the list can be briefly stale after a TOM save.</summary>
    private Task<(Server Server, Database Database)> ConnectToDatabaseAsync(string workspace, string dataset, CancellationToken ct) =>
        FindWithRetryAsync(async () =>
        {
            var server = await ConnectAsync(workspace, ct).ConfigureAwait(false);
            try
            {
                var database = await Task.Run(() => Guard(() => server.Databases.FindByName(dataset)), ct).ConfigureAwait(false);
                if (database is not null) return ((Server, Database)?)(server, database);
            }
            catch
            {
                server.Dispose();
                throw;
            }
            server.Dispose();
            return null;
        }, _lookupRetryDelay, () => DatasetNotFound(dataset), ct);

    /// <summary>Runs <paramref name="attempt"/>; on a miss (null) waits <paramref name="delay"/> and tries exactly once more, then throws <paramref name="notFound"/>.</summary>
    internal static async Task<T> FindWithRetryAsync<T>(Func<Task<T?>> attempt, TimeSpan delay, Func<BridgeException> notFound, CancellationToken ct) where T : struct
    {
        if (await attempt().ConfigureAwait(false) is { } first) return first;
        await Task.Delay(delay, ct).ConfigureAwait(false);
        return await attempt().ConfigureAwait(false) ?? throw notFound();
    }

    /// <summary>
    /// The one place that decides how TOM authenticates. Currently the MSAL token (no secret in the connection string).
    /// If the live check shows the token is not accepted, switch to <c>User ID=app:{clientId}@{tenantId};Password={secret}</c>
    /// (quote values like <see cref="TokenAdomdConnectionFactory.BuildConnectionString"/> and register the secret with the scrubber) here only.
    /// </summary>
    private async Task<Server> ConnectAsync(string workspace, CancellationToken ct)
    {
        var token = await tokens.GetTokenAsync(ct).ConfigureAwait(false);
        var server = new Server { AccessToken = new Microsoft.AnalysisServices.AccessToken(token.Token, token.ExpiresOn, null) };
        var connectionString = TokenAdomdConnectionFactory.BuildConnectionString(config, workspace, null);
        var timeout = TimeSpan.FromSeconds(config.QueryTimeoutSeconds);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var connect = Task.Run(() => server.Connect(connectionString), CancellationToken.None);
        _ = connect.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        try
        {
            await connect.WaitAsync(cts.Token).ConfigureAwait(false);
            return server;
        }
        catch (OperationCanceledException)
        {
            server.Dispose();
            if (ct.IsCancellationRequested) throw;
            throw new BridgeException(ErrorKind.Xmla, $"XMLA connection timed out after {timeout.TotalSeconds:0} s.");
        }
        catch (Exception ex)
        {
            server.Dispose();
            throw new BridgeException(ErrorKind.Xmla, MapError("XMLA connection failed: " + scrubber.Scrub(EngineMessage.Clean(ex.Message))));
        }
    }

    private T Guard<T>(Func<T> body)
    {
        try { return body(); }
        catch (BridgeException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BridgeException(ErrorKind.Xmla, MapError(scrubber.Scrub(EngineMessage.Clean(ex.Message))));
        }
    }

    /// <summary>Prefixes an actionable explanation for the common write failures; the (already scrubbed) engine text is always kept.</summary>
    public static string MapError(string scrubbedMessage)
    {
        bool Has(params string[] parts) => parts.Any(p => scrubbedMessage.Contains(p, StringComparison.OrdinalIgnoreCase));
        string? advice = null;
        if (Has("read-only", "read only", "readonly", "ReadOnly"))
            advice = "The XMLA endpoint is read-only. In the Power BI admin portal set the capacity's XMLA Endpoint to Read Write (Capacity settings > Power BI workloads).";
        else if (Has("enhanced metadata", "enhanced dataset metadata", "V3 metadata", "compatibility level"))
            advice = "This model cannot be changed over XMLA (for example it is Desktop-authored without enhanced metadata). Re-publish it with enhanced metadata enabled, or edit it in Desktop.";
        else if (Has("permission", "not authorized", "unauthorized", "access denied", "does not have access", "forbidden"))
            advice = "The service principal needs Write permission on the dataset (Contributor, Member or Admin in the workspace, or Write on the dataset) and XMLA write access.";
        return advice is null ? scrubbedMessage : $"{advice} Engine message: {scrubbedMessage}";
    }
}
