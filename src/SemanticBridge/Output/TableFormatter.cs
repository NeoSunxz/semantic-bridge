using System.Globalization;
using System.Text;

namespace SemanticBridge.Cli.Output;

public static class ValueText
{
    /// <summary>Culture-invariant text for a result value. Null becomes an empty string.</summary>
    public static string Of(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    public static bool IsNumeric(object? value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;
}

/// <summary>Plain-text aligned table. Cells are never shortened; line breaks inside a cell are shown as \n.</summary>
public static class TableFormatter
{
    public static string Render(IReadOnlyList<string> headers, IReadOnlyList<object?[]> rows)
    {
        var cells = rows.Select(r => r.Select(v => v is null ? "NULL" : Escape(ValueText.Of(v))).ToArray()).ToList();
        var widths = headers.Select((h, i) => Math.Max(Escape(h).Length, cells.Select(r => r[i].Length).DefaultIfEmpty(0).Max())).ToArray();
        var sb = new StringBuilder();
        AppendLine(sb, headers.Select(Escape).ToArray(), widths, null);
        sb.AppendLine(string.Join("-+-", widths.Select(w => new string('-', w))));
        for (var r = 0; r < cells.Count; r++)
            AppendLine(sb, cells[r], widths, rows[r]);
        return sb.ToString();
    }

    private static void AppendLine(StringBuilder sb, string[] cells, int[] widths, object?[]? raw)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0) sb.Append(" | ");
            var numeric = raw is not null && ValueText.IsNumeric(raw[i]);
            sb.Append(numeric ? cells[i].PadLeft(widths[i]) : (i == cells.Length - 1 ? cells[i] : cells[i].PadRight(widths[i])));
        }
        sb.AppendLine();
    }

    private static string Escape(string s) => s.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
