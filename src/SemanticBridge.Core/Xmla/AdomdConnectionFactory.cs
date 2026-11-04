using Microsoft.AnalysisServices.AdomdClient;
using SemanticBridge.Core.Auth;
using SemanticBridge.Core.Config;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Core.Xmla;

/// <summary>The one place that creates and opens ADOMD connections.</summary>
public interface IAdomdConnectionFactory
{
    /// <summary>Opens a connection. Failures are classified: <see cref="ErrorKind.NoXmlaEndpoint"/> or <see cref="ErrorKind.Xmla"/>.</summary>
    Task<AdomdConnection> OpenAsync(string workspace, string? dataset, CancellationToken ct);
}

/// <summary>
/// Connects with an MSAL token set on <see cref="AdomdConnection.AccessToken"/>, so no secret is ever placed in a connection string.
/// If the token path proves unusable, switching to <c>User ID=app:{clientId}@{tenantId};Password=...</c> is a change inside <see cref="CreateConnection"/> only.
/// </summary>
public sealed class TokenAdomdConnectionFactory(ResolvedConfig config, ITokenProvider tokens, SecretScrubber scrubber) : IAdomdConnectionFactory
{
    public async Task<AdomdConnection> OpenAsync(string workspace, string? dataset, CancellationToken ct)
    {
        var token = await tokens.GetTokenAsync(ct).ConfigureAwait(false);
        var connection = CreateConnection(workspace, dataset, token);
        var timeout = TimeSpan.FromSeconds(config.QueryTimeoutSeconds);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var open = Task.Run(connection.Open, CancellationToken.None);
        // ADOMD's Open is synchronous; on cancel or timeout the call is abandoned and the connection disposed.
        _ = open.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        try
        {
            await open.WaitAsync(cts.Token).ConfigureAwait(false);
            return connection;
        }
        catch (OperationCanceledException)
        {
            connection.Dispose();
            if (ct.IsCancellationRequested) throw;
            throw new BridgeException(ErrorKind.Xmla, $"XMLA connection timed out after {timeout.TotalSeconds:0} s.");
        }
        catch (Exception ex)
        {
            connection.Dispose();
            throw new BridgeException(ErrorKind.Xmla, "XMLA connection failed: " + scrubber.Scrub(ex.Message));
        }
    }

    internal AdomdConnection CreateConnection(string workspace, string? dataset, AccessTokenInfo token) =>
        new(BuildConnectionString(config, workspace, dataset))
        {
            AccessToken = new Microsoft.AnalysisServices.AccessToken(token.Token, token.ExpiresOn, null),
        };

    /// <summary>Builds the connection string with every value quoted, so a workspace or dataset name cannot inject extra properties.</summary>
    public static string BuildConnectionString(ResolvedConfig config, string workspace, string? dataset)
    {
        var dataSource = config.Endpoints.XmlaBase.Replace(CloudPresets.WorkspacePlaceholder, Uri.EscapeDataString(workspace), StringComparison.Ordinal);
        var connectionString = $"Data Source={Quote(dataSource)}";
        if (!string.IsNullOrWhiteSpace(dataset)) connectionString += $";Initial Catalog={Quote(dataset)}";
        return connectionString + $";Connect Timeout={config.QueryTimeoutSeconds}";
    }

    private static string Quote(string value)
    {
        if (value.Any(char.IsControl))
            throw new BridgeException(ErrorKind.Usage, "Workspace and dataset names must not contain control characters.");
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
