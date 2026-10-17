namespace CloudHealthOffice.Portal.Services;

// ── PR15: Payment Runs ───────────────────────────────────────────────────────

public class PaymentRunSummary
{
    public string RunId { get; set; } = string.Empty;
    public string RunName { get; set; } = string.Empty;
    public string LineOfBusiness { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // Pending, Running, Completed, Failed, Cancelled
    public DateTime CreatedDate { get; set; }
    public DateTime? StartedDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public int ClaimCount { get; set; }
    public int ProcessedCount { get; set; }
    public decimal TotalAmount { get; set; }
    public string? ErrorMessage { get; set; }
    public string? EraFileUrl { get; set; }
}
