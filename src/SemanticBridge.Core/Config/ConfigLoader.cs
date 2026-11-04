using System.Text.RegularExpressions;
using SemanticBridge.Core.Safety;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SemanticBridge.Core.Config;

/// <summary>Values that come from command-line flags and take part in precedence.</summary>
public sealed record ConfigLoadOptions(string? ConfigPath = null, string? Profile = null);

/// <summary>
/// Resolves the effective configuration. Precedence, highest first:
/// command-line flag (--config, --profile) &gt; environment variable (SBRIDGE_*) &gt; config file &gt; cloud preset / built-in default.
/// With no profile in the file, SBRIDGE_TENANT_ID / SBRIDGE_CLIENT_ID / SBRIDGE_CLIENT_SECRET / SBRIDGE_CLOUD alone are
/// enough ("env-only" setup for Docker and CI). With a profile selected, those variables override the profile's values.
/// </summary>
public static partial class ConfigLoader
{
    public const string EnvConfig = "SBRIDGE_CONFIG";
    public const string EnvProfile = "SBRIDGE_PROFILE";
    public const string EnvTenantId = "SBRIDGE_TENANT_ID";
    public const string EnvClientId = "SBRIDGE_CLIENT_ID";
    public const string EnvClientSecret = "SBRIDGE_CLIENT_SECRET";
    public const string EnvCloud = "SBRIDGE_CLOUD";
    public const string EnvOnlyProfileName = "(env)";

