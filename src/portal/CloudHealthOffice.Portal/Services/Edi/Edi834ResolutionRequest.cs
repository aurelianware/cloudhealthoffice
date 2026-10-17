namespace CloudHealthOffice.Portal.Services;

public class Edi834ResolutionRequest
{
    public string BatchId { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty; // Accept, Reject, Hold
    public string? Notes { get; set; }
}
