using SemanticBridge.Core.Results;

namespace SemanticBridge.Core.Xmla;

/// <summary>
/// XMLA operations. <c>workspace</c> is the name used in the XMLA Data Source (not an ID) and <c>dataset</c> the database name.
/// Tests substitute a fake to cover path selection.
/// </summary>
public interface IXmlaClient
{
    Task<QueryResult> QueryAsync(string workspace, string dataset, string dax, int maxRows, CancellationToken ct);

    Task<ModelSchema> GetSchemaAsync(string workspace, string dataset, string? tableFilter, CancellationToken ct);

    /// <summary>Opens (and closes) a connection to the workspace, optionally scoped to a dataset.</summary>
    Task TestConnectionAsync(string workspace, string? dataset, CancellationToken ct);
}