    public static string DefaultConfigPath(Func<string, string?> getEnv)
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = getEnv("APPDATA");
            if (string.IsNullOrWhiteSpace(appData)) appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "semantic-bridge", "config.yaml");
        }
        var home = getEnv("HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "semantic-bridge", "config.yaml");
    }

    public static ResolvedConfig Load(ConfigLoadOptions options, Func<string, string?> getEnv)
    {
        string? E(string name) => string.IsNullOrWhiteSpace(getEnv(name)) ? null : getEnv(name)!.Trim();

        // 1. Locate the file.
        string path;
        var explicitPath = true;
        if (!string.IsNullOrWhiteSpace(options.ConfigPath)) path = options.ConfigPath;
        else if (E(EnvConfig) is { } envPath) path = envPath;
        else { path = DefaultConfigPath(getEnv); explicitPath = false; }

        ConfigFile? file = null;
        var found = File.Exists(path);
        if (found) file = Parse(path);
        else if (explicitPath)
            throw new BridgeException(ErrorKind.Config, $"Config file not found: {path}. Run 'sbridge config init --config {path}' to create it.");

        // 2. Pick the profile.
        var profiles = file?.Profiles ?? [];
        var requested = !string.IsNullOrWhiteSpace(options.Profile) ? options.Profile : E(EnvProfile);
        var requestedSource = !string.IsNullOrWhiteSpace(options.Profile) ? "--profile" : $"env {EnvProfile}";
        string profileName;
        string profileSource;
        ProfileSection section;
        if (requested is not null)
        {
            (profileName, section) = Lookup(profiles, requested, requestedSource);
            profileSource = requestedSource;
        }
        else if (!string.IsNullOrWhiteSpace(file?.DefaultProfile))
        {
            (profileName, section) = Lookup(profiles, file.DefaultProfile, "default_profile");
            profileSource = "default_profile";
        }
        else if (profiles.Count == 1)
        {
            var only = profiles.First();
            profileName = only.Key;
            section = only.Value ?? new ProfileSection();
            profileSource = "only profile in file";
        }
        else if (profiles.Count > 1)
        {
            throw new BridgeException(ErrorKind.Config,
                $"Config file {path} has several profiles ({string.Join(", ", profiles.Keys)}) and no default_profile. Set default_profile or pass --profile.");
        }
        else
        {
            if (E(EnvTenantId) is null && E(EnvClientId) is null && E(EnvClientSecret) is null)
                throw new BridgeException(ErrorKind.Config,
                    $"No configuration found. Run 'sbridge config init' to create {path}, or set {EnvTenantId}, {EnvClientId} and {EnvClientSecret} (and optionally {EnvCloud}).");
            profileName = EnvOnlyProfileName;
            section = new ProfileSection();
            profileSource = "environment variables only";
        }

        var sources = new Dictionary<string, string>();
        var fileSource = $"file, profile '{profileName}'";

        // 3. Cloud and endpoints.
        string? cloudText;
        if (E(EnvCloud) is { } envCloud) { cloudText = envCloud; sources["cloud"] = $"env {EnvCloud}"; }
        else if (!string.IsNullOrWhiteSpace(section.Cloud)) { cloudText = section.Cloud; sources["cloud"] = fileSource; }
        else { cloudText = "global"; sources["cloud"] = "default"; }
        var cloud = ParseCloud(cloudText, sources["cloud"]);
        var preset = CloudPresets.For(cloud);

        string? authority = Endpoint("authority_host", section.AuthorityHost, preset?.AuthorityHost);
        string? scope = Endpoint("token_scope", section.TokenScope, preset?.TokenScope);
        string? rest = Endpoint("rest_base_url", section.RestBaseUrl, preset?.RestBaseUrl);
        string? xmla = Endpoint("xmla_base", section.XmlaBase, preset?.XmlaBase);

        string? Endpoint(string key, string? fileValue, string? presetValue)
        {
            if (!string.IsNullOrWhiteSpace(fileValue)) { sources[key] = fileSource; return fileValue.Trim(); }
            if (presetValue is not null) { sources[key] = $"preset {cloud.ToString().ToLowerInvariant()}"; return presetValue; }
            return null;
        }

        if (cloud == Cloud.Custom)
        {
            var missing = new[] { ("authority_host", authority), ("token_scope", scope), ("rest_base_url", rest), ("xmla_base", xmla) }
                .Where(p => p.Item2 is null).Select(p => p.Item1).ToArray();
            if (missing.Length > 0)
                throw new BridgeException(ErrorKind.Config,
                    $"cloud 'custom' requires authority_host, token_scope, rest_base_url and xmla_base; missing: {string.Join(", ", missing)}.");
        }
        RequireHttpsUrl("authority_host", authority!, cloud == Cloud.Custom);
        RequireHttpsUrl("rest_base_url", rest!, cloud == Cloud.Custom);
        if (!xmla!.Contains(CloudPresets.WorkspacePlaceholder, StringComparison.Ordinal))
            throw new BridgeException(ErrorKind.Config, $"xmla_base must contain the {CloudPresets.WorkspacePlaceholder} placeholder, e.g. powerbi://api.powerbi.com/v1.0/myorg/{{workspace}}.");

        // 4. Credentials.
        var tenant = Credential("tenant_id", EnvTenantId, section.TenantId);
        var client = Credential("client_id", EnvClientId, section.ClientId);
        string? secret;
        if (E(EnvClientSecret) is { } envSecret) { secret = envSecret; sources["client_secret"] = $"env {EnvClientSecret}"; }
        else if (!string.IsNullOrWhiteSpace(section.ClientSecretEnv) && E(section.ClientSecretEnv.Trim()) is { } indirect)
        {
            secret = indirect;
            sources["client_secret"] = $"env {section.ClientSecretEnv.Trim()} (client_secret_env)";
        }
        else if (!string.IsNullOrEmpty(section.ClientSecret)) { secret = section.ClientSecret; sources["client_secret"] = fileSource; }
        else
        {
            secret = null;
            sources["client_secret"] = string.IsNullOrWhiteSpace(section.ClientSecretEnv)
                ? "not set"
                : $"not set (client_secret_env names {section.ClientSecretEnv.Trim()}, which is not set)";
        }

        string? Credential(string key, string envName, string? fileValue)
        {
            if (E(envName) is { } fromEnv) { sources[key] = $"env {envName}"; return fromEnv; }
            if (!string.IsNullOrWhiteSpace(fileValue)) { sources[key] = fileSource; return fileValue.Trim(); }
            sources[key] = "not set";
            return null;
        }

        // 5. Behaviour and limits.
        string? workspace = null;
        if (!string.IsNullOrWhiteSpace(section.DefaultWorkspace)) { workspace = section.DefaultWorkspace.Trim(); sources["default_workspace"] = fileSource; }
        else sources["default_workspace"] = "not set";

        var allowWrite = section.AllowWrite ?? false;
        sources["allow_write"] = section.AllowWrite is null ? "default" : fileSource;

        var ceiling = section.MaxRowsCeiling ?? ResolvedConfig.DefaultMaxRowsCeiling;
        sources["max_rows_ceiling"] = section.MaxRowsCeiling is null ? "default" : fileSource;
        if (ceiling is < 1 or > ResolvedConfig.DefaultMaxRowsCeiling)
            throw new BridgeException(ErrorKind.Config, $"max_rows_ceiling must be between 1 and {ResolvedConfig.DefaultMaxRowsCeiling} (got {ceiling}).");

        var maxRows = section.MaxRows ?? ResolvedConfig.DefaultMaxRows;
        sources["max_rows"] = section.MaxRows is null ? "default" : fileSource;
        if (maxRows < 1)
            throw new BridgeException(ErrorKind.Config, $"max_rows must be at least 1 (got {maxRows}).");
        if (maxRows > ceiling)
            throw new BridgeException(ErrorKind.Config, $"max_rows ({maxRows}) is above max_rows_ceiling ({ceiling}). Lower max_rows or raise max_rows_ceiling (limit {ResolvedConfig.DefaultMaxRowsCeiling}).");

        var timeout = section.QueryTimeoutSeconds ?? ResolvedConfig.DefaultTimeoutSeconds;
        sources["query_timeout_seconds"] = section.QueryTimeoutSeconds is null ? "default" : fileSource;
        var clamped = Math.Clamp(timeout, ResolvedConfig.MinTimeoutSeconds, ResolvedConfig.MaxTimeoutSeconds);
        if (clamped != timeout)
            sources["query_timeout_seconds"] += $" (clamped from {timeout} to {clamped})";

        var via = QueryVia.Auto;
        sources["default_via"] = "default";
        if (!string.IsNullOrWhiteSpace(section.DefaultVia))
        {
            via = ParseVia(section.DefaultVia, "default_via");
            sources["default_via"] = fileSource;
        }

        return new ResolvedConfig
        {
            ConfigPath = path,
            ConfigFileFound = found,
            ProfileName = profileName,
            ProfileSource = profileSource,
            Cloud = cloud,
            TenantId = tenant,
            ClientId = client,
            ClientSecret = secret,
            Endpoints = new CloudEndpoints(authority!, scope!, rest!, xmla),
            DefaultWorkspace = workspace,
            AllowWrite = allowWrite,
            MaxRows = maxRows,
            MaxRowsCeiling = ceiling,
            QueryTimeoutSeconds = clamped,
            DefaultVia = via,
            Sources = sources,
        };
    }

    public static QueryVia ParseVia(string text, string what) => text.Trim().ToLowerInvariant() switch
    {
        "auto" => QueryVia.Auto,
        "xmla" => QueryVia.Xmla,
        "rest" => QueryVia.Rest,
        _ => throw new BridgeException(ErrorKind.Config, $"Invalid {what} '{text}': expected auto, xmla or rest."),
    };

    private static Cloud ParseCloud(string text, string source) => text.Trim().ToLowerInvariant() switch
    {
        "global" => Cloud.Global,
        "china" => Cloud.China,
        "custom" => Cloud.Custom,
        _ => throw new BridgeException(ErrorKind.Config, $"Invalid cloud '{text}' ({source}): expected global, china or custom."),
    };

    private static (string Name, ProfileSection Section) Lookup(Dictionary<string, ProfileSection?> profiles, string name, string source)
    {
        foreach (var (key, value) in profiles)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return (key, value ?? new ProfileSection());
        var available = profiles.Count == 0 ? "none defined" : string.Join(", ", profiles.Keys);
        throw new BridgeException(ErrorKind.Config, $"Profile '{name}' ({source}) not found. Available profiles: {available}.");
    }

    /// <summary>Requires an absolute https URL; plain http is accepted only for a loopback host in a custom cloud. The value is never echoed.</summary>
    private static void RequireHttpsUrl(string key, string value, bool allowLoopbackHttp)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttps) return;
            if (uri.Scheme == Uri.UriSchemeHttp && allowLoopbackHttp && uri.IsLoopback) return;
        }
        throw new BridgeException(ErrorKind.Config,
            $"{key} must be an absolute https URL (http is allowed only for a loopback host when cloud is 'custom').");
    }

    /// <summary>Parses a config file. Unknown keys and malformed YAML become config errors that never echo the file's values.</summary>
    public static ConfigFile Parse(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BridgeException(ErrorKind.Config, $"Cannot read config file {path}: {ex.Message}");
        }
        return ParseText(text, path);
    }

    public static ConfigFile ParseText(string text, string pathForErrors)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        try
        {
            return deserializer.Deserialize<ConfigFile?>(text) ?? new ConfigFile();
        }
        catch (YamlException ex)
        {
            var inner = ex.InnerException as YamlException ?? ex;
            var line = ex.Start.Line;
            var match = NotFoundProperty().Match(ex.Message);
            if (match.Success)
                throw new BridgeException(ErrorKind.Config, $"Unknown config key '{match.Groups[1].Value}' in {pathForErrors} (line {line}).");
            // Do not echo ex.Message wholesale: it can quote offending values.
            throw new BridgeException(ErrorKind.Config, $"Invalid config file {pathForErrors} (line {line}): {Generic(inner)}");
        }
    }

    private static string Generic(YamlException ex) =>
        ex.InnerException is FormatException or InvalidCastException ? "a value has the wrong type." : "the YAML could not be parsed.";

    [GeneratedRegex(@"Property '([^']+)' not found")]
    private static partial Regex NotFoundProperty();
}
