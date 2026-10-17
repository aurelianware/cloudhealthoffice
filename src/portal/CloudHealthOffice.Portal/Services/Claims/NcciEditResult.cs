namespace CloudHealthOffice.Portal.Services;

// ── PR16: Adjudication Transparency ─────────────────────────────────────────

public class NcciEditResult
{
    public string EditCode { get; set; } = string.Empty;
    public string EditType { get; set; } = string.Empty; // MUE, NCCI-PTP, NCCI-MU
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string? FailureReason { get; set; }
    public string? AffectedProcedureCode { get; set; }
    public string? AffectedModifier { get; set; }
    public string? ResolutionApplied { get; set; }
}
