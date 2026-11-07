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

public sealed record RefreshEntry(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("refresh_type")] string RefreshType,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("start_time")] string StartTime,
    [property: JsonPropertyName("end_time")] string EndTime,
    [property: JsonPropertyName("error")] string? Error)
{
    /// <summary>"Unknown" means the refresh is still running; the others are final.</summary>
    [JsonIgnore]
    public bool IsFinished => Status is "Completed" or "Failed" or "Disabled" or "Cancelled";
}
