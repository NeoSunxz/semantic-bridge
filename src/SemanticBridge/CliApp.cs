using System.CommandLine;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using SemanticBridge.Cli.Mcp;
using SemanticBridge.Cli.Output;
using SemanticBridge.Core;
using SemanticBridge.Core.Config;
using SemanticBridge.Core.Diagnostics;
using SemanticBridge.Core.Results;
using SemanticBridge.Core.Safety;
using SemanticBridge.Core.Xmla;

namespace SemanticBridge.Cli;

/// <summary>
/// Command-line front-end. stdout carries results only; diagnostics, notes and errors go to stderr.
/// Exit codes: 0 ok, 1 runtime error, 2 usage or configuration error, 130 cancelled (Ctrl-C).
/// </summary>
public sealed class CliApp
{
    public const int ExitOk = 0;
    public const int ExitRuntime = 1;
    public const int ExitUsage = 2;
    public const int ExitCancelled = 130;

    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly TextReader _in;
    private readonly Func<string, string?> _getEnv;
    private readonly Func<bool> _inputRedirected;
    private readonly Func<ResolvedConfig, WriteGate?, BridgeService>? _serviceFactory;
    private readonly SecretScrubber _scrubber = new();

    private readonly Option<string?> _configOption = new("--config") { Description = "Path to config.yaml (env: SBRIDGE_CONFIG).", Recursive = true };
    private readonly Option<string?> _profileOption = new("--profile") { Description = "Profile to use (env: SBRIDGE_PROFILE).", Recursive = true };

