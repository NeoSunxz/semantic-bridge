using System.Text.RegularExpressions;

namespace SemanticBridge.Core.Diagnostics;

/// <summary>Tidies engine error text (XMLA/TOM) for people and agents. Pure text; callers still scrub secrets.</summary>
public static partial class EngineMessage
{
    private const string TechnicalDetails = "Technical Details:";

    // Explicit list: DAX text in messages can legitimately contain < and > (for example <> or a<b).
    private static readonly string[] MarkupTags = ["<oii>", "</oii>", "<ccon>", "</ccon>"];

    /// <summary>
    /// Removes the &lt;oii&gt; and &lt;ccon&gt; markers (keeping the inner text) and collapses the "Technical Details:" block
    /// to a trailing " (RootActivityId: id)", or drops it when there is no id.
    /// </summary>
    public static string Clean(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        var text = message;
        foreach (var tag in MarkupTags)
            text = text.Replace(tag, string.Empty, StringComparison.Ordinal);
        var start = text.IndexOf(TechnicalDetails, StringComparison.Ordinal);
        if (start < 0) return text;
        var head = text[..start].TrimEnd();
        var id = RootActivityId().Match(text, start);
        return id.Success ? $"{head} (RootActivityId: {id.Groups[1].Value})" : head;
    }

    [GeneratedRegex(@"RootActivityId:\s*(\S+)")]
    private static partial Regex RootActivityId();
}
