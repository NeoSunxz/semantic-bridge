using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Cli.Mcp;

public sealed record HttpOptions(string Host, int Port, string? Token, bool NoAuth = false);

/// <summary>Hosts the MCP server over stdio or Streamable HTTP. Logs go to stderr only; stdout is reserved for the stdio protocol.</summary>
public static class McpHost
{
    public const string EnvHttpToken = "SBRIDGE_HTTP_TOKEN";
    public const string EnvHttpNoAuth = "SBRIDGE_HTTP_NO_AUTH";
    public const string EnvAllowHttpWrite = "SBRIDGE_ALLOW_HTTP_WRITE";
    public const int MinRecommendedTokenLength = 16;
    public const long MaxRequestBodyBytes = 1_000_000;

    public static async Task RunStdioAsync(IEnumerable<McpServerTool> tools, int queryTimeoutSeconds, TextWriter err, CancellationToken ct)
    {
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var t in tools) collection.Add(t);
        var pending = new PendingRequests();
        await using var transport = new StreamServerTransport(
            new DrainOnEofInput(Console.OpenStandardInput(), pending, TimeSpan.FromSeconds(queryTimeoutSeconds + 30), err, ct),
            new TrackResponsesOutput(Console.OpenStandardOutput(), pending),
            "sbridge");
        await using var server = McpServer.Create(transport, new McpServerOptions { ToolCollection = collection });
        await server.RunAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Builds the HTTP app (not yet started). Port 0 picks a free port; read it from <c>app.Urls</c> after start.</summary>
    /// <exception cref="BridgeException">A non-loopback bind without a token and without NoAuth.</exception>
    public static WebApplication BuildHttp(IEnumerable<McpServerTool> tools, HttpOptions options, TextWriter log)
    {
        var loopbackBind = IsLoopbackHost(options.Host);
        var token = options.Token?.Trim();
        var hasToken = !string.IsNullOrEmpty(token);
        if (!loopbackBind && !hasToken && !options.NoAuth)
            throw new BridgeException(ErrorKind.Usage,
                $"Refusing to listen on non-loopback host '{options.Host}' without authentication. Set {EnvHttpToken}, or pass --no-auth (env {EnvHttpNoAuth}=1) to run unauthenticated on purpose.");

        var builder = WebApplication.CreateBuilder();
        ConfigureLogging(builder.Logging);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxRequestBodyBytes);
        var host = options.Host.Contains(':') && !options.Host.StartsWith('[') ? $"[{options.Host}]" : options.Host;
        builder.WebHost.UseUrls($"http://{host}:{options.Port}");
        builder.Services.AddMcpServer().WithHttpTransport().WithTools(tools);
        var app = builder.Build();

        if (hasToken && token!.Length < MinRecommendedTokenLength)
            log.WriteLine($"mcp: WARNING: {EnvHttpToken} is shorter than {MinRecommendedTokenLength} characters; use a longer random token.");
        if (!hasToken)
            log.WriteLine(loopbackBind
                ? $"mcp: WARNING: {EnvHttpToken} is not set; the HTTP endpoint is unauthenticated. Anyone who can reach it can use this profile's credentials."
                : $"mcp: WARNING: --no-auth is set and the server is bound to the non-loopback address '{options.Host}' without a token. Anyone who can reach this address can use this profile's credentials.");

        app.Use(RejectForeignOrigins(loopbackBind));
        if (hasToken) app.Use(RequireBearer(token!));

        app.MapMcp("/mcp");
        return app;
    }

    /// <summary>
    /// DNS-rebinding defence. A browser page can reach a local server through a hostile DNS name, so:
    /// when bound to loopback, both the Host header and any Origin header must be loopback names;
    /// when bound elsewhere, the Host is legitimately arbitrary, so only an Origin that does not match the request Host is rejected.
    /// Clients that are not browsers send no Origin and are unaffected.
    /// </summary>
    private static Func<HttpContext, RequestDelegate, Task> RejectForeignOrigins(bool loopbackBind) => async (context, next) =>
    {
        var requestHost = context.Request.Host.Host;
        var origin = context.Request.Headers.Origin.ToString();
        var forbidden = loopbackBind && !IsLoopbackHost(requestHost);
        if (!forbidden && origin.Length > 0)
        {
            forbidden = !Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
                || (loopbackBind ? !IsLoopbackHost(originUri.Host) : !string.Equals(originUri.Host.Trim('[', ']'), requestHost.Trim('[', ']'), StringComparison.OrdinalIgnoreCase));
        }
        if (forbidden)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            // E.g. 413 for an oversized body: answer with the status instead of letting Kestrel log a stack trace to stderr.
            context.Response.StatusCode = ex.StatusCode;
        }
    };

    internal static bool IsLoopbackHost(string host)
    {
        var h = host.Trim('[', ']');
        return string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(h, out var ip) && IPAddress.IsLoopback(ip));
    }

    private static Func<HttpContext, RequestDelegate, Task> RequireBearer(string token)
    {
        // Compare fixed-length hashes in constant time so neither content nor length of the token leaks via timing.
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return async (context, next) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : string.Empty;
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
            if (!CryptographicOperations.FixedTimeEquals(expected, actual) || supplied.Length == 0)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            await next(context).ConfigureAwait(false);
        };
    }

    /// <summary>Kestrel could not bind the configured address (port in use, no permission, ...).</summary>
    internal static bool IsBindFailure(Exception ex) => ex is IOException && ex.Message.StartsWith("Failed to bind", StringComparison.Ordinal);

    internal static bool IsAddressInUse(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse }
                || e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        // The console logger is wrapped so that only the startup bind failure is dropped: the CLI reports it as one error line,
        // while the host's critical log of it would add a stack trace. Every other host or Kestrel log still reaches stderr.
        var console = LoggerFactory.Create(b =>
        {
            b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            b.SetMinimumLevel(LogLevel.Warning);
        });
        logging.AddProvider(new BindFailureFilterProvider(console));
        logging.SetMinimumLevel(LogLevel.Warning);
    }
}

/// <summary>Forwards logs to an inner factory except entries whose exception is a bind failure (see <see cref="McpHost.IsBindFailure"/>).</summary>
internal sealed class BindFailureFilterProvider(ILoggerFactory inner) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FilteringLogger(inner.CreateLogger(categoryName));

    public void Dispose() => inner.Dispose();

    private sealed class FilteringLogger(ILogger target) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => target.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => target.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null && McpHost.IsBindFailure(exception)) return;
            target.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
