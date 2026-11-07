namespace SemanticBridge.Core.Safety;

public enum ErrorKind
{
    /// <summary>Invalid or missing configuration (CLI exit code 2).</summary>
    Config,
    /// <summary>Invalid input to a command, e.g. a query that is not DAX (CLI exit code 2).</summary>
    Usage,
    Auth,
    Rest,
    Xmla,
    NotFound,
    Ambiguous,
    Dax,
    WriteDisabled,
}

/// <summary>
/// The only exception type that leaves Core for expected failures. The message is always scrubbed.
/// </summary>
public class BridgeException(ErrorKind kind, string message, Exception? inner = null, int? httpStatus = null, string? code = null) : Exception(message, inner)
{
    public ErrorKind Kind { get; } = kind;

    /// <summary>The HTTP status code of the failed REST response, when the error came from one.</summary>
    public int? HttpStatus { get; } = httpStatus;

    /// <summary>The error code from the REST error body (for example DaxResultsetCountNotSupported), when there was one.</summary>
    public string? Code { get; } = code;

    /// <summary>True for errors the user fixes by changing their input or config (CLI exit code 2).</summary>
    public bool IsUsageError => Kind is ErrorKind.Config or ErrorKind.Usage or ErrorKind.NotFound or ErrorKind.Ambiguous or ErrorKind.WriteDisabled;
}
