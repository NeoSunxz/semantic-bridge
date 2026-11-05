using System.Text.Json;

namespace SemanticBridge.Cli.Mcp;

/// <summary>
/// The MCP SDK abandons in-flight requests as soon as stdin reaches EOF, so <c>printf ... | sbridge mcp</c> would print
/// nothing. These wrappers track JSON-RPC requests that were read but not yet answered and hold the EOF back
/// until every response has been written and flushed. The wait ends early on Ctrl-C (the cancellation token) or after a bounded timeout.
/// JSON-RPC batches are not tracked (they were removed in MCP 2025-06-18).
/// </summary>
internal sealed class PendingRequests
{
    private readonly object _gate = new();
    private readonly HashSet<string> _ids = [];
    private TaskCompletionSource _idle = Completed();

    public void Add(string id)
    {
        lock (_gate)
        {
            if (_ids.Count == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ids.Add(id);
        }
    }

    /// <summary>The tracking key for a JSON-RPC id, or null when the id is not a string or number (such ids are not tracked).</summary>
    public static string? KeyOf(JsonElement id) =>
        id.ValueKind is JsonValueKind.String or JsonValueKind.Number ? id.GetRawText() : null;

    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_ids.Remove(id) && _ids.Count == 0) _idle.TrySetResult();
        }
    }

    public int Count
    {
        get { lock (_gate) return _ids.Count; }
    }

    public Task WhenIdleAsync(CancellationToken ct)
    {
        lock (_gate) return _idle.Task.WaitAsync(ct);
    }

    private static TaskCompletionSource Completed()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetResult();
        return tcs;
    }
}

/// <summary>Splits a byte stream into newline-delimited lines and hands each complete line to a callback.</summary>
internal sealed class LineSplitter(Action<ReadOnlySpan<byte>> onLine, int maxLineBytes = int.MaxValue)
{
    private readonly List<byte> _buffer = [];

    /// <summary>True once a single line exceeded the cap; further input is ignored.</summary>
    public bool Overflowed { get; private set; }

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (Overflowed) return;
            if (b != (byte)'\n')
            {
                if (_buffer.Count >= maxLineBytes)
                {
                    Overflowed = true;
                    _buffer.Clear();
                    return;
                }
                _buffer.Add(b);
                continue;
            }
            if (_buffer.Count > 0) onLine(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buffer));
            _buffer.Clear();
        }
    }
}

internal sealed class DrainOnEofInput : Stream
{
    internal const int MaxLineBytes = 4 * 1024 * 1024;

    private readonly Stream _inner;
    private readonly PendingRequests _pending;
    private readonly TimeSpan _maxWait;
    private readonly TextWriter _err;
    private readonly CancellationToken _ct;
    private readonly LineSplitter _lines;
    private readonly int _maxLine;
    private bool _ended;

    public DrainOnEofInput(Stream inner, PendingRequests pending, TimeSpan maxWait, TextWriter err, CancellationToken ct, int maxLineBytes = MaxLineBytes)
    {
        _inner = inner;
        _pending = pending;
        _maxWait = maxWait;
        _err = err;
        _ct = ct;
        _maxLine = maxLineBytes;
        _lines = new LineSplitter(line => Track(line, pending), maxLineBytes);
    }

    private static void Track(ReadOnlySpan<byte> line, PendingRequests pending)
    {
        if (!TryParse(line, out var root)) return;
        using (root)
        {
            var e = root.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return;
            var hasMethod = e.TryGetProperty("method", out var method);
            if (hasMethod && e.TryGetProperty("id", out var id))
            {
                if (PendingRequests.KeyOf(id) is { } key) pending.Add(key);
            }
            else if (hasMethod && method.ValueKind == JsonValueKind.String && method.GetString() == "notifications/cancelled"
                && e.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("requestId", out var rid)
                && PendingRequests.KeyOf(rid) is { } rkey)
                pending.Remove(rkey);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_ended) return 0;
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (n > 0)
        {
            _lines.Feed(buffer.Span[..n]);
            if (!_lines.Overflowed) return n;
            _ended = true;
            _err.WriteLine($"mcp: an input line exceeded {_maxLine} bytes; closing input");
            return 0;
        }
        _ended = true;
        try
        {
            await _pending.WhenIdleAsync(_ct).WaitAsync(_maxWait, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down on Ctrl-C: stop waiting and report EOF.
        }
        catch (TimeoutException)
        {
            _err.WriteLine($"mcp: stdin closed; {_pending.Count} request(s) still pending after {_maxWait.TotalSeconds:0.###} s, exiting");
        }
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    internal static bool TryParse(ReadOnlySpan<byte> line, out JsonDocument doc)
    {
        try
        {
            doc = JsonDocument.Parse(line.ToArray());
            return true;
        }
        catch (JsonException)
        {
            doc = null!;
            return false;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class TrackResponsesOutput(Stream inner, PendingRequests pending) : Stream
{
    private readonly object _gate = new();
    private readonly LineSplitter _lines = new(line =>
    {
        if (!DrainOnEofInput.TryParse(line, out var root)) return;
        using (root)
        {
            var e = root.RootElement;
            if (e.ValueKind == JsonValueKind.Object && !e.TryGetProperty("method", out _) && e.TryGetProperty("id", out var id)
                && PendingRequests.KeyOf(id) is { } key)
                pending.Remove(key);
        }
    });

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate) _lines.Feed(buffer.Span);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override void Flush() => inner.Flush();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
