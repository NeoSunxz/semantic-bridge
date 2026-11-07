using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AnalysisServices.AdomdClient;
using Microsoft.Identity.Client;
using Tom = Microsoft.AnalysisServices.Tabular;

// Phase 0 spike: throwaway connectivity probe. Never prints secrets, tokens or connection strings.

var secrets = new List<string>();

string Scrub(string? s)
{
    if (string.IsNullOrEmpty(s)) return "";
    foreach (var v in secrets)
        if (v.Length >= 4) s = s.Replace(v, "***");
    s = Regex.Replace(s, @"(?i)\b(password|pwd)\s*=\s*[^;]*", "$1=***");
    s = Regex.Replace(s, @"(?i)Bearer\s+[A-Za-z0-9\-._~+/=]+", "Bearer ***");
    s = Regex.Replace(s, @"eyJ[A-Za-z0-9_\-]*\.[A-Za-z0-9_\-]*\.[A-Za-z0-9_\-]*", "***jwt***");
    return s;
}

if (args.Contains("--self-test"))
{
    secrets.Add("s3cr3t-value-123");
    var samples = new[]
    {
        "bad secret s3cr3t-value-123 here",
        "Data Source=x;User ID=app:a@b;Password=hunter2;Initial Catalog=d",
        "conn pwd=abc;rest",
        "Authorization: Bearer abc.DEF-123_xyz",
        "token eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJl end",
    };
    foreach (var s in samples) Console.WriteLine($"{s}\n  -> {Scrub(s)}");
    return 0;
}

string[] required = { "SBRIDGE_TENANT_ID", "SBRIDGE_CLIENT_ID", "SBRIDGE_CLIENT_SECRET", "SBRIDGE_WORKSPACE", "SBRIDGE_DATASET" };
var missing = required.Where(n => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(n))).ToArray();
if (missing.Length > 0)
{
    Console.WriteLine("Missing required environment variables: " + string.Join(", ", missing));
    return 2;
}

var tenant = Environment.GetEnvironmentVariable("SBRIDGE_TENANT_ID")!;
var clientId = Environment.GetEnvironmentVariable("SBRIDGE_CLIENT_ID")!;
var secret = Environment.GetEnvironmentVariable("SBRIDGE_CLIENT_SECRET")!;
var wsInput = Environment.GetEnvironmentVariable("SBRIDGE_WORKSPACE")!;
var dataset = Environment.GetEnvironmentVariable("SBRIDGE_DATASET")!;
secrets.Add(secret);

var cloud = (Environment.GetEnvironmentVariable("SBRIDGE_CLOUD") ?? "china").ToLowerInvariant();
string authority, scope, restBase, xmlaHost;
if (cloud == "global")
{
    authority = "https://login.microsoftonline.com";
    scope = "https://analysis.windows.net/powerbi/api/.default";
    restBase = "https://api.powerbi.com/v1.0/myorg";
    xmlaHost = "api.powerbi.com";
}
else if (cloud == "china")
{
    authority = "https://login.partner.microsoftonline.cn";
    scope = "https://analysis.chinacloudapi.cn/powerbi/api/.default";
    restBase = "https://api.powerbi.cn/v1.0/myorg";
    xmlaHost = "api.powerbi.cn";
}
else
{
    Console.WriteLine("Unknown SBRIDGE_CLOUD (expected global|china)");
    return 2;
}
Console.WriteLine($"Cloud preset: {cloud}");

async Task Step(string name, Func<Task<string>> body)
{
    var sw = Stopwatch.StartNew();
    try
    {
        var detail = await body();
        Console.WriteLine($"OK  {name} ({sw.ElapsedMilliseconds} ms) {Scrub(detail)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {name}: {Scrub(ex.GetType().Name + ": " + ex.Message)}");
    }
}

string? token = null;
string? wsName = null;
string? wsId = null;
using var http = new HttpClient();

await Step("1 msal-token", async () =>
{
    var app = ConfidentialClientApplicationBuilder.Create(clientId)
        .WithClientSecret(secret)
        .WithAuthority(new Uri($"{authority}/{tenant}"))
        .Build();
    var r = await app.AcquireTokenForClient(new[] { scope }).ExecuteAsync();
    token = r.AccessToken;
    secrets.Add(token);
    return $"expires {r.ExpiresOn:u}";
});

