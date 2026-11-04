using SemanticBridge.Core.Safety;

namespace SemanticBridge.Core.Rest;

/// <summary>Resolves a user-supplied name or ID against a list. IDs match first; names match case-insensitively.</summary>
public static class NameResolver
{
    public static T Resolve<T>(IReadOnlyList<T> items, string input, string kind, Func<T, string> getId, Func<T, string> getName)
    {
        var wanted = input.Trim();
        var byId = items.Where(i => string.Equals(getId(i), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byId.Count == 1) return byId[0];

        var byName = items.Where(i => string.Equals(getName(i), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0];
        if (byName.Count > 1)
        {
            var candidates = string.Join("; ", byName.Select(i => $"{getName(i)} ({getId(i)})"));
            throw new BridgeException(ErrorKind.Ambiguous,
                $"{kind} name '{wanted}' is ambiguous. Use the ID instead. Candidates: {candidates}.");
        }

        var sample = string.Join(", ", items.Take(10).Select(getName));
        var more = items.Count > 10 ? $", ... ({items.Count} total)" : string.Empty;
        throw new BridgeException(ErrorKind.NotFound,
            $"{kind} '{wanted}' was not found among the {items.Count} visible to this service principal"
            + (items.Count > 0 ? $" ({sample}{more})." : ".")
            + " Check the name/ID and that the app has access.");
    }
}
