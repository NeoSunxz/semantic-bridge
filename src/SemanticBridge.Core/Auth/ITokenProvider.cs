namespace SemanticBridge.Core.Auth;

/// <summary>An access token with the expiry reported by the identity provider.</summary>
public sealed record AccessTokenInfo(string Token, DateTimeOffset ExpiresOn)
{
    /// <summary>The compiler-generated ToString would print the token; mask it.</summary>
    public override string ToString() => $"AccessTokenInfo {{ Token = ***, ExpiresOn = {ExpiresOn:o} }}";
}

/// <summary>Source of Power BI access tokens. The seam for adding interactive sign-in later.</summary>
public interface ITokenProvider
{
    Task<AccessTokenInfo> GetTokenAsync(CancellationToken cancellationToken);
}
