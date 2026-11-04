using System.Text.Json.Serialization;

namespace SemanticBridge.Core.Xmla;

public sealed record SchemaColumn(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("data_type")] string DataType,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("is_hidden")] bool IsHidden);

public sealed record SchemaMeasure(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("expression")] string? Expression,
    [property: JsonPropertyName("data_type")] string DataType,
    [property: JsonPropertyName("format_string")] string? FormatString,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("is_hidden")] bool IsHidden);

public sealed record SchemaTable(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("is_hidden")] bool IsHidden,
    [property: JsonPropertyName("columns")] IReadOnlyList<SchemaColumn> Columns,
    [property: JsonPropertyName("measures")] IReadOnlyList<SchemaMeasure> Measures);

public sealed record SchemaRelationship(
    [property: JsonPropertyName("from_table")] string FromTable,
    [property: JsonPropertyName("from_column")] string FromColumn,
    [property: JsonPropertyName("from_cardinality")] string FromCardinality,
    [property: JsonPropertyName("to_table")] string ToTable,
    [property: JsonPropertyName("to_column")] string ToColumn,
    [property: JsonPropertyName("to_cardinality")] string ToCardinality,
    [property: JsonPropertyName("cross_filter")] string CrossFilter,
    [property: JsonPropertyName("is_active")] bool IsActive);

public sealed class ModelSchema
{
    [JsonPropertyName("tables")] public required IReadOnlyList<SchemaTable> Tables { get; init; }
    [JsonPropertyName("relationships")] public required IReadOnlyList<SchemaRelationship> Relationships { get; init; }
    [JsonPropertyName("path")] public string Path { get; init; } = Results.ResultPaths.Xmla;
    [JsonPropertyName("elapsed_ms")] public long ElapsedMs { get; init; }
    [JsonPropertyName("warnings")] public IReadOnlyList<string> Warnings { get; init; } = [];
}
