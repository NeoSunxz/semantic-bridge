using SemanticBridge.Core.Auth;
using SemanticBridge.Core.Config;
using SemanticBridge.Core.Rest;
using SemanticBridge.Core.Safety;
using SemanticBridge.Core.Xmla;

namespace SemanticBridge.Core;

/// <summary>Composition root: wires the real token provider, REST client and XMLA client for one resolved profile.</summary>
public sealed class BridgeRuntime : IDisposable
{
    private readonly HttpClient _http;

    private BridgeRuntime(HttpClient http, SecretScrubber scrubber, ITokenProvider tokens, PowerBiRestClient rest, IXmlaClient xmla, BridgeService service)
    {
        _http = http;
        Scrubber = scrubber;
        Tokens = tokens;
        Rest = rest;
        Xmla = xmla;
        Service = service;
    }

    public SecretScrubber Scrubber { get; }
    public ITokenProvider Tokens { get; }
    public PowerBiRestClient Rest { get; }
    public IXmlaClient Xmla { get; }
    public BridgeService Service { get; }

    public static BridgeRuntime Create(ResolvedConfig config, SecretScrubber? sharedScrubber = null)
    {
        var scrubber = sharedScrubber ?? new SecretScrubber();
        scrubber.Register(config.ClientSecret);
        var tokens = new ClientSecretTokenProvider(config, scrubber);
        // Per-call timeouts are applied by the REST client, so the HttpClient itself never times out.
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var rest = new PowerBiRestClient(http, config, tokens, scrubber);
        var xmla = new AdomdXmlaClient(new TokenAdomdConnectionFactory(config, tokens, scrubber), scrubber, config.QueryTimeoutSeconds);
        return new BridgeRuntime(http, scrubber, tokens, rest, xmla, new BridgeService(config, rest, xmla));
    }

    public void Dispose() => _http.Dispose();
}