    private CliApp(TextWriter output, TextWriter error, TextReader input, Func<string, string?> getEnv, Func<bool> inputRedirected,
        Func<ResolvedConfig, WriteGate?, BridgeService>? serviceFactory)
    {
        _serviceFactory = serviceFactory;
        _out = output;
        _err = error;
        _in = input;
        _getEnv = getEnv;
        _inputRedirected = inputRedirected;
    }

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, TextReader input,
        Func<string, string?> getEnv, Func<bool> inputRedirected, CancellationToken ct,
        Func<ResolvedConfig, WriteGate?, BridgeService>? serviceFactory = null)
    {
        var app = new CliApp(output, error, input, getEnv, inputRedirected, serviceFactory);
        var root = app.BuildRoot();
        var parse = root.Parse(args);
        if (parse.Errors.Count > 0)
        {
            foreach (var e in parse.Errors) error.WriteLine("error: " + e.Message);
            error.WriteLine("Run 'sbridge --help' for usage.");
            return ExitUsage;
        }
        return await parse.InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, ct).ConfigureAwait(false);
    }

    private RootCommand BuildRoot()
    {
        var root = new RootCommand("Semantic Bridge for Power BI: query Power BI semantic models over XMLA and REST. Not affiliated with or endorsed by Microsoft.");
        root.Options.Add(_configOption);
        root.Options.Add(_profileOption);
        root.Subcommands.Add(BuildConfig());
        root.Subcommands.Add(BuildWorkspaces());
        root.Subcommands.Add(BuildDatasets());
        root.Subcommands.Add(BuildSchema());
        root.Subcommands.Add(BuildQuery());
        root.Subcommands.Add(BuildDescribe());
        root.Subcommands.Add(BuildRefresh());
        root.Subcommands.Add(BuildRefreshStatus());
        root.Subcommands.Add(BuildMcp());
        return root;
    }

    // ---- option factories -------------------------------------------------

    private static Option<string?> WorkspaceOption() => new("--workspace", "-w") { Description = "Workspace name or ID (default: default_workspace)." };

    private static Option<string?> DatasetOption() => new("--dataset", "-d") { Description = "Dataset name or ID (default: default_dataset)." };

    private static Option<string> FormatOption(params string[] allowed)
    {
        var option = new Option<string>("--format", "-f")
        {
            Description = $"Output format: {string.Join(", ", allowed)}.",
            DefaultValueFactory = _ => allowed[0],
        };
        option.AcceptOnlyFromAmong(allowed);
        return option;
    }

    private static Option<string?> ViaOption()
    {
        var option = new Option<string?>("--via") { Description = "Path: auto (XMLA, REST only when the workspace has no XMLA endpoint), xmla or rest." };
        // Case-insensitive like the MCP `via` argument (AcceptOnlyFromAmong is case-sensitive).
        option.Validators.Add(r =>
        {
            if (r.Tokens.Count > 0 && !Array.Exists(["auto", "xmla", "rest"], v => string.Equals(v, r.Tokens[0].Value.Trim(), StringComparison.OrdinalIgnoreCase)))
                r.AddError($"Invalid value '{r.Tokens[0].Value}' for --via: expected auto, xmla or rest.");
        });
        return option;
    }

    // ---- commands ---------------------------------------------------------

    private Command BuildConfig()
    {
        var config = new Command("config", "Create, inspect and test the configuration.");

        var init = new Command("init", "Write a commented config template.");
        var force = new Option<bool>("--force") { Description = "Overwrite an existing file." };
        init.Options.Add(force);
        init.SetAction((pr, ct) => Run(() => ConfigInit(pr.GetValue(_configOption), pr.GetValue(force))));
        config.Subcommands.Add(init);

        var show = new Command("show", "Print the resolved configuration with secrets masked.");
        show.SetAction((pr, ct) => Run(() =>
        {
            var cfg = LoadConfig(pr);
            _out.Write(ConfigRenderer.Render(cfg));
            return Task.FromResult(ExitOk);
        }));
        config.Subcommands.Add(show);

        var test = new Command("test", "Check token acquisition, REST reachability and (with a workspace) the XMLA connection.");
        var ws = WorkspaceOption();
        test.Options.Add(ws);
        test.SetAction((pr, ct) => Run(() => ConfigTest(LoadConfig(pr), pr.GetValue(ws), ct)));
        config.Subcommands.Add(test);
        return config;
    }

    private Command BuildWorkspaces()
    {
        var cmd = new Command("workspaces", "List workspaces visible to the service principal.");
        var format = FormatOption("table", "json", "csv");
        cmd.Options.Add(format);
        cmd.SetAction((pr, ct) => Run(async () =>
        {
            using var rt = CreateRuntime(pr);
            var items = await rt.Service.ListWorkspacesAsync(ct).ConfigureAwait(false);
            WriteList(pr.GetValue(format)!, items, ["id", "name", "is_on_dedicated_capacity"],
                items.Select(w => new object?[] { w.Id, w.Name, w.IsOnDedicatedCapacity }).ToList());
            return ExitOk;
        }));
        return cmd;
    }

    private Command BuildDatasets()
    {
        var cmd = new Command("datasets", "List datasets in a workspace.");
        var ws = WorkspaceOption();
        var format = FormatOption("table", "json", "csv");
        cmd.Options.Add(ws);
        cmd.Options.Add(format);
        cmd.SetAction((pr, ct) => Run(async () =>
        {
            using var rt = CreateRuntime(pr);
            var items = await rt.Service.ListDatasetsAsync(pr.GetValue(ws), ct).ConfigureAwait(false);
            WriteList(pr.GetValue(format)!, items, ["id", "name", "configured_by"],
                items.Select(d => new object?[] { d.Id, d.Name, d.ConfiguredBy }).ToList());
            return ExitOk;
        }));
        return cmd;
    }

    private Command BuildSchema()
    {
        var cmd = new Command("schema", "Show model metadata: tables, columns, measures (with DAX) and relationships. Needs XMLA.");
        var ws = WorkspaceOption();
        var ds = DatasetOption();
        var table = new Option<string?>("--table") { Description = "Only this table (and its relationships)." };
        var format = FormatOption("table", "json");
        var via = ViaOption();
        foreach (var o in new Option[] { ws, ds, table, format, via }) cmd.Options.Add(o);
        cmd.SetAction((pr, ct) => Run(async () =>
        {
            var cfg = LoadConfig(pr);
            var viaValue = pr.GetValue(via) is { } v ? ConfigLoader.ParseVia(v, "--via") : (QueryVia?)null;
            using var rt = OpenService(cfg, null);
            var schema = await rt.Service.GetSchemaAsync(pr.GetValue(ws), pr.GetValue(ds), pr.GetValue(table), viaValue, ct).ConfigureAwait(false);
            if (pr.GetValue(format) == "json") _out.WriteLine(JsonOutput.Serialize(schema));
            else
            {
                _out.Write(SchemaFormatter.Render(schema));
                _err.WriteLine($"-- path: {schema.Path}, {schema.ElapsedMs} ms");
            }
            foreach (var w in schema.Warnings) _err.WriteLine("warning: " + w);
            return ExitOk;
        }));
        return cmd;
    }

    private Command BuildQuery()
    {
        var cmd = new Command("query", "Run a DAX query (DEFINE/EVALUATE). Give the query as an argument, with --file, or on stdin.");
        var dax = new Argument<string?>("dax") { Description = "The DAX query.", Arity = ArgumentArity.ZeroOrOne };
        var ws = WorkspaceOption();
        var ds = DatasetOption();
        var file = new Option<string?>("--file") { Description = "Read the DAX query from a file." };
        var format = FormatOption("table", "json", "csv");
        var maxRows = new Option<int?>("--max-rows") { Description = "Row limit for this query (default max_rows; capped by max_rows_ceiling, which is reported).", CustomParser = r => ParseWholeNumber(r, "--max-rows") };
        var via = ViaOption();
        cmd.Arguments.Add(dax);
        foreach (var o in new Option[] { ws, ds, file, format, maxRows, via }) cmd.Options.Add(o);
        cmd.SetAction((pr, ct) => Run(async () =>
        {
            var cfg = LoadConfig(pr);
            var text = ReadQueryText(pr.GetValue(dax), pr.GetValue(file));
            var viaValue = pr.GetValue(via) is { } v ? ConfigLoader.ParseVia(v, "--via") : (QueryVia?)null;
            using var rt = OpenService(cfg, null);
            var result = await rt.Service.QueryAsync(pr.GetValue(ws), pr.GetValue(ds), text, pr.GetValue(maxRows), viaValue, ct).ConfigureAwait(false);
            WriteQueryResult(pr.GetValue(format)!, result);
            return ExitOk;
        }));
        return cmd;
    }

    private Command BuildDescribe()
    {
        var cmd = new Command("describe", "WRITE: update the description of a table, column or measure on the live model (needs allow_write: true and an XMLA Read Write endpoint). Shows old -> new and asks for confirmation.");
        var text = new Argument<string?>("description") { Description = "The new description text (empty text clears it). Not used with --file.", Arity = ArgumentArity.ZeroOrOne };
        var ws = WorkspaceOption();
        var ds = DatasetOption();
        var table = new Option<string?>("--table") { Description = "Table to describe." };
        var column = new Option<string?>("--column") { Description = "Column to describe, as Table[Column] (quote table names that contain brackets: 'My [Table]'[Col])." };
        var measure = new Option<string?>("--measure") { Description = "Measure to describe, by name." };
        var file = new Option<string?>("--file") { Description = "Batch of updates: a YAML or JSON list of {table|column|measure, description}." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Apply without asking for confirmation (for scripts)." };
        var format = FormatOption("table", "json");
        cmd.Arguments.Add(text);
        foreach (var o in new Option[] { ws, ds, table, column, measure, file, yes, format }) cmd.Options.Add(o);
        cmd.SetAction((pr, ct) => Run(async () =>
        {
            var cfg = LoadConfig(pr);
            var gate = new WriteGate(cfg.AllowWrite);
            gate.EnsureOpen();
            var updates = ReadDescriptionUpdates(pr.GetValue(text), pr.GetValue(table), pr.GetValue(column), pr.GetValue(measure), pr.GetValue(file));
            var assumeYes = pr.GetValue(yes);
            RequireConfirmable(assumeYes);

            using var rt = OpenService(cfg, gate);
            var plan = await rt.Service.PlanDescriptionsAsync(pr.GetValue(ws), pr.GetValue(ds), updates, ct).ConfigureAwait(false);
            _err.WriteLine($"Workspace '{plan.Workspace}', dataset '{plan.Dataset}':");
            foreach (var u in plan.Unchanged) _err.WriteLine($"unchanged: {u.Display} already has this description.");
            var applied = false;
            if (plan.Changes.Count == 0) _err.WriteLine("No changes to apply.");
            else
            {
                foreach (var c in plan.Changes) _err.WriteLine($"  {c.Display}: {Quote(c.Old)} -> {Quote(c.New)}");
                if (!assumeYes && !Confirm($"Apply {plan.Changes.Count} change(s)?"))
                {
                    _err.WriteLine("aborted: no changes were made.");
                    return ExitRuntime;
                }
                await rt.Service.ApplyDescriptionsAsync(plan, ct).ConfigureAwait(false);
                applied = true;
            }

            if (pr.GetValue(format) == "json")
                _out.WriteLine(JsonOutput.Serialize(new { applied, workspace = plan.Workspace, dataset = plan.Dataset, changes = plan.Changes, unchanged = plan.Unchanged }));
            else if (plan.Changes.Count > 0)
                _out.Write(TableFormatter.Render(["type", "target", "old", "new"], plan.Changes.Select(c => new object?[] { c.Type, c.Display, c.Old, c.New }).ToList()));
            if (applied) _err.WriteLine($"-- applied {plan.Changes.Count} change(s)");
            return ExitOk;
        }));
        return cmd;
    }

    private Command BuildRefresh()
    {
        var cmd = new Command("refresh", "WRITE: queue a dataset refresh (needs allow_write: true). Asks for confirmation. Exit code is 1 when a --wait ends failed or times out.");
        var ws = WorkspaceOption();
        var ds = DatasetOption();
        var wait = new Option<bool>("--wait") { Description = "Poll until the refresh completes, fails or --timeout passes." };
        var timeout = new Option<int>("--timeout") { Description = "With --wait: seconds to wait (1..86400).", DefaultValueFactory = _ => 600, CustomParser = r => ParseWholeNumber(r, "--timeout") ?? 600 };
        timeout.Validators.Add(r =>
        {
            if (r.Tokens.Count > 0 && int.TryParse(r.Tokens[0].Value, out var v) && v is < 1 or > 86400) r.AddError("--timeout must be between 1 and 86400.");
        });
        var yes = new Option<bool>("--yes", "-y") { Description = "Refresh without asking for confirmation (for scripts)." };
        foreach (var o in new Option[] { ws, ds, wait, timeout, yes }) cmd.Options.Add(o);
        cmd.SetAction((pr, ct) => Run(async () =>
        {
            if (!pr.GetValue(wait) && pr.GetResult(timeout) is { Implicit: false })
                throw new BridgeException(ErrorKind.Usage, "--timeout needs --wait.");
            var cfg = LoadConfig(pr);
            var gate = new WriteGate(cfg.AllowWrite);
            gate.EnsureOpen();
            var assumeYes = pr.GetValue(yes);
            RequireConfirmable(assumeYes);
            var datasetInput = cfg.ResolveDatasetInput(pr.GetValue(ds));
            if (!assumeYes && !Confirm($"Refresh dataset '{datasetInput}' now? This uses capacity and changes the data every report shows."))
            {
                _err.WriteLine("aborted: no refresh was started.");
                return ExitRuntime;
            }

            using var rt = OpenService(cfg, gate);
            var trigger = await rt.Service.RefreshAsync(pr.GetValue(ws), pr.GetValue(ds), ct, trackForWait: pr.GetValue(wait)).ConfigureAwait(false);
            _out.WriteLine("accepted: true");
            _out.WriteLine($"workspace: {trigger.WorkspaceName}");
            _out.WriteLine($"dataset: {trigger.DatasetName}");
            if (trigger.RequestId is not null) _out.WriteLine($"request_id: {trigger.RequestId}");
            if (!pr.GetValue(wait)) return ExitOk;

            _err.WriteLine("Waiting for the refresh to finish...");
            var result = await rt.Service.WaitForRefreshAsync(trigger, TimeSpan.FromSeconds(pr.GetValue(timeout)), ct).ConfigureAwait(false);
            if (result.Entry is { } e)
            {
                _out.WriteLine($"status: {e.Status}");
                _out.WriteLine($"start_time: {e.StartTime}");
                if (e.EndTime.Length > 0) _out.WriteLine($"end_time: {e.EndTime}");
                if (e.Error is not null) _out.WriteLine($"error: {_scrubber.Scrub(e.Error)}");
            }
            if (result.Entry is null)
            {
                _out.WriteLine("status: unknown");
                _err.WriteLine("error: could not identify this refresh in the history within the wait, so its outcome is unknown (it may still be running). Check 'sbridge refresh-status'.");
                return ExitRuntime;
            }
            if (result.TimedOut)
            {
                _err.WriteLine($"error: the refresh did not finish within {pr.GetValue(timeout)} s; it may still be running. Check 'sbridge refresh-status'.");
                return ExitRuntime;
            }
            return result.Entry is { Status: "Completed" } ? ExitOk : ExitRuntime;
        }));
        return cmd;
    }

    private Command BuildRefreshStatus()
    {
        var cmd = new Command("refresh-status", "Show a dataset's recent refresh history (newest first).");
        var ws = WorkspaceOption();
        var ds = DatasetOption();
        var top = new Option<int>("--top") { Description = $"How many entries (1..{BridgeService.MaxRefreshHistory}).", DefaultValueFactory = _ => 5, CustomParser = r => ParseWholeNumber(r, "--top") ?? 5 };
        var format = FormatOption("table", "json", "csv");
        foreach (var o in new Option[] { ws, ds, top, format }) cmd.Options.Add(o);
        cmd.SetAction((pr, ct) => Run(async () =>
        {
            using var rt = OpenService(LoadConfig(pr), null);
            var items = await rt.Service.GetRefreshStatusAsync(pr.GetValue(ws), pr.GetValue(ds), pr.GetValue(top), ct).ConfigureAwait(false);
            WriteList(pr.GetValue(format)!, items, ["request_id", "refresh_type", "status", "start_time", "end_time", "error"],
                items.Select(r => new object?[] { r.RequestId, r.RefreshType, r.Status, r.StartTime, r.EndTime, r.Error }).ToList());
            return ExitOk;
        }));
        return cmd;
    }

    private Command BuildMcp()
    {
        var cmd = new Command("mcp", "Run the MCP server (stdio by default; --http for Streamable HTTP). Uses one profile for its lifetime.");
        var http = new Option<bool>("--http") { Description = "Serve Streamable HTTP instead of stdio. Bearer token from SBRIDGE_HTTP_TOKEN (required on a non-loopback host)." };
        var host = new Option<string>("--host") { Description = "HTTP bind address (use 0.0.0.0 to listen on all interfaces).", DefaultValueFactory = _ => "127.0.0.1" };
        var port = new Option<int>("--port") { Description = "HTTP port.", DefaultValueFactory = _ => 8080, CustomParser = r => ParseWholeNumber(r, "--port") ?? 8080 };
        port.Validators.Add(r =>
        {
            if (r.Tokens.Count > 0 && int.TryParse(r.Tokens[0].Value, out var v) && v is < 1 or > 65535) r.AddError("--port must be between 1 and 65535.");
        });
        var noAuth = new Option<bool>("--no-auth") { Description = "Allow --http on a non-loopback --host without SBRIDGE_HTTP_TOKEN (unauthenticated; dangerous). Env: SBRIDGE_HTTP_NO_AUTH=1." };
        var allowHttpWrite = new Option<bool>("--allow-http-write") { Description = "Allow write tools over HTTP (also needs allow_write: true). Env: SBRIDGE_ALLOW_HTTP_WRITE=1." };
        foreach (var o in new Option[] { http, host, port, noAuth, allowHttpWrite }) cmd.Options.Add(o);
        cmd.SetAction((pr, ct) => Run(async () =>
        {
            var cfg = LoadConfig(pr);
            cfg.RequireCredentials();
            var overHttp = pr.GetValue(http);
            var httpWrite = pr.GetValue(allowHttpWrite) || _getEnv(McpHost.EnvAllowHttpWrite) == "1";
            var gate = new WriteGate(cfg.AllowWrite, overHttp, httpWrite);
            using var rt = CreateRuntime(cfg, gate);
            var tools = McpToolRegistry.Create(new McpTools(rt.Service, _scrubber), gate, _err);
            if (!overHttp)
            {
                await McpHost.RunStdioAsync(tools, cfg.QueryTimeoutSeconds, _err, ct).ConfigureAwait(false);
                return ExitOk;
            }
            var token = _getEnv(McpHost.EnvHttpToken);
            _scrubber.Register(token);
            _scrubber.Register(token?.Trim());
            var unauthenticated = pr.GetValue(noAuth) || _getEnv(McpHost.EnvHttpNoAuth) == "1";
            await using var app = McpHost.BuildHttp(tools, new HttpOptions(pr.GetValue(host)!, pr.GetValue(port), string.IsNullOrEmpty(token) ? null : token, unauthenticated), _err);
            try
            {
                await app.StartAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (McpHost.IsBindFailure(ex))
            {
                _err.WriteLine($"error: cannot listen on {pr.GetValue(host)}:{pr.GetValue(port)}: " + (McpHost.IsAddressInUse(ex) ? "address already in use" : _scrubber.Scrub(ex.Message)));
                return ExitRuntime;
            }
            _err.WriteLine($"mcp: listening on {string.Join(", ", app.Urls.Select(u => u + "/mcp"))} (profile '{cfg.ProfileName}')");
            await app.WaitForShutdownAsync(ct).ConfigureAwait(false);
            return ExitOk;
        }));
        return cmd;
    }

    // ---- helpers ----------------------------------------------------------

    private const long MaxBatchFileBytes = 1024 * 1024;

    private static IReadOnlyList<DescriptionUpdate> ReadDescriptionUpdates(string? text, string? table, string? column, string? measure, string? file)
    {
        var targets = new[] { table, column, measure }.Count(t => t is not null);
        if (file is not null)
        {
            if (targets > 0 || text is not null)
                throw new BridgeException(ErrorKind.Usage, "With --file, do not also give --table/--column/--measure or a description text.");
            string content;
            try
            {
                if (new FileInfo(file).Length > MaxBatchFileBytes)
                    throw new BridgeException(ErrorKind.Usage, $"Batch file '{file}' is larger than the {MaxBatchFileBytes / 1024 / 1024} MB limit.");
                content = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Say what failed, never the file's content or the OS message (it can quote paths and data).
                throw new BridgeException(ErrorKind.Usage, $"Cannot read batch file '{file}' ({ex.GetType().Name}).");
            }
            return DescriptionBatch.Parse(content, json: file.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        }
        if (targets != 1)
            throw new BridgeException(ErrorKind.Usage, "Give exactly one of --table, --column or --measure (or --file for a batch).");
        if (text is null)
            throw new BridgeException(ErrorKind.Usage, "No description text given. Pass it as an argument (\"\" clears the description).");
        var target = table is not null ? DescriptionTargets.Table(table) : column is not null ? DescriptionTargets.ParseColumn(column) : DescriptionTargets.Measure(measure!);
        return [new DescriptionUpdate(target, text)];
    }

    /// <summary>Refuses before any network call when a confirmation would be needed but stdin is not a terminal.</summary>
    private void RequireConfirmable(bool assumeYes)
    {
        if (!assumeYes && _inputRedirected())
            throw new BridgeException(ErrorKind.Usage, "This command changes the live model and needs confirmation, but stdin is not a terminal. Pass --yes to proceed without asking.");
    }

    private bool Confirm(string question)
    {
        _err.Write(question + " [y/N] ");
        _err.Flush();
        var answer = _in.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }

    // Backslash and quote first, then the same visible escapes as table cells (tab, line breaks, control and bidi characters).
    private static string Quote(string? value) => value is null ? "(none)"
        : "\"" + TableFormatter.Escape(value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)) + "\"";

    private string ReadQueryText(string? argument, string? file)
    {
        if (argument is not null && file is not null)
            throw new BridgeException(ErrorKind.Usage, "Give the query either as an argument or with --file, not both.");
        string text;
        if (file is not null)
        {
            try { text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new BridgeException(ErrorKind.Usage, $"Cannot read query file '{file}': {ex.Message}");
            }
        }
        else if (argument is not null) text = argument;
        else if (_inputRedirected()) text = _in.ReadToEnd();
        else throw new BridgeException(ErrorKind.Usage, "No query given. Pass the DAX as an argument, use --file, or pipe it on stdin.");
        if (string.IsNullOrWhiteSpace(text)) throw new BridgeException(ErrorKind.Usage, "The query is empty.");
        return text;
    }

    private void WriteQueryResult(string format, QueryResult result)
    {
        var headers = result.Columns.Select(c => c.Name).ToList();
        switch (format)
        {
            case "json":
                _out.WriteLine(JsonOutput.Serialize(result));
                return;
            case "csv":
                _out.Write(CsvFormatter.Render(headers, result.Rows));
                break;
            default:
                _out.Write(TableFormatter.Render(headers, result.Rows));
                break;
        }
        _err.WriteLine($"-- {result.RowCount} row(s), path: {result.Path}, {result.ElapsedMs} ms");
        if (result.Truncated) _err.WriteLine("note: result is TRUNCATED. " + result.TruncationReason);
        foreach (var w in result.Warnings) _err.WriteLine("warning: " + w);
    }

    private void WriteList<T>(string format, IReadOnlyList<T> items, string[] headers, IReadOnlyList<object?[]> rows)
    {
        switch (format)
        {
            case "json": _out.WriteLine(JsonOutput.Serialize(items)); break;
            case "csv": _out.Write(CsvFormatter.Render(headers, rows)); break;
            default: _out.Write(TableFormatter.Render(headers, rows)); break;
        }
        _err.WriteLine($"-- {items.Count} item(s)");
    }

    private ResolvedConfig LoadConfig(ParseResult pr)
    {
        var cfg = ConfigLoader.Load(new ConfigLoadOptions(pr.GetValue(_configOption), pr.GetValue(_profileOption)), _getEnv);
        _scrubber.Register(cfg.ClientSecret);
        return cfg;
    }

    private BridgeRuntime CreateRuntime(ParseResult pr) => CreateRuntime(LoadConfig(pr));

    private BridgeRuntime CreateRuntime(ResolvedConfig cfg, WriteGate? gate = null) => BridgeRuntime.Create(cfg, _scrubber, gate);

    private sealed class ServiceScope(BridgeService service, IDisposable? owner) : IDisposable
    {
        public BridgeService Service => service;

        public void Dispose() => owner?.Dispose();
    }

    /// <summary>The real runtime, or the injected service in tests (so write flows run without a network).</summary>
    private ServiceScope OpenService(ResolvedConfig cfg, WriteGate? gate)
    {
        if (_serviceFactory is { } factory) return new ServiceScope(factory(cfg, gate), null);
        var runtime = CreateRuntime(cfg, gate);
        return new ServiceScope(runtime.Service, runtime);
    }

    /// <summary>Parses the single token of an integer option; a bad value becomes a usage error that names the option.</summary>
    private static int? ParseWholeNumber(System.CommandLine.Parsing.ArgumentResult result, string option)
    {
        var token = result.Tokens.Count > 0 ? result.Tokens[0].Value : null;
        if (token is null) return null;
        if (int.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)) return value;
        // A well-formed integer that does not fit in 32 bits is out of range, not malformed.
        var outOfRange = System.Numerics.BigInteger.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _);
        result.AddError(outOfRange
            ? $"Invalid value '{token}' for {option}: the number is out of range."
            : $"Invalid value '{token}' for {option}: expected a whole number.");
        return null;
    }

    private async Task<int> Run(Func<Task<int>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _err.WriteLine("error: cancelled.");
            return ExitCancelled;
        }
        catch (BridgeException ex)
        {
            ReportError(ex.Message, ex.Kind != ErrorKind.Usage ? ex : null);
            return ex.IsUsageError ? ExitUsage : ExitRuntime;
        }
        catch (Exception ex)
        {
            ReportError($"unexpected {ex.GetType().Name}: {ex.Message}", ex);
            return ExitRuntime;
        }
    }

    // Usage errors are the user's own invalid input (for example the explicit --via rest refusal); infrastructure hints would mislead.
    private void ReportError(string message, Exception? hintFor)
    {
        var clean = _scrubber.Scrub(message);
        _err.WriteLine("error: " + clean);
        if (hintFor is not null && ErrorHints.For(hintFor, clean) is { } hint) _err.WriteLine("hint: " + hint);
    }

    private Task<int> ConfigInit(string? configOption, bool force)
    {
        var path = !string.IsNullOrWhiteSpace(configOption) ? configOption
            : _getEnv(ConfigLoader.EnvConfig) is { Length: > 0 } fromEnv ? fromEnv
            : ConfigLoader.DefaultConfigPath(_getEnv);
        if (File.Exists(path) && !force)
            throw new BridgeException(ErrorKind.Config, $"{path} already exists. Use --force to overwrite it.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        // The file may later hold a secret: create it private to the user (0600) where the OS supports it.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            options.UnixCreateMode = Private;
            // UnixCreateMode applies only to new files; tighten an existing one before its content is replaced (--force).
            if (File.Exists(path)) File.SetUnixFileMode(path, Private);
        }
        using (var writer = new StreamWriter(path, options)) writer.Write(ConfigTemplate.Text);
        _out.WriteLine($"Wrote {path}");
        _err.WriteLine("Edit the file, set tenant_id and client_id, and provide the secret via SBRIDGE_CLIENT_SECRET. Then run 'sbridge config test'.");
        return Task.FromResult(ExitOk);
    }

    private async Task<int> ConfigTest(ResolvedConfig cfg, string? workspace, CancellationToken ct)
    {
        if (workspace is not null && string.IsNullOrWhiteSpace(workspace)) throw new BridgeException(ErrorKind.Usage, "Workspace name or ID is empty.");
        _out.WriteLine($"profile: {cfg.ProfileName}, cloud: {cfg.Cloud.ToString().ToLowerInvariant()}");
        using var rt = CreateRuntime(cfg);
        var failed = false;

        var tokenOk = await Step("token", async () =>
        {
            var token = await rt.Tokens.GetTokenAsync(ct).ConfigureAwait(false);
            return $"acquired, valid for {Math.Max(0, (int)(token.ExpiresOn - DateTimeOffset.UtcNow).TotalMinutes)} min";
        }).ConfigureAwait(false);
        failed |= !tokenOk;

        var restOk = false;
        if (tokenOk)
        {
            restOk = await Step("rest", async () =>
            {
                await rt.Rest.PingAsync(ct).ConfigureAwait(false);
                return "Power BI REST API reachable and authorized";
            }).ConfigureAwait(false);
            failed |= !restOk;
        }
        else Skipped("rest", "no token");

        var wsInput = !string.IsNullOrWhiteSpace(workspace) ? workspace : cfg.DefaultWorkspace;
        if (string.IsNullOrWhiteSpace(wsInput)) Skipped("xmla", "no workspace (pass -w or set default_workspace)");
        else if (!restOk) Skipped("xmla", "workspace lookup needs the REST step to pass");
        else
        {
            var xmlaOk = await Step("xmla", async () =>
            {
                var resolved = await rt.Service.ResolveWorkspaceAsync(wsInput, ct).ConfigureAwait(false);
                await rt.Xmla.TestConnectionAsync(resolved.XmlaName, null, ct).ConfigureAwait(false);
                return $"connected to workspace '{resolved.Workspace.Name}'";
            }).ConfigureAwait(false);
            failed |= !xmlaOk;
        }
        return failed ? ExitRuntime : ExitOk;
    }

    private async Task<bool> Step(string name, Func<Task<string>> body)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var detail = await body().ConfigureAwait(false);
            _out.WriteLine($"[ ok ] {name}: {_scrubber.Scrub(detail)} ({sw.ElapsedMilliseconds} ms)");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = _scrubber.Scrub(ex.Message);
            _out.WriteLine($"[FAIL] {name}: {message} ({sw.ElapsedMilliseconds} ms)");
            if (ErrorHints.For(ex, message) is { } hint) _out.WriteLine("       hint: " + hint);
            return false;
        }
    }

    private void Skipped(string name, string reason) => _out.WriteLine($"[skip] {name}: {reason}");
}
