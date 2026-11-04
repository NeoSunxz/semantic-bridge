using SemanticBridge.Core;
using SemanticBridge.Core.Config;
using SemanticBridge.Core.Results;

namespace SemanticBridge.IntegrationTests;

/// <summary>
/// Live-tenant checks. Skipped unless the tenant, client ID and client secret are set as SBRIDGE_IT_TENANT_ID, SBRIDGE_IT_CLIENT_ID
/// and SBRIDGE_IT_CLIENT_SECRET (optional: SBRIDGE_IT_CLOUD, SBRIDGE_IT_WORKSPACE, SBRIDGE_IT_DATASET).
/// Each SBRIDGE_IT_* variable falls back to the matching SBRIDGE_* variable (SBRIDGE_TENANT_ID, ..., SBRIDGE_DATASET) when unset.
/// </summary>
public sealed class LiveTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static string? It(string name) => Env("SBRIDGE_IT_" + name) ?? Env("SBRIDGE_" + name);

    private static BridgeRuntime Runtime()
    {
        Assert.SkipUnless(It("TENANT_ID") is not null && It("CLIENT_ID") is not null && It("CLIENT_SECRET") is not null,
            "Set SBRIDGE_IT_TENANT_ID, SBRIDGE_IT_CLIENT_ID and SBRIDGE_IT_CLIENT_SECRET (or SBRIDGE_TENANT_ID, SBRIDGE_CLIENT_ID, SBRIDGE_CLIENT_SECRET) to run live tests.");
        var env = new Dictionary<string, string?>
        {
            [ConfigLoader.EnvTenantId] = It("TENANT_ID"),
            [ConfigLoader.EnvClientId] = It("CLIENT_ID"),
            [ConfigLoader.EnvClientSecret] = It("CLIENT_SECRET"),
            [ConfigLoader.EnvCloud] = It("CLOUD") ?? "global",
            ["HOME"] = Path.GetTempPath(),
            ["APPDATA"] = Path.GetTempPath(),
        };
        return BridgeRuntime.Create(ConfigLoader.Load(new ConfigLoadOptions(), n => env.GetValueOrDefault(n)));
    }

    [Fact]
    public async Task TokenCanBeAcquired()
    {
        using var rt = Runtime();
        var token = await rt.Tokens.GetTokenAsync(TestContext.Current.CancellationToken);
        Assert.True(token.ExpiresOn > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task WorkspacesCanBeListed()
    {
        using var rt = Runtime();
        var workspaces = await rt.Service.ListWorkspacesAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(workspaces);
    }

    [Fact]
    public async Task SimpleQueryRunsOnTheConfiguredDataset()
    {
        Assert.SkipUnless(It("WORKSPACE") is not null && It("DATASET") is not null, "Set SBRIDGE_IT_WORKSPACE and SBRIDGE_IT_DATASET (or SBRIDGE_WORKSPACE and SBRIDGE_DATASET).");
        using var rt = Runtime();
        var result = await rt.Service.QueryAsync(It("WORKSPACE"), It("DATASET")!, "EVALUATE ROW(\"x\", 1)", 10, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.RowCount);
        Assert.False(string.IsNullOrEmpty(result.Path));
        Assert.Contains(result.Path, new[] { ResultPaths.Xmla, ResultPaths.Rest });
    }
}
