using System.Net;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace SemanticBridge.Core.Safety;

/// <summary>
/// Removes secrets from any text that leaves Core. Known secret values (client secret, access tokens)
/// are registered at runtime; well-known shapes (passwords, bearer tokens, JWTs, connection strings)
/// are always removed.
/// </summary>
public sealed class SecretScrubber
{
    public const string Mask = "***";

    private static readonly Regex ConnectionString = new(
        @"(?i)\b(Data Source|Provider|Initial Catalog|User ID|Integrated Security)\s*=[^;\r\n]*(;\s*[A-Za-z][A-Za-z ]*=[^;\r\n]*)+;?",
        RegexOptions.Compiled);

    private static readonly Regex Password = new(@"(?i)\b(password|pwd)\s*=\s*[^;\r\n]*", RegexOptions.Compiled);

    private static readonly Regex Bearer = new(@"(?i)\bBearer\s+[A-Za-z0-9\-._~+/=]+", RegexOptions.Compiled);

    private static readonly Regex Jwt = new(@"eyJ[A-Za-z0-9_\-]*\.[A-Za-z0-9_\-]*\.[A-Za-z0-9_\-]*", RegexOptions.Compiled);

    private static readonly Regex KeyedSecret = new(
        @"(?i)\b(client_secret|access_token|refresh_token|id_token)(""?\s*[:=]\s*""?)[^&\s""',;]+",
        RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly List<string> _secrets = [];

    /// <summary>Registers a literal value that must never appear in output. Very short values are ignored.</summary>
    public void Register(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 4) return;
        lock (_gate)
        {
            foreach (var form in Forms(secret))
                if (form.Length >= 4 && !_secrets.Contains(form)) _secrets.Add(form);
        }
    }

    /// <summary>The secret as it appears raw and after JSON, URI and form-url-encoded escaping.</summary>
    private static IEnumerable<string> Forms(string secret)
    {
        yield return secret;
        yield return JavaScriptEncoder.Default.Encode(secret);
        yield return JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(secret);
        yield return secret.Replace("/", "\\/", StringComparison.Ordinal);
        yield return Uri.EscapeDataString(secret);
        yield return WebUtility.UrlEncode(secret);
    }

    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string[] known;
        lock (_gate) known = [.. _secrets];
        // Longest first so a secret that contains another one is masked completely.
        foreach (var secret in known.OrderByDescending(s => s.Length))
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        text = ConnectionString.Replace(text, "[connection string removed]");
        text = Password.Replace(text, "$1=" + Mask);
        text = KeyedSecret.Replace(text, "$1$2" + Mask);
        text = Bearer.Replace(text, "Bearer " + Mask);
        text = Jwt.Replace(text, Mask);
        return text;
    }
}
