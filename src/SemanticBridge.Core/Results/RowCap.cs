namespace SemanticBridge.Core.Results;

/// <summary>
/// Collects rows up to <c>maxRows</c> and detects truncation. Every row-collecting path goes through it.
/// Callers keep reading while <see cref="Add"/> returns true: it accepts up to maxRows rows and flags
/// truncation when one more row exists, so at most maxRows + 1 rows are read.
/// </summary>
public sealed class RowCap(int maxRows)
{
    private readonly List<object?[]> _rows = [];

    public int MaxRows { get; } = maxRows;
    public IReadOnlyList<object?[]> Rows => _rows;
    public bool Truncated { get; private set; }

    /// <summary>Adds a row. Returns false (and marks the result truncated) when the row would exceed the cap; stop reading then.</summary>
    public bool Add(object?[] row) => Add(() => row);

    /// <summary>Like <see cref="Add(object?[])"/> but builds the row only when there is room, so rejected rows are never processed.</summary>
    public bool Add(Func<object?[]> buildRow)
    {
        if (_rows.Count >= MaxRows)
        {
            Truncated = true;
            return false;
        }
        _rows.Add(buildRow());
        return true;
    }

    public string? Reason => Truncated
        ? $"Result exceeds max_rows ({MaxRows}); more rows exist. Aggregate or filter in DAX (e.g. SUMMARIZECOLUMNS, TOPN), or raise the row limit (--max-rows in the CLI, max_rows in MCP)."
        : null;
}
