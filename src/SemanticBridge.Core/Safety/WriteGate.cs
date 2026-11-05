namespace SemanticBridge.Core.Safety;

/// <summary>
/// Single decision point for write capabilities. Writes need <c>allow_write: true</c>; over HTTP they
/// additionally need the explicit <c>--allow-http-write</c> opt-in. No write command exists yet.
/// </summary>
public sealed class WriteGate(bool allowWrite, bool overHttp = false, bool allowHttpWrite = false)
{
    public bool OverHttp => overHttp;

    public bool IsOpen => allowWrite && (!overHttp || allowHttpWrite);

    public string? ClosedReason =>
        !allowWrite ? "Writes are disabled. Set allow_write: true in the profile to enable them."
        : overHttp && !allowHttpWrite ? "Writes over HTTP also require --allow-http-write (or SBRIDGE_ALLOW_HTTP_WRITE=1)."
        : null;

    public void EnsureOpen()
    {
        if (ClosedReason is { } reason) throw new BridgeException(ErrorKind.WriteDisabled, reason);
    }
}
