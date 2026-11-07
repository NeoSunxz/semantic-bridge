using System.Globalization;

namespace SemanticBridge.Core.Results;

/// <summary>
/// Makes engine values JSON/CSV-safe. NaN and Infinity cannot be represented in JSON, so they become null
/// and are counted so the result can carry a warning (never silently dropped).
/// </summary>
public sealed class ValueSanitizer
{
    private readonly Dictionary<string, int> _nonFinite = [];

    public object? Sanitize(object? value, string columnName)
    {
        switch (value)
        {
            case null or DBNull:
                return null;
            case double d when !double.IsFinite(d):
                return CountNonFinite(columnName);
            case float f when !float.IsFinite(f):
                return CountNonFinite(columnName);
            case DateTime dt:
                return FormatDateTime(dt);
            case DateTimeOffset dto:
                return FormatDateTime(dto.DateTime) + dto.ToString("zzz", CultureInfo.InvariantCulture);
            case TimeSpan ts:
                return ts.ToString("c", CultureInfo.InvariantCulture);
            case byte[] bytes:
                return Convert.ToBase64String(bytes);
            case string or bool or long or int or short or byte or sbyte or ushort or uint or ulong or decimal or double or float:
                return value;
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }

    // Same shape the REST API returns: no offset or Z, milliseconds only when non-zero (trailing zeros trimmed).
    private static string FormatDateTime(DateTime dt)
    {
        var text = dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return dt.Millisecond == 0 ? text : text + "." + dt.Millisecond.ToString("000", CultureInfo.InvariantCulture).TrimEnd('0');
    }

    private object? CountNonFinite(string columnName)
    {
        _nonFinite[columnName] = _nonFinite.GetValueOrDefault(columnName) + 1;
        return null;
    }

    public IReadOnlyList<string> Warnings =>
        [.. _nonFinite.Select(kv => $"{kv.Value} NaN/Infinity value(s) in column {kv.Key} were returned as null.")];
}
