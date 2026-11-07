using SemanticBridge.Core.Safety;
namespace SemanticBridge.Core.Config;

public enum QueryVia
{
    Auto,
    Xmla,
    Rest,
}

/// <summary>Fully resolved settings for one profile, plus where each value came from.</summary>
public sealed class ResolvedConfig
{
    public const int DefaultMaxRows = 1000;
    public const int DefaultMaxRowsCeiling = 100_000;
    public const int DefaultTimeoutSeconds = 120;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 600;

    public string? ConfigPath { get; init; }
    public bool ConfigFileFound { get; init; }
    public required string ProfileName { get; init; }
    public required string ProfileSource { get; init; }

    public required Cloud Cloud { get; init; }
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public required CloudEndpoints Endpoints { get; init; }
    public string? DefaultWorkspace { get; init; }
    public string? DefaultDataset { get; init; }
    public bool AllowWrite { get; init; }
    public int MaxRows { get; init; } = DefaultMaxRows;
    public int MaxRowsCeiling { get; init; } = DefaultMaxRowsCeiling;
    public int QueryTimeoutSeconds { get; init; } = DefaultTimeoutSeconds;
    public QueryVia DefaultVia { get; init; } = QueryVia.Auto;

    /// <summary>Config key to a human-readable origin such as "env SBRIDGE_TENANT_ID" or "preset china".</summary>
    public IReadOnlyDictionary<string, string> Sources { get; init; } = new Dictionary<string, string>();

    /// <summary>Returns the given dataset, or default_dataset when none is given; a usage error if neither is set.</summary>
    public string ResolveDatasetInput(string? dataset)
    {
        var input = !string.IsNullOrWhiteSpace(dataset) ? dataset : DefaultDataset;
        if (string.IsNullOrWhiteSpace(input))
            throw new BridgeException(ErrorKind.Usage, "No dataset given: pass a dataset (-d in the CLI, `dataset` in MCP) or set default_dataset (or SBRIDGE_DATASET).");
        return input;
    }

    /// <summary>Throws a config error listing every credential that is still missing.</summary>
    public void RequireCredentials()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(TenantId)) missing.Add("tenant_id (SBRIDGE_TENANT_ID)");
        if (string.IsNullOrWhiteSpace(ClientId)) missing.Add("client_id (SBRIDGE_CLIENT_ID)");
        if (string.IsNullOrEmpty(ClientSecret)) missing.Add("client_secret (SBRIDGE_CLIENT_SECRET or client_secret_env)");
        if (missing.Count > 0)
            throw new Safety.BridgeException(Safety.ErrorKind.Config,
                $"Profile '{ProfileName}' is missing: {string.Join(", ", missing)}. Run 'sbridge config init' or set the environment variables.");
    }
}
