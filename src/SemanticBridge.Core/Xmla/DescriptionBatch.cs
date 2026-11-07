using System.Text.Json;
using SemanticBridge.Core.Safety;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace SemanticBridge.Core.Xmla;

/// <summary>
/// Parses a batch file: a list of items, each with exactly one of <c>table</c>, <c>column</c> (<c>Table[Column]</c>) or <c>measure</c>,
/// plus <c>description</c>. YAML or JSON. Errors name the item number and the problem, never the values.
/// </summary>
public static class DescriptionBatch
{
    private static readonly string[] Keys = ["table", "column", "measure", "description"];

    public static IReadOnlyList<DescriptionUpdate> Parse(string text, bool json)
    {
        List<Dictionary<string, string?>>? items;
        try
        {
            items = json
                ? JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(text)
                : new DeserializerBuilder().Build().Deserialize<List<Dictionary<string, string?>>>(text);
        }
        catch (Exception ex) when (ex is JsonException or YamlException)
        {
            // Library messages can quote file content; say only where parsing stopped.
            var where = ex is YamlException y ? $" (line {y.Start.Line})" : ex is JsonException { LineNumber: { } line } ? $" (line {line + 1})" : string.Empty;
            throw new BridgeException(ErrorKind.Usage,
                $"The batch file is not a valid {(json ? "JSON" : "YAML")} list of {{table|column|measure, description}} items{where}. Every value must be text.");
        }
        if (items is null || items.Count == 0)
            throw new BridgeException(ErrorKind.Usage, "The batch file contains no items.");

        var updates = new List<DescriptionUpdate>();
        for (var n = 0; n < items.Count; n++)
        {
            var item = items[n];
            var label = $"Batch item {n + 1}";
            if (item is null) throw new BridgeException(ErrorKind.Usage, $"{label} is empty.");
            var map = new Dictionary<string, string?>(item, StringComparer.OrdinalIgnoreCase);
            if (map.Keys.FirstOrDefault(k => !Keys.Contains(k, StringComparer.OrdinalIgnoreCase)) is { } unknown)
                throw new BridgeException(ErrorKind.Usage, $"{label} has an unknown key '{unknown}'. Allowed: table, column, measure, description.");
            if (!map.TryGetValue("description", out var description) || description is null)
                throw new BridgeException(ErrorKind.Usage, $"{label} has no 'description'.");
            var targets = new[] { "table", "column", "measure" }.Where(k => map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)).ToList();
            if (targets.Count != 1)
                throw new BridgeException(ErrorKind.Usage, $"{label} must have exactly one of table, column or measure.");
            try
            {
                var target = targets[0] switch
                {
                    "table" => DescriptionTargets.Table(map["table"]!),
                    "column" => DescriptionTargets.ParseColumn(map["column"]!),
                    _ => DescriptionTargets.Measure(map["measure"]!),
                };
                updates.Add(new DescriptionUpdate(target, description));
            }
            catch (BridgeException ex) when (ex.Kind == ErrorKind.Usage)
            {
                throw new BridgeException(ErrorKind.Usage, $"{label}: {ex.Message}");
            }
        }
        return updates;
    }
}
