using System.Text.Json.Serialization;

namespace SemanticBridge.Core.Rest;

public sealed record Workspace(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("is_on_dedicated_capacity")] bool? IsOnDedicatedCapacity);

public sealed record Dataset(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("configured_by")] string? ConfiguredBy);
