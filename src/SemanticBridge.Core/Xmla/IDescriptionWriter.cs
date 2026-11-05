namespace SemanticBridge.Core.Xmla;

/// <summary>
/// Reads and writes object descriptions on a live model through the XMLA read-write endpoint (TOM).
/// <c>workspace</c> is the XMLA workspace name and <c>dataset</c> the database name. Tests substitute a fake.
/// </summary>
public interface IDescriptionWriter
{
    /// <summary>Lists every table, column and measure with its current description.</summary>
    Task<IReadOnlyList<ModelObjectInfo>> ListAsync(string workspace, string dataset, CancellationToken ct);

    /// <summary>Applies all changes in one SaveChanges, so they succeed or fail together.</summary>
    Task ApplyAsync(string workspace, string dataset, IReadOnlyList<DescriptionChange> changes, CancellationToken ct);
}
