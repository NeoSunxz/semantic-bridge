using System.Text;

namespace SemanticBridge.Cli.Output;

/// <summary>RFC 4180 CSV: CRLF line endings, fields with comma, quote, CR or LF are quoted, quotes doubled. Null is an empty field.</summary>
public static class CsvFormatter
{
    public static string Render(IReadOnlyList<string> headers, IReadOnlyList<object?[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", headers.Select(Field))).Append("\r\n");
        foreach (var row in rows)
            sb.Append(string.Join(",", row.Select(v => Field(ValueText.Of(v))))).Append("\r\n");
        return sb.ToString();
    }

    public static string Field(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}
