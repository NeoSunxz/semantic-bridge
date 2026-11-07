using Microsoft.Identity.Client;
using SemanticBridge.Core.Config;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Core.Auth;

/// <summary>
/// Service-principal tokens through an MSAL confidential client. MSAL caches tokens in memory and
/// refreshes them near expiry, so calling this per request is cheap.
/// </summary>
public sealed class ClientSecretTokenProvider : ITokenProvider
{
    private readonly IConfidentialClientApplication _app;
    private readonly string[] _scopes;
    private readonly SecretScrubber _scrubber;

    public ClientSecretTokenProvider(ResolvedConfig config, SecretScrubber scrubber)
    {
        config.RequireCredentials();
        _scrubber = scrubber;
        _scrubber.Register(config.ClientSecret);
        _scopes = [config.Endpoints.TokenScope];
        var authority = new Uri($"{config.Endpoints.AuthorityHost.TrimEnd('/')}/{config.TenantId}");
        _app = ConfidentialClientApplicationBuilder.Create(config.ClientId)
            .WithClientSecret(config.ClientSecret)
            .WithAuthority(authority, validateAuthority: config.Cloud != Cloud.Custom)
            .Build();
    }

    public async Task<AccessTokenInfo> GetTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _app.AcquireTokenForClient(_scopes).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            _scrubber.Register(result.AccessToken);
            return new AccessTokenInfo(result.AccessToken, result.ExpiresOn);
        }
        catch (MsalException ex)
        {
            throw new BridgeException(ErrorKind.Auth, "Token acquisition failed: " + _scrubber.Scrub(ex.Message));
        }
    }
}
