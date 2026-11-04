namespace SemanticBridge.Core.Config;

public enum Cloud
{
    Global,
    China,
    Custom,
}

/// <summary>The four endpoints that define a cloud. <c>XmlaBase</c> contains a <c>{workspace}</c> placeholder.</summary>
public sealed record CloudEndpoints(string AuthorityHost, string TokenScope, string RestBaseUrl, string XmlaBase);

public static class CloudPresets
{
    public const string WorkspacePlaceholder = "{workspace}";

    public static readonly CloudEndpoints Global = new(
        "https://login.microsoftonline.com",
        "https://analysis.windows.net/powerbi/api/.default",
        "https://api.powerbi.com/v1.0/myorg",
        "powerbi://api.powerbi.com/v1.0/myorg/{workspace}");

    public static readonly CloudEndpoints China = new(
        "https://login.partner.microsoftonline.cn",
        "https://analysis.chinacloudapi.cn/powerbi/api/.default",
        "https://api.powerbi.cn/v1.0/myorg",
        "powerbi://api.powerbi.cn/v1.0/myorg/{workspace}");

    /// <summary>Returns the preset for a built-in cloud, or null for <see cref="Cloud.Custom"/>.</summary>
    public static CloudEndpoints? For(Cloud cloud) => cloud switch
    {
        Cloud.Global => Global,
        Cloud.China => China,
        _ => null,
    };
}
