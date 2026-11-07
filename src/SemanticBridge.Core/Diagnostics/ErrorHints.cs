using System.Text.RegularExpressions;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Core.Diagnostics;

/// <summary>
/// Maps failures to an actionable hint. Never sees secrets (callers pass scrubbed text). Matches structured signals first
/// (HTTP status, AADSTS code, error kind) and then specific Microsoft phrases; never bare digits or words inside free text,
/// so a DAX error that mentions "m401" or a GUID containing 401 gets no hint.
/// </summary>
public static partial class ErrorHints
{
    public static string? For(Exception ex, string scrubbedMessage) =>
        ex is BridgeException b ? For(scrubbedMessage, b.Kind, b.HttpStatus) : For(scrubbedMessage);

    public static string? For(string? message, ErrorKind? kind = null, int? httpStatus = null)
    {
        if (string.IsNullOrEmpty(message)) return null;
        // Engine errors about the user's own query text are not infrastructure problems.
        if (kind == ErrorKind.Dax) return null;

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
        if (kind == ErrorKind.Xmla && XmlaUnavailable().IsMatch(message))
            return "The XMLA endpoint is not available. The workspace must be on Premium, PPU, Embedded or Fabric capacity with the XMLA endpoint set to Read (or Read Write), and the tenant setting 'Allow XMLA endpoints and Analyze in Excel with on-premises semantic models' (Integration settings) must be enabled.";
        if (httpStatus == 401 || Http401().IsMatch(message))
            return "The token was rejected. Check that 'cloud' (authority and token scope) matches the tenant's cloud.";
        if (httpStatus == 403 || Http403().IsMatch(message))
            return "Access denied. In the Power BI/Fabric admin portal enable 'Allow service principals to use Power BI APIs' (Developer settings) and, for REST queries, 'Dataset Execute Queries REST API' (Integration settings) for a security group containing the app, and add the app to the workspace (Member or higher; Viewer cannot read model metadata).";
        if (message.Contains("No such host", StringComparison.OrdinalIgnoreCase) || message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase))
            return "The host could not be resolved. Check the network/proxy and the endpoint settings of the cloud.";
        return null;
    }

    [GeneratedRegex(@"AADSTS\d+")]
    private static partial Regex AadstsCode();

    // "XMLA" followed, anywhere in the message, by what is wrong with it. Only applied to errors of kind Xmla.
    [GeneratedRegex(@"\bXMLA\b[\s\S]*\b(disabled|not enabled|not available|read-only|read only|capacity)\b", RegexOptions.IgnoreCase)]
    private static partial Regex XmlaUnavailable();

    // The .NET web error text "The remote server returned an error: (401) Unauthorized." or the bare status word.
    [GeneratedRegex(@"error: \(401\)|\bUnauthorized\b", RegexOptions.IgnoreCase)]
    private static partial Regex Http401();

    [GeneratedRegex(@"error: \(403\)|\bForbidden\b", RegexOptions.IgnoreCase)]
    private static partial Regex Http403();
}
