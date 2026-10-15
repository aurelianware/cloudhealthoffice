using MudBlazor;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// How the portal shows a claims-service status (wire names: Approved, Deny,
/// Denied, Paid, ...). The claim lifecycle, modeled on QNXT PAY/PAID,
/// DENY/DENIED:
/// <list type="bullet">
///   <item><description>Adjudicated, not yet remitted — Pended, Pay
///     (wire "Approved"), Deny: editable (reprocess, examiner resolve /
///     override).</description></item>
///   <item><description>Finalized by the payment cycle (835 sent) — Paid,
///     Denied: only an adjustment (new version) or a void / reversal.</description></item>
///   <item><description>Corrected / reversed — Voided.</description></item>
/// </list>
/// </summary>
public static class ClaimStatusDisplay
{
    /// <summary>Phase hint for an adjudicated claim not yet remitted (Pay / Deny).</summary>
    public const string AwaitingPaymentCycle = "Adjudicated – awaiting payment cycle";

    /// <summary>Phase hint for a claim the payment cycle finalized (Paid / Denied).</summary>
    public const string Finalized = "Finalized";

    /// <summary>The label a status is shown with ("Approved" is shown as "Pay").</summary>
    public static string Label(string? status) => Normalize(status) switch
    {
        "Approved" => "Pay",
        "Deny" => "Deny",
        "Paid" => "Paid",
        "Denied" => "Denied",
        // Legacy claim-level value (never assigned any more): what it meant.
        "PartiallyPaid" => "Paid with line denials",
        "InAdjudication" => "In Adjudication",
        var other => other,
    };

    /// <summary>
    /// The label for a claim whose lines are known: a Pay / Paid claim with
    /// a denied line shows "… with line denials" (derived from the lines,
    /// not a claim-level status).
    /// </summary>
    public static string Label(string? status, IEnumerable<ClaimServiceLine>? lines)
    {
        var normalized = Normalize(status);
        var hasLineDenials = lines?.Any(l => l.IsDenied) == true;
        return normalized switch
        {
            "Approved" when hasLineDenials => "Pay with line denials",
            "Paid" when hasLineDenials => "Paid with line denials",
            _ => Label(status),
        };
    }

    /// <summary>"Adjudicated – awaiting payment cycle" (Pay / Deny), "Finalized" (Paid / Denied), "Reversed" (Voided); otherwise null.</summary>
    public static string? PhaseHint(string? status) => Normalize(status) switch
    {
        "Approved" or "Deny" => AwaitingPaymentCycle,
        "Paid" or "Denied" or "PartiallyPaid" => Finalized,
        "Voided" => "Reversed",
        _ => null,
    };

    /// <summary>Denied in either phase (Deny, or finalized Denied).</summary>
    public static bool IsDenied(string? status) => Normalize(status) is "Deny" or "Denied";

    /// <summary>Paid outcome in either phase (Pay, Paid, legacy PartiallyPaid).</summary>
    public static bool IsPayOutcome(string? status) => Normalize(status) is "Approved" or "Paid" or "PartiallyPaid";

    /// <summary>An examiner may resolve / override or reprocess it: Pended, Pay or Deny.</summary>
    public static bool IsReprocessable(string? status) => Normalize(status) is "Pended" or "Approved" or "Deny";

    /// <summary>Finalized by the payment cycle or reversed: only an adjustment or a void changes it.</summary>
    public static bool IsFinalized(string? status) => Normalize(status) is "Paid" or "Denied" or "PartiallyPaid" or "Voided";

    /// <summary>A reversal (void) applies to a finalized paid claim only.</summary>
    public static bool IsReversible(string? status) => Normalize(status) is "Paid" or "PartiallyPaid";

    public static Color Color(string? status) => Normalize(status) switch
    {
        "Approved" or "Paid" or "PartiallyPaid" => MudBlazor.Color.Success,
        "Deny" or "Denied" => MudBlazor.Color.Error,
        "Pended" => MudBlazor.Color.Warning,
        "InAdjudication" or "Received" => MudBlazor.Color.Info,
        "Voided" => MudBlazor.Color.Dark,
        _ => MudBlazor.Color.Default,
    };

    /// <summary>The status filter choices of the Claims page: (wire value, label).</summary>
    public static IReadOnlyList<(string Value, string Label)> FilterOptions { get; } =
    [
        ("Submitted", "Submitted"),
        ("InAdjudication", "In Adjudication"),
        ("Pended", "Pended"),
        ("Approved", "Pay"),
        ("Deny", "Deny"),
        ("Paid", "Paid"),
        ("Denied", "Denied"),
        ("Voided", "Voided"),
    ];

    /// <summary>Wire names, tolerant of display spellings and case.</summary>
    private static string Normalize(string? status)
    {
        var s = (status ?? string.Empty).Trim();
        return s.ToLowerInvariant() switch
        {
            "approved" or "pay" => "Approved",
            "deny" => "Deny",
            "denied" => "Denied",
            "paid" => "Paid",
            "partiallypaid" or "partially paid" => "PartiallyPaid",
            "pended" => "Pended",
            "inadjudication" or "in adjudication" => "InAdjudication",
            "voided" => "Voided",
            "received" => "Received",
            "submitted" => "Submitted",
            _ => s,
        };
    }
}
