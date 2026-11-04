using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SemanticBridge.Core.Auth;
using SemanticBridge.Core.Config;
using SemanticBridge.Core.Results;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Core.Rest;

/// <summary>Typed client for the Power BI REST API. Retries 429/5xx a bounded number of times and scrubs every error.</summary>
public sealed class PowerBiRestClient
{
    public const int PageSize = 1000;
    private const int MaxPages = 100;
    private const int MaxAttempts = 4;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Documented executeQueries limits: 100,000 rows or 1,000,000 values per query.</summary>
    public const int ServiceMaxRows = 100_000;
    public const int ServiceMaxValues = 1_000_000;

    private readonly HttpClient _http;
    private readonly ResolvedConfig _config;
    private readonly ITokenProvider _tokens;
    private readonly SecretScrubber _scrubber;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public PowerBiRestClient(HttpClient http, ResolvedConfig config, ITokenProvider tokens, SecretScrubber scrubber,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _config = config;
        _tokens = tokens;
        _scrubber = scrubber;
        _delay = delay ?? Task.Delay;
    }

    public async Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(CancellationToken ct)
    {
        var all = new List<Workspace>();
        for (var page = 0; page < MaxPages; page++)
        {
            var body = await SendAsync(HttpMethod.Get, $"/groups?$top={PageSize}&$skip={all.Count}", null, DefaultCallTimeout, ErrorKind.Rest, ct).ConfigureAwait(false);
            using var doc = ParseJson(body);
            var count = 0;
            foreach (var item in Values(doc.RootElement))
            {
                count++;
                all.Add(new Workspace(Str(item, "id"), Str(item, "name"),
                    item.TryGetProperty("isOnDedicatedCapacity", out var cap) && cap.ValueKind is JsonValueKind.True or JsonValueKind.False ? cap.GetBoolean() : null));
            }
            if (count < PageSize) return all;
        }
        throw new BridgeException(ErrorKind.Rest, $"Stopped listing workspaces after {MaxPages} pages of {PageSize}; refusing to return a partial list.");
    }

    public async Task<IReadOnlyList<Dataset>> GetDatasetsAsync(string workspaceId, CancellationToken ct)
    {
        var body = await SendAsync(HttpMethod.Get, $"/groups/{Uri.EscapeDataString(workspaceId)}/datasets", null, DefaultCallTimeout, ErrorKind.Rest, ct).ConfigureAwait(false);
        using var doc = ParseJson(body);
        return [.. Values(doc.RootElement).Select(d => new Dataset(Str(d, "id"), Str(d, "name"),
            d.TryGetProperty("configuredBy", out var by) && by.ValueKind == JsonValueKind.String ? by.GetString() : null))];
    }

    /// <summary>Cheap reachability/authorization probe: lists at most one workspace.</summary>
    public async Task PingAsync(CancellationToken ct) =>
        await SendAsync(HttpMethod.Get, "/groups?$top=1", null, DefaultCallTimeout, ErrorKind.Rest, ct).ConfigureAwait(false);

    public async Task<QueryResult> ExecuteQueryAsync(string workspaceId, string datasetId, string dax, int maxRows, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var payload = JsonSerializer.Serialize(new
        {
            queries = new[] { new { query = dax } },
            serializerSettings = new { includeNulls = true },
        });
        var path = $"/groups/{Uri.EscapeDataString(workspaceId)}/datasets/{Uri.EscapeDataString(datasetId)}/executeQueries";
        var body = await SendAsync(HttpMethod.Post, path, payload, TimeSpan.FromSeconds(_config.QueryTimeoutSeconds), ErrorKind.Dax, ct).ConfigureAwait(false);
        var result = ParseQueryResult(body, maxRows, sw.ElapsedMilliseconds);
        return result;
    }

