namespace SemanticBridge.Core.Safety;

/// <summary>
/// Accepts only DAX queries: after leading whitespace and comments the text must start with
/// DEFINE or EVALUATE. This keeps XMLA/MDX/XML commands out of the query path.
/// </summary>
public static class DaxGuard
{
    public static void Validate(string? dax)
    {
        var rest = SkipLeadingTrivia(dax ?? string.Empty);
        if (StartsWithKeyword(rest, "EVALUATE") || StartsWithKeyword(rest, "DEFINE")) return;
        throw new BridgeException(ErrorKind.Usage, "Only DAX queries are allowed: the query must start with DEFINE or EVALUATE (comments and whitespace before it are ignored).");
    }

    private static string SkipLeadingTrivia(string s)
    {
        var i = 0;
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i]) || s[i] == '﻿') { i++; continue; }
            if (Starts(s, i, "//") || Starts(s, i, "--"))
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (Starts(s, i, "/*"))
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
                continue;
            }
            break;
        }
        return s[i..];
    }

    private static bool Starts(string s, int i, string token) => string.CompareOrdinal(s, i, token, 0, token.Length) == 0;

    private static bool StartsWithKeyword(string s, string keyword) =>
        s.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
        && (s.Length == keyword.Length || !(char.IsLetterOrDigit(s[keyword.Length]) || s[keyword.Length] == '_'));
}
