namespace CloudHealthOffice.Portal.Services;

public class NetworkAssignment
{
    public string NetworkId { get; set; } = string.Empty;
    public string NetworkName { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string Status { get; set; } = string.Empty;

    // ── Panel-gating projection (capability 5.5) ───────────────────────
    // Surfaced read-only on the portal so operators can audit whether
    // each participation has been touched by panel-gating-aware code.
    // Authoring still happens via the provider-service API; future
    // capability adds inline edit capability.
    public string LineOfBusiness { get; set; } = string.Empty;
    public int? PanelLimit { get; set; }
    public bool? PanelAccepted { get; set; }
    public List<string> AcceptedLobs { get; set; } = new();
    public int? MinAcceptedAgeYears { get; set; }
    public int? MaxAcceptedAgeYears { get; set; }
}
