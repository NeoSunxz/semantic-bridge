using System.Text;
using SemanticBridge.Core.Config;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Cli.Output;

/// <summary>Renders the resolved config for 'config show'. The client secret is never printed, not even partially.</summary>
public static class ConfigRenderer
{
    public static string Render(ResolvedConfig c)
    {
        var rows = new List<(string Key, string Value)>
        {
            ("cloud", c.Cloud.ToString().ToLowerInvariant()),
            ("tenant_id", c.TenantId ?? "(not set)"),
            ("client_id", c.ClientId ?? "(not set)"),
            ("client_secret", string.IsNullOrEmpty(c.ClientSecret) ? "(not set)" : SecretScrubber.Mask + " (set)"),
            ("authority_host", c.Endpoints.AuthorityHost),
            ("token_scope", c.Endpoints.TokenScope),
            ("rest_base_url", c.Endpoints.RestBaseUrl),
            ("xmla_base", c.Endpoints.XmlaBase),
            ("default_workspace", c.DefaultWorkspace ?? "(not set)"),
            ("default_dataset", c.DefaultDataset ?? "(not set)"),
            ("allow_write", c.AllowWrite ? "true" : "false"),
            ("max_rows", c.MaxRows.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("max_rows_ceiling", c.MaxRowsCeiling.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("query_timeout_seconds", c.QueryTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("default_via", c.DefaultVia.ToString().ToLowerInvariant()),
        };
        var sb = new StringBuilder();
        sb.AppendLine($"config file: {c.ConfigPath} ({(c.ConfigFileFound ? "found" : "not found")})");
        sb.AppendLine($"profile: {c.ProfileName} (from {c.ProfileSource})");
        var keyWidth = rows.Max(r => r.Key.Length);
        var valueWidth = rows.Max(r => r.Value.Length);
        foreach (var (key, value) in rows)
            sb.AppendLine($"{key.PadRight(keyWidth)}  {value.PadRight(valueWidth)}  [{c.Sources.GetValueOrDefault(key, "?")}]");
        return sb.ToString();
    }
}