async Task<JsonElement> RestGet(string path)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, restBase + path);
    req.Headers.Authorization = new("Bearer", token);
    using var resp = await http.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode) throw new Exception($"HTTP {(int)resp.StatusCode}: {body}");
    return JsonDocument.Parse(body).RootElement.Clone();
}

if (token != null)
{
    await Step("2 rest-groups", async () =>
    {
        var j = await RestGet("/groups");
        var arr = j.GetProperty("value");
        foreach (var g in arr.EnumerateArray())
        {
            var id = g.GetProperty("id").GetString();
            var name = g.GetProperty("name").GetString();
            if (string.Equals(id, wsInput, StringComparison.OrdinalIgnoreCase) || string.Equals(name, wsInput, StringComparison.OrdinalIgnoreCase))
            {
                wsId = id; wsName = name; break;
            }
        }
        return $"count={arr.GetArrayLength()} workspace found={(wsId != null)}";
    });

    if (wsId != null)
    {
        await Step("3 rest-datasets", async () =>
        {
            var j = await RestGet($"/groups/{wsId}/datasets");
            var arr = j.GetProperty("value");
            var found = arr.EnumerateArray().Any(d =>
                string.Equals(d.GetProperty("name").GetString(), dataset, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.GetProperty("id").GetString(), dataset, StringComparison.OrdinalIgnoreCase));
            return $"count={arr.GetArrayLength()} dataset found={found}";
        });
    }
}
wsName ??= wsInput;
var dataSource = $"powerbi://{xmlaHost}/v1.0/myorg/{Uri.EscapeDataString(wsName)}";

async Task<int> RunQuery(AdomdConnection c, string dax)
{
    await Task.Yield();
    using var cmd = c.CreateCommand();
    cmd.CommandText = dax;
    using var rd = cmd.ExecuteReader();
    int n = 0;
    while (rd.Read()) n++;
    return n;
}

await Step("4 adomd-token", async () =>
{
    if (token == null) throw new Exception("no token (step 1 failed)");
    using var c = new AdomdConnection($"Data Source={dataSource};Initial Catalog={dataset}");
    c.AccessToken = new Microsoft.AnalysisServices.AccessToken(token, DateTimeOffset.UtcNow.AddMinutes(30), null);
    c.Open();
    return $"rows={await RunQuery(c, "EVALUATE ROW(\"x\", 1)")}";
});

var appConn = $"Data Source={dataSource};Initial Catalog={dataset};User ID=app:{clientId}@{tenant};Password={secret}";

await Step("5 adomd-app-form", async () =>
{
    using var c = new AdomdConnection(appConn);
    c.Open();
    return $"rows={await RunQuery(c, "EVALUATE ROW(\"x\", 1)")}";
});

AdomdConnection Open()
{
    var c = new AdomdConnection($"Data Source={dataSource};Initial Catalog={dataset}");
    if (token != null)
    {
        c.AccessToken = new Microsoft.AnalysisServices.AccessToken(token, DateTimeOffset.UtcNow.AddMinutes(30), null);
    }
    else
    {
        c.ConnectionString = appConn;
    }
    c.Open();
    return c;
}

await Step("6 info-functions", async () =>
{
    using var c = Open();
    var t = await RunQuery(c, "EVALUATE INFO.TABLES()");
    var col = await RunQuery(c, "EVALUATE INFO.COLUMNS()");
    var m = await RunQuery(c, "EVALUATE INFO.MEASURES()");
    return $"tables={t} columns={col} measures={m}";
});

await Step("7 tmschema-measures", async () =>
{
    await Task.Yield();
    using var c = Open();
    var ds = c.GetSchemaDataSet("TMSCHEMA_MEASURES", null);
    return $"rows={ds.Tables[0].Rows.Count}";
});

await Step("8 tom-read", async () =>
{
    await Task.Yield();
    var server = new Tom.Server();
    string how;
    try
    {
        // TOM connection strings accept an access token via "Password" with an empty user in some builds; try app: form.
        server.Connect(appConn);
        how = "app-form";
    }
    catch
    {
        server.Dispose();
        throw;
    }
    try
    {
        var db = server.Databases.FindByName(dataset) ?? throw new Exception("database not found by name");
        return $"via={how} tables={db.Model.Tables.Count}";
    }
    finally
    {
        server.Disconnect();
        server.Dispose();
    }
});

return 0;
