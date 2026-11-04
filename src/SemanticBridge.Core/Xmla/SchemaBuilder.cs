using System.Data;
using System.Globalization;
using SemanticBridge.Core.Results;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Core.Xmla;

/// <summary>One metadata row with normalized column names (INFO.* returns "[Name]", rowsets return "Name").</summary>
public sealed class MetaRow(IReadOnlyDictionary<string, object?> values)
{
    public string? Str(string key) => values.TryGetValue(key, out var v) && v is not null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

    public int? Int(string key)
    {
        if (!values.TryGetValue(key, out var v) || v is null) return null;
        return v is bool b ? (b ? 1 : 0) : int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    public bool Bool(string key)
    {
        if (!values.TryGetValue(key, out var v) || v is null) return false;
        return v is bool b ? b : bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out var parsed) && parsed;
    }

    public bool Has(string key) => values.ContainsKey(key);

    public static string Normalize(string columnName)
    {
        var open = columnName.LastIndexOf('[');
        var close = columnName.LastIndexOf(']');
        return open >= 0 && close > open ? columnName[(open + 1)..close] : columnName;
    }

    public static IReadOnlyList<MetaRow> From(QueryResult result) =>
        [.. result.Rows.Select(r => new MetaRow(result.Columns.Select((c, i) => (Key: Normalize(c.Name), Value: r[i]))
            .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value)))];

    public static IReadOnlyList<MetaRow> From(DataTable table) =>
        [.. table.Rows.Cast<DataRow>().Select(r => new MetaRow(table.Columns.Cast<DataColumn>()
            .ToDictionary(c => c.ColumnName, c => r[c] is DBNull ? null : (object?)r[c])))];
}

/// <summary>Builds <see cref="ModelSchema"/> from the four metadata row sets. Pure logic, shared by the INFO.* and TMSCHEMA_* paths.</summary>
public static class SchemaBuilder
{
    private const int ColumnTypeRowNumber = 3;

    public static ModelSchema Build(
        IReadOnlyList<MetaRow> tables,
        IReadOnlyList<MetaRow> columns,
        IReadOnlyList<MetaRow> measures,
        IReadOnlyList<MetaRow> relationships,
        string? tableFilter,
        long elapsedMs,
        IReadOnlyList<string> warnings)
    {
        var visibleTables = tables.Where(t => !t.Bool("IsPrivate")).ToList();
        // Rows without an ID cannot be referenced by a relationship; duplicate IDs keep the first row.
        var tableNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in visibleTables)
            if (t.Str("ID") is { Length: > 0 } tableId) tableNames.TryAdd(tableId, t.Str("Name") ?? string.Empty);

        var columnNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in columns)
            if (c.Str("ID") is { Length: > 0 } columnId) columnNames.TryAdd(columnId, ColumnName(c));
        var columnsByTable = columns
            .Where(c => c.Int("Type") != ColumnTypeRowNumber)
            .GroupBy(c => c.Str("TableID") ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.ToList());
        var measuresByTable = measures
            .GroupBy(m => m.Str("TableID") ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<SchemaTable>();
        foreach (var t in visibleTables)
        {
            var id = t.Str("ID") ?? string.Empty;
            result.Add(new SchemaTable(
                t.Str("Name") ?? string.Empty,
                Blank(t.Str("Description")),
                t.Bool("IsHidden"),
                [.. columnsByTable.GetValueOrDefault(id, []).Select(c => new SchemaColumn(
                    ColumnName(c), DataTypeName(c.Int("ExplicitDataType") is > 0 ? c.Int("ExplicitDataType") : c.Int("InferredDataType")),
                    Blank(c.Str("Description")), c.Bool("IsHidden"))).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)],
                [.. measuresByTable.GetValueOrDefault(id, []).Select(m => new SchemaMeasure(
                    m.Str("Name") ?? string.Empty, Blank(m.Str("Expression")), DataTypeName(m.Int("DataType")),
                    Blank(m.Str("FormatString")), Blank(m.Str("Description")), m.Bool("IsHidden"))).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)]));
        }
        result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        var rels = new List<SchemaRelationship>();
        foreach (var r in relationships)
        {
            var fromTable = tableNames.GetValueOrDefault(r.Str("FromTableID") ?? string.Empty);
            var toTable = tableNames.GetValueOrDefault(r.Str("ToTableID") ?? string.Empty);
            if (fromTable is null || toTable is null) continue;
            rels.Add(new SchemaRelationship(
                fromTable, columnNames.GetValueOrDefault(r.Str("FromColumnID") ?? string.Empty, string.Empty), Cardinality(r.Int("FromCardinality")),
                toTable, columnNames.GetValueOrDefault(r.Str("ToColumnID") ?? string.Empty, string.Empty), Cardinality(r.Int("ToCardinality")),
                CrossFilter(r.Int("CrossFilteringBehavior")), !r.Has("IsActive") || r.Bool("IsActive")));
        }

        if (!string.IsNullOrWhiteSpace(tableFilter))
        {
            var wanted = tableFilter.Trim();
            var match = result.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new BridgeException(ErrorKind.NotFound,
                    $"Table '{wanted}' was not found in the model. Tables: {string.Join(", ", result.Select(t => t.Name))}.");
            result = [match];
            rels = [.. rels.Where(r => string.Equals(r.FromTable, match.Name, StringComparison.Ordinal) || string.Equals(r.ToTable, match.Name, StringComparison.Ordinal))];
        }

        return new ModelSchema { Tables = result, Relationships = rels, ElapsedMs = elapsedMs, Warnings = warnings };
    }

    private static string ColumnName(MetaRow c) => Blank(c.Str("ExplicitName")) ?? Blank(c.Str("InferredName")) ?? string.Empty;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public static string DataTypeName(int? type) => type switch
    {
        1 => "Automatic",
        2 => "String",
        6 => "Int64",
        8 => "Double",
        9 => "DateTime",
        10 => "Decimal",
        11 => "Boolean",
        17 => "Binary",
        19 => "Variant",
        null or 0 => "Unknown",
        _ => $"Unknown({type})",
    };

    private static string Cardinality(int? v) => v switch { 1 => "one", 2 => "many", _ => "unknown" };

    private static string CrossFilter(int? v) => v switch { 1 => "single", 2 => "both", 3 => "automatic", _ => "unknown" };
}
