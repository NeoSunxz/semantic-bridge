using System.Text.Json.Serialization;

namespace SemanticBridge.Core.Results;

public sealed record ColumnInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type);

public static class ResultPaths
{
    public const string Xmla = "xmla";
    public const string Rest = "rest";
}

/// <summary>
/// Result of a DAX query. Column names are exactly what the engine returned. Each row is an array aligned with <see cref="Columns"/>.
/// </summary>
public sealed class QueryResult
{
    [JsonPropertyName("columns")] public required IReadOnlyList<ColumnInfo> Columns { get; init; }
    [JsonPropertyName("rows")] public required IReadOnlyList<object?[]> Rows { get; init; }
    [JsonPropertyName("row_count")] public int RowCount => Rows.Count;
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }
    [JsonPropertyName("truncation_reason")] public string? TruncationReason { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("elapsed_ms")] public long ElapsedMs { get; init; }
    [JsonPropertyName("warnings")] public IReadOnlyList<string> Warnings { get; init; } = [];

    public QueryResult WithWarnings(IEnumerable<string> extra) => new()
    {
        Columns = Columns,
        Rows = Rows,
        Truncated = Truncated,
        TruncationReason = TruncationReason,
        Path = Path,
        ElapsedMs = ElapsedMs,
        Warnings = [.. extra, .. Warnings],
    };
}
