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
        var widths = headers.Select((h, i) => Math.Max(DisplayWidth.Of(Escape(h)), cells.Select(r => DisplayWidth.Of(r[i])).DefaultIfEmpty(0).Max())).ToArray();
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
            var padding = new string(' ', Math.Max(0, widths[i] - DisplayWidth.Of(cells[i])));
            if (numeric) sb.Append(padding).Append(cells[i]);
            else if (i == cells.Length - 1) sb.Append(cells[i]);
            else sb.Append(cells[i]).Append(padding);
        }
        sb.AppendLine();
    }

    private static string Escape(string s) => s.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}

/// <summary>Terminal column width of text: East Asian wide and fullwidth characters take 2 columns, combining marks 0.</summary>
public static class DisplayWidth
{
    public static int Of(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
            width += Of(rune);
        return width;
    }

    private static int Of(Rune rune)
    {
        var cp = rune.Value;
        if (cp is >= 0x0300 and <= 0x036F or >= 0x200B and <= 0x200F or >= 0xFE00 and <= 0xFE0F) return 0;
        if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) return 0;
        return IsWide(cp) ? 2 : 1;
    }

    private static bool IsWide(int cp) =>
        cp is >= 0x1100 and <= 0x115F          // Hangul Jamo
            or >= 0x2E80 and <= 0x303E         // CJK radicals, Kangxi, symbols and punctuation
            or >= 0x3041 and <= 0x33FF         // Hiragana, Katakana, Bopomofo, Hangul compat Jamo, CJK compatibility
            or >= 0x3400 and <= 0x4DBF         // CJK Extension A
            or >= 0x4E00 and <= 0x9FFF         // CJK Unified Ideographs
            or >= 0xA000 and <= 0xA4CF         // Yi
            or >= 0xAC00 and <= 0xD7A3         // Hangul syllables
            or >= 0xF900 and <= 0xFAFF         // CJK Compatibility Ideographs
            or >= 0xFE30 and <= 0xFE6F         // CJK Compatibility Forms, small forms
            or >= 0xFF00 and <= 0xFF60         // Fullwidth forms
            or >= 0xFFE0 and <= 0xFFE6         // Fullwidth signs
            or >= 0x20000 and <= 0x3FFFD;      // CJK Extensions B and later
}
