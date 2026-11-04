using System.Text;
using SemanticBridge.Core.Xmla;

namespace SemanticBridge.Cli.Output;

public static class SchemaFormatter
{
    public static string Render(ModelSchema schema)
    {
        var sb = new StringBuilder();
        foreach (var t in schema.Tables)
        {
            sb.Append("Table ").Append(t.Name);
            if (t.IsHidden) sb.Append(" (hidden)");
            sb.AppendLine();
            if (t.Description is not null) sb.Append("  ").AppendLine(OneLine(t.Description));
            if (t.Columns.Count > 0)
            {
                sb.AppendLine("  Columns:");
                foreach (var c in t.Columns)
                {
                    sb.Append("    ").Append(c.Name).Append(" : ").Append(c.DataType);
                    if (c.IsHidden) sb.Append(" (hidden)");
                    if (c.Description is not null) sb.Append("  -- ").Append(OneLine(c.Description));
                    sb.AppendLine();
                }
            }
            if (t.Measures.Count > 0)
            {
                sb.AppendLine("  Measures:");
                foreach (var m in t.Measures)
                {
                    sb.Append("    [").Append(m.Name).Append(']');
                    if (m.IsHidden) sb.Append(" (hidden)");
                    if (m.Description is not null) sb.Append("  -- ").Append(OneLine(m.Description));
                    sb.AppendLine();
                    if (m.Expression is not null) sb.Append("      = ").AppendLine(OneLine(m.Expression));
                }
            }
            sb.AppendLine();
        }
        if (schema.Relationships.Count > 0)
        {
            sb.AppendLine("Relationships:");
            foreach (var r in schema.Relationships)
            {
                sb.Append("  ").Append(r.FromTable).Append('[').Append(r.FromColumn).Append("] (").Append(r.FromCardinality).Append(") -> ")
                  .Append(r.ToTable).Append('[').Append(r.ToColumn).Append("] (").Append(r.ToCardinality).Append("), filter: ").Append(r.CrossFilter)
                  .Append(r.IsActive ? string.Empty : ", inactive").AppendLine();
            }
        }
        return sb.ToString();
    }

    private static string OneLine(string s) => s.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
