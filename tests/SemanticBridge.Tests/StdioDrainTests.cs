using System.Text;
using SemanticBridge.Cli.Mcp;

namespace SemanticBridge.Tests;

public sealed class StdioDrainTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    /// <summary>Hands out one scripted chunk per read, then reports EOF.</summary>
    private sealed class ScriptedStream(params string[] chunks) : Stream
    {
        private int _next;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_next >= chunks.Length) return new(0);
            var bytes = Encoding.UTF8.GetBytes(chunks[_next++]);
            bytes.CopyTo(buffer);
            return new(bytes.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    private static async Task<int> ReadToEndAsync(Stream s)
    {
        var buf = new byte[1024];
        var total = 0;
        int n;
        while ((n = await s.ReadAsync(buf.AsMemory())) > 0) total += n;
        return total;
    }

    private static async Task Respond(TrackResponsesOutput output, string json) =>
        await output.WriteAsync(Encoding.UTF8.GetBytes(json + "\n"), CancellationToken.None);

    private static (DrainOnEofInput Input, TrackResponsesOutput Output, PendingRequests Pending, StringWriter Err) Create(
        string[] chunks, TimeSpan? maxWait = null, int maxLine = DrainOnEofInput.MaxLineBytes, CancellationToken? ct = null)
    {
        var pending = new PendingRequests();
        var err = new StringWriter();
        return (new DrainOnEofInput(new ScriptedStream(chunks), pending, maxWait ?? Long, err, ct ?? TestContext.Current.CancellationToken, maxLine),
            new TrackResponsesOutput(Stream.Null, pending), pending, err);
    }

    [Fact]
    public void StringAndNumberIdsAreDistinctKeys()
    {
        var pending = new PendingRequests();
        pending.Add("1");
        pending.Add("\"1\"");
        Assert.Equal(2, pending.Count);
        pending.Remove("1");
        Assert.Equal(1, pending.Count);
        pending.Remove("\"1\"");
        Assert.Equal(0, pending.Count);
        Assert.True(pending.WhenIdleAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task EofWaitsUntilTheResponseIsWritten()
    {
        var (input, output, pending, _) = Create(["{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/list\"}\n"]);
        var read = ReadToEndAsync(input);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(read.IsCompleted);
        Assert.Equal(1, pending.Count);
        await Respond(output, "{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{}}");
        await read.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public async Task StringIdIsMatchedByStringResponseOnly()
    {
        var (input, output, pending, _) = Create(["{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"method\":\"x\"}\n"]);
        var read = ReadToEndAsync(input);
        await Respond(output, "{\"jsonrpc\":\"2.0\",\"id\":\"abd\",\"result\":{}}");
        await Respond(output, "{\"jsonrpc\":\"2.0\",\"id\":0,\"result\":{}}");
        Assert.Equal(1, pending.Count);
        await Respond(output, "{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"result\":{}}");
        await read.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ServerInitiatedRequestOnOutputDoesNotClearPending()
    {
        var (input, output, pending, _) = Create(["{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\"}\n"]);
        var read = ReadToEndAsync(input);
        await Respond(output, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"sampling/createMessage\"}");
        Assert.Equal(1, pending.Count);
        await Respond(output, "{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{}}");
        await read.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancelledNotificationClearsThePendingRequest()
    {
        var (input, _, pending, _) = Create([
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\"}\n",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":5}}\n"]);
        await ReadToEndAsync(input).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public async Task LineSplitAcrossReadsIsTracked()
    {
        var (input, output, pending, _) = Create(["{\"jsonrpc\":\"2.0\",\"id\":9,\"me", "thod\":\"tools/list\"}", "\n"]);
        var read = ReadToEndAsync(input);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(read.IsCompleted);
        Assert.Equal(1, pending.Count);
        await Respond(output, "{\"jsonrpc\":\"2.0\",\"id\":9,\"result\":{}}");
        await read.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[1]")]
    public async Task IdsThatAreNotStringOrNumberAreNotTracked(string id)
    {
        var (input, _, pending, _) = Create([$"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"x\"}}\n"]);
        await ReadToEndAsync(input).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public async Task CancellationReleasesTheWait()
    {
        using var cts = new CancellationTokenSource();
        var (input, _, pending, err) = Create(["{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\"}\n"], ct: cts.Token);
        var read = ReadToEndAsync(input);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(read.IsCompleted);
        await cts.CancelAsync();
        await read.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, pending.Count);
        Assert.Equal(string.Empty, err.ToString());
    }

    [Fact]
    public async Task BoundedWaitExpiresAndSaysSoOnStderr()
    {
        var (input, _, _, err) = Create(["{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\"}\n"], maxWait: TimeSpan.FromMilliseconds(100));
        await ReadToEndAsync(input).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var text = err.ToString();
        Assert.Contains("mcp: stdin closed; 1 request(s) still pending after 0.1 s, exiting", text);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task OversizedLineEndsInputWithStderrNoteAndNoOutput()
    {
        var (input, _, _, err) = Create(["{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + new string('x', 200) + "\"}\n"], maxLine: 64);
        var total = await ReadToEndAsync(input).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, total);
        Assert.Contains("exceeded 64 bytes", err.ToString());
    }
}
