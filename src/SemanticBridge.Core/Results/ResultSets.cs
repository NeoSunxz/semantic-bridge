namespace SemanticBridge.Core.Results;

/// <summary>Truncation notice for queries that return more than one result set (several EVALUATE statements).</summary>
public static class ResultSets
{
    public const string MultipleNotice =
        "The query returned more than one result set (multiple EVALUATE statements); only the first is returned. Run each EVALUATE as its own query.";

    /// <summary>Truncation state and reason from the row cap plus whether further result sets exist; both facts are kept.</summary>
    public static (bool Truncated, string? Reason) Combine(RowCap cap, bool moreResultSets)
    {
        var reasons = new List<string>();
        if (cap.Reason is { } capReason) reasons.Add(capReason);
        if (moreResultSets) reasons.Add(MultipleNotice);
        return (reasons.Count > 0, reasons.Count > 0 ? string.Join(" ", reasons) : null);
    }
}
