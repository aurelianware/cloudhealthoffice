namespace ClaimsExaminerService.Models;

/// <summary>
/// Aggregated history of Request-for-Additional-Information (RFAI) cases that
/// have been raised against a specific provider for a specific edit type. Used
/// as an input signal to the AI examiner: a provider with a chronically poor
/// RFAI response rate is a stronger candidate for EscalateToHuman than one with
/// a clean record on the same edit, all else equal.
///
/// V1 sources this from rfai-service when wired (see IProviderRfaiHistoryClient).
/// The default implementation returns an empty list — the prompt builder treats
/// missing history as a neutral signal, not as evidence either way.
/// </summary>
public class ProviderRfaiHistory
{
    /// <summary>
    /// Edit-type tag this history is for (e.g., "NCCI", "NE001"). Lets the
    /// orchestrator scope history to the edit currently under review rather
    /// than dragging in unrelated past RFAI activity.
    /// </summary>
    public string EditCode { get; set; } = string.Empty;

    /// <summary>Total RFAIs sent to this provider for this edit type.</summary>
    public int TotalRfaisSent { get; set; }

    /// <summary>Number of RFAIs the provider responded to.</summary>
    public int TotalResponded { get; set; }

    /// <summary>Response rate as a percent (0–100).</summary>
    public double ResponseRatePct { get; set; }

    /// <summary>Average days between RFAI sent and provider response.</summary>
    public int AvgResponseDays { get; set; }

    /// <summary>Most recent RFAI date for this provider/edit pair.</summary>
    public DateTime? LastRfaiSentAt { get; set; }

    /// <summary>Most recent provider response date for this provider/edit pair.</summary>
    public DateTime? LastResponseAt { get; set; }
}
