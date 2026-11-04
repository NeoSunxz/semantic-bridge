using SemanticBridge.Core.Config;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Tests;

public sealed class ConfigLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbridge-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string yaml)
    {
        var path = Path.Combine(_dir, "config.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private static Func<string, string?> Env(params (string, string)[] vars)
    {
        var map = vars.ToDictionary(v => v.Item1, v => v.Item2);
        return name => map.GetValueOrDefault(name);
    }

    private const string TwoProfiles = """
        default_profile: cn
        profiles:
          cn:
            cloud: china
            tenant_id: file-tenant
            client_id: file-client
            client_secret: file-secret
            max_rows: 50
          gl:
            cloud: global
            tenant_id: gl-tenant
            client_id: gl-client
        """;

    [Fact]
    public void FileValuesAndPresetsAreUsed()
    {
        var cfg = ConfigLoader.Load(new ConfigLoadOptions(Write(TwoProfiles)), Env());
        Assert.Equal("cn", cfg.ProfileName);
        Assert.Equal(Cloud.China, cfg.Cloud);
        Assert.Equal("file-tenant", cfg.TenantId);
        Assert.Equal("https://login.partner.microsoftonline.cn", cfg.Endpoints.AuthorityHost);
        Assert.Equal("https://analysis.chinacloudapi.cn/powerbi/api/.default", cfg.Endpoints.TokenScope);
        Assert.Equal("https://api.powerbi.cn/v1.0/myorg", cfg.Endpoints.RestBaseUrl);
        Assert.Equal("powerbi://api.powerbi.cn/v1.0/myorg/{workspace}", cfg.Endpoints.XmlaBase);
        Assert.Equal(50, cfg.MaxRows);
        Assert.False(cfg.AllowWrite);
        Assert.Equal(100_000, cfg.MaxRowsCeiling);
        Assert.Equal(120, cfg.QueryTimeoutSeconds);
        Assert.Equal(QueryVia.Auto, cfg.DefaultVia);
        Assert.Equal("preset china", cfg.Sources["authority_host"]);
    }

    [Fact]
    public void EnvOverridesFileAndFlagSelectsProfile()
    {
        var path = Write(TwoProfiles);
        var cfg = ConfigLoader.Load(new ConfigLoadOptions(path), Env((ConfigLoader.EnvTenantId, "env-tenant"), (ConfigLoader.EnvClientSecret, "env-secret"), (ConfigLoader.EnvCloud, "global")));
        Assert.Equal("env-tenant", cfg.TenantId);
        Assert.Equal("env-secret", cfg.ClientSecret);
        Assert.Equal(Cloud.Global, cfg.Cloud);
        Assert.Equal("file-client", cfg.ClientId);
        Assert.StartsWith("env ", cfg.Sources["tenant_id"]);

        // --profile beats SBRIDGE_PROFILE beats default_profile.
        var viaEnv = ConfigLoader.Load(new ConfigLoadOptions(path), Env((ConfigLoader.EnvProfile, "gl")));
        Assert.Equal("gl", viaEnv.ProfileName);
        var viaFlag = ConfigLoader.Load(new ConfigLoadOptions(path, "cn"), Env((ConfigLoader.EnvProfile, "gl")));
        Assert.Equal("cn", viaFlag.ProfileName);
        Assert.Equal("--profile", viaFlag.ProfileSource);
    }

    [Fact]
    public void ConfigPathFlagBeatsEnv()
    {
        var path = Write(TwoProfiles);
        var cfg = ConfigLoader.Load(new ConfigLoadOptions(path), Env((ConfigLoader.EnvConfig, Path.Combine(_dir, "missing.yaml"))));
        Assert.Equal(path, cfg.ConfigPath);
        var ex = Assert.Throws<BridgeException>(() => ConfigLoader.Load(new ConfigLoadOptions(), Env((ConfigLoader.EnvConfig, Path.Combine(_dir, "missing.yaml")))));
        Assert.Equal(ErrorKind.Config, ex.Kind);
    }

    [Fact]
    public void EnvOnlySetupWorksWithoutFile()
    {
        var env = Env(("HOME", _dir), ("APPDATA", _dir), (ConfigLoader.EnvTenantId, "t"), (ConfigLoader.EnvClientId, "c"), (ConfigLoader.EnvClientSecret, "s3cret-value"), (ConfigLoader.EnvCloud, "china"));
        var cfg = ConfigLoader.Load(new ConfigLoadOptions(), env);
        Assert.Equal(ConfigLoader.EnvOnlyProfileName, cfg.ProfileName);
        Assert.False(cfg.ConfigFileFound);
        Assert.Equal(Cloud.China, cfg.Cloud);
        Assert.Equal("t", cfg.TenantId);
        cfg.RequireCredentials();
    }

    [Fact]
    public void NoFileAndNoEnvIsAConfigError()
    {
        var ex = Assert.Throws<BridgeException>(() => ConfigLoader.Load(new ConfigLoadOptions(), Env(("HOME", _dir), ("APPDATA", _dir))));
        Assert.Equal(ErrorKind.Config, ex.Kind);
        Assert.True(ex.IsUsageError);
    }

    [Fact]
    public void CustomCloudRequiresAllFourEndpoints()
    {
        var path = Write("""
            profiles:
              p:
                cloud: custom
                authority_host: https://login.example.test
                token_scope: https://example.test/.default
            """);
        var ex = Assert.Throws<BridgeException>(() => ConfigLoader.Load(new ConfigLoadOptions(path), Env()));
        Assert.Contains("rest_base_url", ex.Message);
        Assert.Contains("xmla_base", ex.Message);
        Assert.DoesNotContain("authority_host, token_scope", ex.Message.Split("missing:")[1]);
    }

    [Fact]
    public void CustomCloudWithAllEndpointsWorks()
    {
        var path = Write("""
            profiles:
              p:
                cloud: custom
                authority_host: https://login.example.test
                token_scope: https://example.test/.default
                rest_base_url: https://api.example.test/v1.0/myorg
                xmla_base: powerbi://api.example.test/v1.0/myorg/{workspace}
            """);
        var cfg = ConfigLoader.Load(new ConfigLoadOptions(path), Env());
        Assert.Equal(Cloud.Custom, cfg.Cloud);
        Assert.Equal("https://api.example.test/v1.0/myorg", cfg.Endpoints.RestBaseUrl);
    }

    private string CustomProfile(string authority, string rest, string cloud = "custom") => Write($$"""
        profiles:
          p:
            cloud: {{cloud}}
            authority_host: {{authority}}
            token_scope: https://example.test/.default
            rest_base_url: {{rest}}
            xmla_base: powerbi://api.example.test/v1.0/myorg/{workspace}
        """);

    [Theory]
    [InlineData("http://login.example.test", "https://api.example.test/v1.0", "authority_host")]
    [InlineData("https://login.example.test", "http://api.example.test/v1.0", "rest_base_url")]
    [InlineData("http://localhost.evil.test", "https://api.example.test/v1.0", "authority_host")]
    [InlineData("ftp://login.example.test", "https://api.example.test/v1.0", "authority_host")]
    public void NonHttpsUrlsAreRejectedWithoutEchoingTheValue(string authority, string rest, string key)
    {
        var ex = Assert.Throws<BridgeException>(() => ConfigLoader.Load(new ConfigLoadOptions(CustomProfile(authority, rest)), Env()));
        Assert.Equal(ErrorKind.Config, ex.Kind);
        Assert.Contains(key, ex.Message);
        Assert.DoesNotContain("example.test", ex.Message);
        Assert.DoesNotContain("evil", ex.Message);
    }

    [Fact]
    public void HttpIsAllowedOnlyForLoopbackInACustomCloud()
    {
        var cfg = ConfigLoader.Load(new ConfigLoadOptions(CustomProfile("http://localhost:8080", "http://127.0.0.1:9090/v1.0")), Env());
        Assert.Equal("http://127.0.0.1:9090/v1.0", cfg.Endpoints.RestBaseUrl);
        // Built-in clouds never accept http, even for loopback values set in the file.
        var path = Write("""
            profiles:
              p:
                cloud: global
                authority_host: http://localhost:8080
            """);
        Assert.Throws<BridgeException>(() => ConfigLoader.Load(new ConfigLoadOptions(path), Env()));
    }

    [Fact]
    public void XmlaBaseWithoutPlaceholderIsRejected()
    {
        var path = Write("""
            profiles:
              p:
                cloud: global
                xmla_base: powerbi://api.powerbi.com/v1.0/myorg/fixed
            """);
        Assert.Throws<BridgeException>(() => ConfigLoader.Load(new ConfigLoadOptions(path), Env()));
    }

    [Fact]
    public void UnknownKeysAreRejectedWithTheKeyName()
    {
        var root = Write("profiles:\n  p:\n    tenant_id: a\nbogus_key: 1\n");
        Assert.Contains("bogus_key", Assert.Throws<BridgeException>(() => ConfigLoader.Load(new ConfigLoadOptions(root), Env())).Message);
        var nested = Write("profiles:\n  p:\n    tenant_idd: a\n");
        var ex = Assert.Throws<BridgeException>(() => ConfigLoader.Load(new ConfigLoadOptions(nested), Env()));
        Assert.Contains("tenant_idd", ex.Message);
        Assert.Equal(ErrorKind.Config, ex.Kind);
    }

    [Fact]
    public void TimeoutIsClampedAndReported()
    {
        var path = Write("profiles:\n  p:\n    query_timeout_seconds: 5000\n");
        var cfg = ConfigLoader.Load(new ConfigLoadOptions(path), Env());
        Assert.Equal(600, cfg.QueryTimeoutSeconds);
        Assert.Contains("clamped", cfg.Sources["query_timeout_seconds"]);
        var low = Write("profiles:\n  p:\n    query_timeout_seconds: 0\n");
        Assert.Equal(1, ConfigLoader.Load(new ConfigLoadOptions(low), Env()).QueryTimeoutSeconds);
    }

    [Fact]
    public void MaxRowsAboveCeilingIsRejected()
    {
        var path = Write("profiles:\n  p:\n    max_rows: 500\n    max_rows_ceiling: 100\n");
        Assert.Throws<BridgeException>(() => ConfigLoader.Load(new ConfigLoadOptions(path), Env()));
        var tooHigh = Write("profiles:\n  p:\n    max_rows_ceiling: 100001\n");
        Assert.Throws<BridgeException>(() => ConfigLoader.Load(new ConfigLoadOptions(tooHigh), Env()));
    }

    [Fact]
    public void ClientSecretEnvIndirectionAndPrecedence()
    {
        var path = Write("profiles:\n  p:\n    client_secret_env: MY_SECRET\n    client_secret: file-value\n");
        Assert.Equal("indirect", ConfigLoader.Load(new ConfigLoadOptions(path), Env(("MY_SECRET", "indirect"))).ClientSecret);
        Assert.Equal("direct", ConfigLoader.Load(new ConfigLoadOptions(path), Env(("MY_SECRET", "indirect"), (ConfigLoader.EnvClientSecret, "direct"))).ClientSecret);
        Assert.Equal("file-value", ConfigLoader.Load(new ConfigLoadOptions(path), Env()).ClientSecret);
    }

    [Fact]
    public void SeveralProfilesWithoutDefaultNeedAChoice()
    {
        var path = Write("profiles:\n  a:\n    tenant_id: x\n  b:\n    tenant_id: y\n");
        Assert.Throws<BridgeException>(() => ConfigLoader.Load(new ConfigLoadOptions(path), Env()));
        Assert.Equal("b", ConfigLoader.Load(new ConfigLoadOptions(path, "B"), Env()).ProfileName);
        Assert.Throws<BridgeException>(() => ConfigLoader.Load(new ConfigLoadOptions(path, "nope"), Env()));
    }

    [Fact]
    public void GeneratedTemplateIsValidConfig()
    {
        var path = Write(ConfigTemplate.Text);
        var cfg = ConfigLoader.Load(new ConfigLoadOptions(path), Env());
        Assert.Equal("default", cfg.ProfileName);
        Assert.False(cfg.AllowWrite);
        Assert.Contains("WARNING", ConfigTemplate.Text);
    }
}
