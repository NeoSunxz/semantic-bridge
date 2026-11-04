using System.Text.Encodings.Web;
using System.Text.Json;

namespace SemanticBridge.Cli.Output;

public static class JsonOutput
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
