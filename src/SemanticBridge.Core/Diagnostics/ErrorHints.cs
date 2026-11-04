using System.Text.RegularExpressions;

namespace SemanticBridge.Core.Diagnostics;

/// <summary>Maps common failure messages to an actionable hint. Pure text matching; never sees secrets (callers pass scrubbed text).</summary>
public static partial class ErrorHints
{
    public static string? For(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;

        if (message.Contains("AADSTS7000215", StringComparison.Ordinal))
            return "The client secret is invalid. Use the secret VALUE (not the secret ID) and check it matches client_id.";
        if (message.Contains("AADSTS7000222", StringComparison.Ordinal))
            return "The client secret has expired. Create a new secret in the app registration.";
        if (message.Contains("AADSTS700016", StringComparison.Ordinal))
            return "The application was not found in this tenant. Check client_id, tenant_id and that 'cloud' matches where the app is registered.";
        if (message.Contains("AADSTS90002", StringComparison.Ordinal) || message.Contains("AADSTS900023", StringComparison.Ordinal))
            return "The tenant was not found. Check tenant_id and that 'cloud' (authority) matches the tenant's cloud (global vs china).";
        if (message.Contains("AADSTS500011", StringComparison.Ordinal) || message.Contains("AADSTS70011", StringComparison.Ordinal) || message.Contains("AADSTS500021", StringComparison.Ordinal))
            return "The token scope or resource is not valid for this tenant. Check 'cloud' and token_scope.";
        if (AadstsCode().IsMatch(message))
            return "Azure AD rejected the sign-in. Look up the AADSTS code at https://login.microsoftonline.com/error (global) and check tenant_id, client_id, client_secret and cloud.";
        if (message.Contains("XMLA", StringComparison.OrdinalIgnoreCase)
            && (message.Contains("disabled", StringComparison.OrdinalIgnoreCase) || message.Contains("read", StringComparison.OrdinalIgnoreCase) || message.Contains("capacity", StringComparison.OrdinalIgnoreCase)))
            return "The XMLA endpoint is not available. The workspace must be on Premium, PPU, Embedded or Fabric capacity with the XMLA endpoint set to Read (or Read Write), and the tenant setting 'Allow XMLA endpoints and Analyze in Excel' must be enabled for the service principal.";
        if (message.Contains("401", StringComparison.Ordinal) || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            return "The token was rejected. Check that 'cloud' (authority and token scope) matches the tenant's cloud.";
        if (message.Contains("403", StringComparison.Ordinal) || message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
            return "Access denied. In the Power BI/Fabric admin portal enable 'Service principals can use Power BI/Fabric APIs' (and 'Dataset Execute Queries REST API' for REST queries) for a security group containing the app, and add the app to the workspace (Member or higher; Viewer cannot read model metadata).";
        if (message.Contains("No such host", StringComparison.OrdinalIgnoreCase) || message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase))
            return "The host could not be resolved. Check the network/proxy and the endpoint settings of the cloud.";
        return null;
    }

    [GeneratedRegex(@"AADSTS\d+")]
    private static partial Regex AadstsCode();
}
