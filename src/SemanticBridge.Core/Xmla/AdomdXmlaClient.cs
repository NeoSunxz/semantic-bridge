using System.Data;
using System.Diagnostics;
using Microsoft.AnalysisServices.AdomdClient;
using SemanticBridge.Core.Diagnostics;
using SemanticBridge.Core.Results;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Core.Xmla;

/// <summary>XMLA queries and model metadata through ADOMD.NET.</summary>
public sealed class AdomdXmlaClient(IAdomdConnectionFactory factory, SecretScrubber scrubber, int timeoutSeconds) : IXmlaClient
{
    /// <summary>Row cap for metadata queries (INFO.*), matching the global ceiling.</summary>
    private const int MetadataMaxRows = 100_000;

    public async Task<QueryResult> QueryAsync(string workspace, string dataset, string dax, int maxRows, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var connection = await factory.OpenAsync(workspace, dataset, ct).ConfigureAwait(false);
        return await Task.Run(() => ReadQuery(connection, dax, maxRows, sw, ct), ct).ConfigureAwait(false);
    }

    public async Task<ModelSchema> GetSchemaAsync(string workspace, string dataset, string? tableFilter, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var connection = await factory.OpenAsync(workspace, dataset, ct).ConfigureAwait(false);
        return await Task.Run(() => ReadSchema(connection, tableFilter, sw, ct), ct).ConfigureAwait(false);
    }

    public async Task TestConnectionAsync(string workspace, string? dataset, CancellationToken ct)
    {
        using var connection = await factory.OpenAsync(workspace, dataset, ct).ConfigureAwait(false);
    }

    private ModelSchema ReadSchema(AdomdConnection connection, string? tableFilter, Stopwatch sw, CancellationToken ct)
    {
        var warnings = new List<string>();
        IReadOnlyList<MetaRow> tables, columns, measures, relationships;
        try
        {
            tables = Info(connection, "TABLES", warnings, sw, ct);
            columns = Info(connection, "COLUMNS", warnings, sw, ct);
            measures = Info(connection, "MEASURES", warnings, sw, ct);
            relationships = Info(connection, "RELATIONSHIPS", warnings, sw, ct);
        }
        catch (BridgeException ex) when (ex.Kind is ErrorKind.Dax or ErrorKind.Xmla)
        {
            // INFO.* DAX functions are not available everywhere; the TMSCHEMA_* schema rowsets carry the same data.
            warnings.Clear();
            warnings.Add(InfoFallbackWarning(ex.Message));
            tables = Rowset(connection, "TMSCHEMA_TABLES", warnings, ct);
            columns = Rowset(connection, "TMSCHEMA_COLUMNS", warnings, ct);
            measures = Rowset(connection, "TMSCHEMA_MEASURES", warnings, ct);
            relationships = Rowset(connection, "TMSCHEMA_RELATIONSHIPS", warnings, ct);
        }
        return SchemaBuilder.Build(tables, columns, measures, relationships, tableFilter, sw.ElapsedMilliseconds, warnings);
    }

    internal string InfoFallbackWarning(string engineMessage) =>
        "INFO.* functions failed (" + scrubber.Scrub(EngineMessage.Clean(engineMessage)) + "); metadata was read from TMSCHEMA_* rowsets instead.";

    private IReadOnlyList<MetaRow> Info(AdomdConnection connection, string function, List<string> warnings, Stopwatch sw, CancellationToken ct)
    {
        var result = ReadQuery(connection, $"EVALUATE INFO.{function}()", MetadataMaxRows, sw, ct);
        if (result.Truncated) warnings.Add($"INFO.{function} returned more than {MetadataMaxRows} rows; the schema is incomplete.");
        return MetaRow.From(result);
    }

    private IReadOnlyList<MetaRow> Rowset(AdomdConnection connection, string name, List<string> warnings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var set = connection.GetSchemaDataSet(name, null);
            return set.Tables.Count == 0 ? [] : CapRows(MetaRow.From(set.Tables[0]), name, warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BridgeException(ErrorKind.Xmla, $"Reading {name} failed: " + scrubber.Scrub(EngineMessage.Clean(ex.Message)));
        }
    }

    /// <summary>Applies the metadata row cap to a rowset and reports it when hit.</summary>
    public static IReadOnlyList<MetaRow> CapRows(IReadOnlyList<MetaRow> rows, string name, List<string> warnings)
    {
        if (rows.Count <= MetadataMaxRows) return rows;
        warnings.Add($"{name} returned more than {MetadataMaxRows} rows; the schema is incomplete.");
        return [.. rows.Take(MetadataMaxRows)];
    }

    private QueryResult ReadQuery(AdomdConnection connection, string dax, int maxRows, Stopwatch sw, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = dax;
        command.CommandTimeout = timeoutSeconds;
        using var registration = ct.Register(() =>
        {
            try { command.Cancel(); }
            catch (Exception) { /* the command may already be finished */ }
        });
        try
        {
            using var reader = command.ExecuteReader();
            var count = reader.FieldCount;
            var names = new string[count];
            var columns = new ColumnInfo[count];
            for (var i = 0; i < count; i++)
            {
                names[i] = reader.GetName(i);
                columns[i] = new ColumnInfo(names[i], TypeName(reader, i));
            }

            var cap = new RowCap(maxRows);
            var sanitizer = new ValueSanitizer();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (!cap.Add(() =>
                    {
                        var row = new object?[count];
                        for (var i = 0; i < count; i++) row[i] = sanitizer.Sanitize(reader.GetValue(i), names[i]);
                        return row;
                    }))
                    break;
            }

            return new QueryResult
            {
                Columns = columns,
                Rows = cap.Rows,
                Truncated = cap.Truncated,
                TruncationReason = cap.Reason,
                Path = ResultPaths.Xmla,
                ElapsedMs = sw.ElapsedMilliseconds,
                Warnings = sanitizer.Warnings,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BridgeException)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            var kind = ex is AdomdErrorResponseException ? ErrorKind.Dax : ErrorKind.Xmla;
            throw new BridgeException(kind, scrubber.Scrub(EngineMessage.Clean(ex.Message)));
        }
    }

    private static string TypeName(IDataReader reader, int i)
    {
        try { return reader.GetFieldType(i)?.Name ?? "Unknown"; }
        catch (Exception) { return "Unknown"; }
    }
}
