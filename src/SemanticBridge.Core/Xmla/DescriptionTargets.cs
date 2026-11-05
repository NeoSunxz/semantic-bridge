using System.Text;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Core.Xmla;

/// <summary>Parses and resolves description targets. Pure functions; no network.</summary>
public static class DescriptionTargets
{
    /// <summary>
    /// Parses <c>Table[Column]</c> or <c>'Table Name'[Column]</c>. A quoted table may contain <c>''</c> for an apostrophe;
    /// the column part is everything between the first <c>[</c> after the table and the last <c>]</c>, with <c>]]</c> meaning one <c>]</c>.
    /// </summary>
    public static DescriptionTarget ParseColumn(string input)
    {
        var s = (input ?? string.Empty).Trim();
        var table = new StringBuilder();
        var i = 0;
        if (s.StartsWith('\''))
        {
            i = 1;
            while (true)
            {
                if (i >= s.Length) throw BadColumn();
                if (s[i] == '\'')
                {
                    if (i + 1 < s.Length && s[i + 1] == '\'') { table.Append('\''); i += 2; continue; }
                    i++;
                    break;
                }
                table.Append(s[i++]);
            }
            if (i >= s.Length || s[i] != '[') throw BadColumn();
        }
        else
        {
            var open = s.IndexOf('[');
            if (open <= 0) throw BadColumn();
            table.Append(s[..open].Trim());
            i = open;
        }

        if (s[i] != '[' || !s.EndsWith(']') || s.Length - i < 3) throw BadColumn();
        var column = s[(i + 1)..^1].Replace("]]", "]", StringComparison.Ordinal);
        if (table.Length == 0 || column.Length == 0) throw BadColumn();
        return new DescriptionTarget(TargetKind.Column, table.ToString(), column);

        static BridgeException BadColumn() => new(ErrorKind.Usage,
            "A column must be given as Table[Column], e.g. Sales[Amount] or 'Sales Orders'[Order Date] (quote table names that contain brackets).");
    }

    public static DescriptionTarget Table(string name) => new(TargetKind.Table, null, RequireName(name, "table"));

    public static DescriptionTarget Measure(string name) => new(TargetKind.Measure, null, RequireName(name, "measure"));

    private static string RequireName(string name, string what) =>
        string.IsNullOrWhiteSpace(name) ? throw new BridgeException(ErrorKind.Usage, $"The {what} name is empty.") : name.Trim();

    /// <summary>
    /// Finds the object a target refers to. Exact (ordinal) matches win over case-insensitive ones.
    /// An ambiguous measure name lists the tables that define it; a miss lists close names.
    /// </summary>
    public static ModelObjectInfo Resolve(IReadOnlyList<ModelObjectInfo> model, DescriptionTarget target)
    {
        var ofKind = model.Where(o => o.Kind == target.Kind).ToList();
        List<ModelObjectInfo> Matches(Func<string, string, bool> eq) => target.Kind switch
        {
            TargetKind.Table => [.. ofKind.Where(o => eq(o.Name, target.Name))],
            TargetKind.Column => [.. ofKind.Where(o => eq(o.Table, target.Table!) && eq(o.Name, target.Name))],
            _ => [.. ofKind.Where(o => eq(o.Name, target.Name))],
        };

        var found = Matches((a, b) => string.Equals(a, b, StringComparison.Ordinal));
        if (found.Count == 0) found = Matches((a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase));
        if (found.Count == 1) return found[0];
        if (found.Count > 1)
            throw new BridgeException(ErrorKind.Ambiguous,
                $"{target.Kind} '{target.Name}' exists in more than one table ({string.Join(", ", found.Select(f => f.Table))}). "
                + "Measure names should be unique; rename one, or use the engine's exact name.");

        var wantedName = target.Name;
        var close = ofKind
            .Where(o => (target.Kind != TargetKind.Column || string.Equals(o.Table, target.Table, StringComparison.OrdinalIgnoreCase))
                        && (o.Name.Contains(wantedName, StringComparison.OrdinalIgnoreCase) || wantedName.Contains(o.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(o => o.Kind == TargetKind.Table ? o.Name : $"{o.Table}[{o.Name}]")
            .Take(5)
            .ToList();
        var hint = close.Count > 0 ? $" Close names: {string.Join(", ", close)}." : string.Empty;
        throw new BridgeException(ErrorKind.NotFound, $"{target.Kind} {target.Display} was not found in the model.{hint}");
    }

    /// <summary>Builds the change list: resolved targets with old text. A target listed twice is rejected.</summary>
    public static (IReadOnlyList<DescriptionChange> Changes, IReadOnlyList<DescriptionChange> Unchanged) Plan(
        IReadOnlyList<ModelObjectInfo> model, IReadOnlyList<DescriptionUpdate> updates)
    {
        var changes = new List<DescriptionChange>();
        var unchanged = new List<DescriptionChange>();
        var seen = new HashSet<(TargetKind, string, string)>();
        foreach (var u in updates)
        {
            var found = Resolve(model, u.Target);
            if (!seen.Add((found.Kind, found.Table, found.Name)))
                throw new BridgeException(ErrorKind.Usage, $"{found.Kind} {new DescriptionTarget(found.Kind, found.Table, found.Name).Display} is listed more than once.");
            var change = new DescriptionChange(found.Kind.ToString().ToLowerInvariant(), found.Table, found.Name, found.Description, u.Description);
            (change.IsUnchanged ? unchanged : changes).Add(change);
        }
        return (changes, unchanged);
    }
}
