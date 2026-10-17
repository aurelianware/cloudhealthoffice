namespace CloudHealthOffice.Portal.Services;

// ── PR17: Reporting DTOs ─────────────────────────────────────────────────────

public class ReportRequest
{
    public DateTime DateFrom { get; set; }
    public DateTime DateTo { get; set; }
    public string? ProviderId { get; set; }
    public string? SponsorId { get; set; }
    public string? PlanId { get; set; }
}
