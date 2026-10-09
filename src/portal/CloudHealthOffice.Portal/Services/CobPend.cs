namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Whether a pended claim carries a coordination-of-benefits pend — as its
/// routing pend code, or as an additional pend ("COB: ...") a later stage
/// added (PR #1278 follow-up 4). Approving such a claim needs the payer
/// order the examiner confirmed (<c>payerSequence</c>); claims-service
/// refuses it otherwise, whichever position the COB pend is in.
/// </summary>
public static class CobPend
{
    private const string Code = "COB";

    public static bool On(WorkQueueItem? item) =>
        item is not null && Any(item.QueueReasonCode, item.PendReasons);

    public static bool On(ClaimPendDetails? pend) =>
        pend is not null && Any(pend.PendCode, pend.AdditionalPendReasons);

    /// <summary>The routing code is COB, or any "{code}: {reason}" entry's code is.</summary>
    public static bool Any(string? routingCode, IEnumerable<string>? reasons) =>
        IsCob(routingCode) || (reasons ?? []).Any(r => IsCob(CodeOf(r)));

    private static bool IsCob(string? code) =>
        string.Equals(code?.Trim(), Code, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The code of a "{code}: {reason}" entry — the same rule claims-service
    /// applies (a short token with no spaces before the first colon); null
    /// when the entry has none.
    /// </summary>
    private static string? CodeOf(string? entry)
    {
        if (string.IsNullOrEmpty(entry)) return null;
        var colon = entry.IndexOf(':');
        if (colon <= 0) return null;
        var code = entry[..colon].Trim();
        return code.Length is > 0 and <= 20 && !code.Contains(' ') ? code : null;
    }
}
