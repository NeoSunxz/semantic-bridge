using System.Text.Json.Serialization;

namespace SemanticBridge.Core.Xmla;

public enum TargetKind { Table, Column, Measure }

/// <summary>An object whose description can be changed. <c>Table</c> is null for a table target and, until resolved, for a measure target.</summary>
public sealed record DescriptionTarget(TargetKind Kind, string? Table, string Name)
{
    /// <summary>Human-readable form: <c>Table</c>, <c>Table[Column]</c> or <c>[Measure]</c> (with its table once resolved).</summary>
    public string Display => Kind switch
    {
        TargetKind.Table => Name,
        TargetKind.Column => $"{Table}[{Name}]",
        _ => Table is null ? $"[{Name}]" : $"{Table}[{Name}] (measure)",
    };
}

public sealed record DescriptionUpdate(DescriptionTarget Target, string Description);

/// <summary>One model object as read from the live model. For a table, <c>Table</c> and <c>Name</c> are both the table name.</summary>
public sealed record ModelObjectInfo(TargetKind Kind, string Table, string Name, string? Description);

public sealed record DescriptionChange(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("table")] string Table,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("old")] string? Old,
    [property: JsonPropertyName("new")] string New)
{
    [JsonIgnore] public TargetKind Kind => Enum.Parse<TargetKind>(Type, ignoreCase: true);

    [JsonIgnore] public string Display => Kind == TargetKind.Table ? Name : Kind == TargetKind.Column ? $"{Table}[{Name}]" : $"{Table}[{Name}] (measure)";

    /// <summary>Compared ordinally; a null old description equals an empty new one.</summary>
    [JsonIgnore] public bool IsUnchanged => string.Equals(Old ?? string.Empty, New, StringComparison.Ordinal);
}

/// <summary>The outcome of planning: what would change, and what already has the requested text.</summary>
public sealed class DescriptionPlan
{
    [JsonPropertyName("workspace")] public required string Workspace { get; init; }
    [JsonPropertyName("dataset")] public required string Dataset { get; init; }
    [JsonIgnore] public string XmlaWorkspace { get; init; } = string.Empty;
    [JsonPropertyName("changes")] public required IReadOnlyList<DescriptionChange> Changes { get; init; }
    [JsonPropertyName("unchanged")] public required IReadOnlyList<DescriptionChange> Unchanged { get; init; }
}
