namespace FhirService.Models;

/// <summary>
/// Result of auto-adjudication attempt by PasAutoAdjudicator.
/// </summary>
public class PasDecisionResult
{
    public bool HasDecision { get; set; }
    public string? Decision { get; set; }       // "approved" | "denied" | "modified"
    public string? DenialReasonCode { get; set; }
    public string? DenialReason { get; set; }
    public string? AuthorizationNumber { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string? RuleName { get; set; }        // Which rule triggered the decision
    public long ElapsedMs { get; set; }
}

/// <summary>
/// Configuration for PAS auto-adjudication rules.
/// Bound from Cms0057:PasAutoAdjudication config section.
/// </summary>
public class PasAutoAdjudicationConfig
{
    public bool Enabled { get; set; } = true;
    public List<string> AutoApproveServiceTypes { get; set; } = new();
    public List<string> AutoDenyServiceTypes { get; set; } = new();
    public double GoldCardThreshold { get; set; } = 0.95;
    public decimal DollarThreshold { get; set; } = 500.00m;
    public int MaxResponseMs { get; set; } = 12000;
}