    /// <summary>Parses an executeQueries response. Public for tests. Detects HTTP 200 responses that carry an error object.</summary>
    public QueryResult ParseQueryResult(string body, int maxRows, long elapsedMs)
    {
        using var doc = ParseJson(body);
        var root = doc.RootElement;
        JsonElement? first = root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0
            ? results[0]
            : null;

        JsonElement? error = null;
        if (root.TryGetProperty("error", out var rootErr) && rootErr.ValueKind == JsonValueKind.Object) error = rootErr;
        else if (first is { } f && f.TryGetProperty("error", out var resErr) && resErr.ValueKind == JsonValueKind.Object) error = resErr;

        JsonElement rows = default;
        var hasRows = false;
        if (first is { } r && r.TryGetProperty("tables", out var tables) && tables.ValueKind == JsonValueKind.Array && tables.GetArrayLength() > 0
            && tables[0].TryGetProperty("rows", out rows) && rows.ValueKind == JsonValueKind.Array)
            hasRows = true;

        var errorText = error is { } e ? DescribeErrorObject(e) : null;
        if (errorText is not null && (!hasRows || rows.GetArrayLength() == 0))
            throw new BridgeException(ErrorKind.Dax, "executeQueries returned an error: " + errorText);

        var names = new List<string>();
        var kinds = new Dictionary<string, HashSet<string>>();
        var received = 0;
        var cap = new RowCap(maxRows);
        var capped = false;
        if (hasRows)
        {
            // First pass: column order = first appearance across rows, so a sparse first row cannot hide a column.
            foreach (var row in rows.EnumerateArray())
            {
                received++;
                foreach (var prop in row.EnumerateObject())
                    if (!kinds.ContainsKey(prop.Name)) { names.Add(prop.Name); kinds[prop.Name] = []; }
            }
            foreach (var row in rows.EnumerateArray())
            {
                var values = new object?[names.Count];
                for (var i = 0; i < names.Count; i++)
                {
                    if (row.TryGetProperty(names[i], out var cell))
                    {
                        values[i] = ToValue(cell);
                        if (values[i] is not null) kinds[names[i]].Add(TypeName(values[i]!));
                    }
                }
                if (!cap.Add(values)) { capped = true; break; }
            }
        }

        var reasons = new List<string>();
        if (capped && cap.Reason is { } capReason) reasons.Add(capReason);
        if (errorText is not null)
            reasons.Add("REST executeQueries returned HTTP 200 with an error, so the data is partial: " + errorText);
        if (received >= ServiceMaxRows || (long)received * Math.Max(names.Count, 1) >= ServiceMaxValues)
            reasons.Add($"REST executeQueries returns at most {ServiceMaxRows:N0} rows / {ServiceMaxValues:N0} values per query; the result may be incomplete.");

        return new QueryResult
        {
            Columns = [.. names.Select(n => new ColumnInfo(n, ColumnType(kinds[n])))],
            Rows = cap.Rows,
            Truncated = reasons.Count > 0,
            TruncationReason = reasons.Count > 0 ? string.Join(" ", reasons) : null,
            Path = ResultPaths.Rest,
            ElapsedMs = elapsedMs,
        };
    }

    private static string ColumnType(HashSet<string> seen) =>
        seen.Count switch
        {
            0 => "Unknown",
            1 => seen.First(),
            _ when seen.SetEquals(["Int64", "Double"]) => "Double",
            _ => "Mixed",
        };

    private static string TypeName(object v) => v switch
    {
        string => "String",
        bool => "Boolean",
        long => "Int64",
        double => "Double",
        _ => "Unknown",
    };

    private static object? ToValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
        _ => e.GetRawText(),
    };

    private string DescribeErrorObject(JsonElement error)
    {
        string? code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        string? message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var details = new List<string>();
        if (error.TryGetProperty("pbi.error", out var pbi) && pbi.ValueKind == JsonValueKind.Object
            && pbi.TryGetProperty("details", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in arr.EnumerateArray())
                if (d.TryGetProperty("detail", out var detail) && detail.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                    details.Add(v.GetString()!);
        }
        var parts = new[] { code, message }.Concat(details).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var text = parts.Count > 0 ? string.Join(" - ", parts) : error.GetRawText();
        return Truncate(_scrubber.Scrub(text), 500);
    }

    private async Task<string> SendAsync(HttpMethod method, string path, string? jsonBody, TimeSpan timeout, ErrorKind badRequestKind, CancellationToken ct)
    {
        var url = _config.Endpoints.RestBaseUrl.TrimEnd('/') + path;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var token = await _tokens.GetTokenAsync(cts.Token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                if (jsonBody is not null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new BridgeException(ErrorKind.Rest, "REST request failed: " + _scrubber.Scrub(ex.Message));
                }

                using (response)
                {
                    var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode) return text;

                    if (IsRetryable(response.StatusCode) && attempt < MaxAttempts)
                    {
                        await _delay(RetryDelay(response, attempt), cts.Token).ConfigureAwait(false);
                        continue;
                    }

                    var kind = response.StatusCode == HttpStatusCode.BadRequest ? badRequestKind : ErrorKind.Rest;
                    throw new BridgeException(kind, DescribeHttpError(response, text));
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new BridgeException(ErrorKind.Rest, $"REST request timed out after {timeout.TotalSeconds:0} s.");
        }
    }

    private static bool IsRetryable(HttpStatusCode code) =>
        code == HttpStatusCode.TooManyRequests || ((int)code >= 500 && code != HttpStatusCode.NotImplemented);

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        TimeSpan? requested = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        var delay = requested ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        return delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }

    private string DescribeHttpError(HttpResponseMessage response, string body)
    {
        var detail = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                detail = DescribeErrorObject(err);
        }
        catch (JsonException)
        {
            // Not JSON: fall back to the raw (scrubbed, then truncated) body.
        }
        detail = Truncate(_scrubber.Scrub(detail), 500);
        var status = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
        return string.IsNullOrWhiteSpace(detail) ? $"Power BI REST API returned {status}." : $"Power BI REST API returned {status}: {detail}";
    }

    private static JsonDocument ParseJson(string body)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw new BridgeException(ErrorKind.Rest, "The Power BI REST API returned a response that is not valid JSON."); }
    }

    private static IEnumerable<JsonElement> Values(JsonElement root) =>
        root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
