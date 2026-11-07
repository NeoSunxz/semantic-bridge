using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Cli.Mcp;

/// <summary>
/// Checks a tool call's argument names and JSON types against the tool's input schema before invoking it.
/// Without it the SDK only answers "An error occurred invoking '...'", which does not tell the agent what to fix.
/// Only argument names are reported, never values.
/// </summary>
public sealed class ArgumentCheckedTool(McpServerTool inner, SecretScrubber scrubber) : DelegatingMcpServerTool(inner)
{
    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var problem = Check(ProtocolTool.InputSchema, request.Params?.Arguments);
        if (problem is null) return base.InvokeAsync(request, cancellationToken);
        return ValueTask.FromResult(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = scrubber.Scrub(problem) }] });
    }

    /// <summary>Returns a message naming the problem arguments, or null when the call fits the schema.</summary>
    internal static string? Check(JsonElement schema, IDictionary<string, JsonElement>? arguments)
    {
        var properties = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object
            ? p.EnumerateObject().ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal)
            : [];
        var required = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToArray()
            : [];
        var supplied = arguments ?? new Dictionary<string, JsonElement>();

        var missing = required.Where(n => !supplied.TryGetValue(n, out var v) || v.ValueKind == JsonValueKind.Null).ToArray();
        var unknown = supplied.Keys.Where(k => !properties.ContainsKey(k)).Order(StringComparer.Ordinal).ToArray();
        var wrongType = supplied
            .Where(kv => properties.TryGetValue(kv.Key, out var s) && !TypeMatches(s, kv.Value) && !missing.Contains(kv.Key))
            .Select(kv => $"{kv.Key} (expected {Describe(properties[kv.Key], kv.Value)})")
            .ToArray();
        if (missing.Length == 0 && unknown.Length == 0 && wrongType.Length == 0) return null;

        var parts = new List<string>();
        if (missing.Length > 0) parts.Add("Missing required argument(s): " + string.Join(", ", missing) + ".");
        if (unknown.Length > 0) parts.Add("Unknown argument(s): " + string.Join(", ", unknown) + ".");
        if (wrongType.Length > 0) parts.Add("Wrong type for argument(s): " + string.Join(", ", wrongType) + ".");
        parts.Add(properties.Count == 0 ? "This tool takes no arguments." : "Expected arguments: " + string.Join(", ", properties.Keys) + ".");
        return string.Join(' ', parts);
    }

    private static IEnumerable<string> Types(JsonElement property)
    {
        if (!property.TryGetProperty("type", out var type)) return [];
        return type.ValueKind switch
        {
            JsonValueKind.String => [type.GetString()!],
            JsonValueKind.Array => type.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToArray(),
            _ => [],
        };
    }

    private static string Describe(JsonElement property, JsonElement value)
    {
        var text = string.Join(" or ", Types(property).Where(t => t != "null"));
        // All integer parameters are 32-bit; say so when the value is a whole number that only fails the range.
        return text == "integer" && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _) ? "integer within 32-bit range" : text;
    }

    private static bool TypeMatches(JsonElement property, JsonElement value)
    {
        // A null for an optional argument means "not given".
        if (value.ValueKind == JsonValueKind.Null) return true;
        var types = Types(property).ToArray();
        return types.Length == 0 || types.Any(t => t switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "null" => false,
            _ => true,
        });
    }
}
