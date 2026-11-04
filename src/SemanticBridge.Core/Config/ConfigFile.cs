namespace SemanticBridge.Core.Config;

/// <summary>Shape of config.yaml. Property names map to snake_case keys; unknown keys are rejected on load.</summary>
public sealed class ConfigFile
{
    public string? DefaultProfile { get; set; }

    public Dictionary<string, ProfileSection?>? Profiles { get; set; }
}

public sealed class ProfileSection
{
    public string? Cloud { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? ClientSecretEnv { get; set; }
    public string? AuthorityHost { get; set; }
    public string? RestBaseUrl { get; set; }
    public string? XmlaBase { get; set; }
    public string? TokenScope { get; set; }
    public string? DefaultWorkspace { get; set; }
    public bool? AllowWrite { get; set; }
    public int? MaxRows { get; set; }
    public int? MaxRowsCeiling { get; set; }
    public int? QueryTimeoutSeconds { get; set; }
    public string? DefaultVia { get; set; }
}
