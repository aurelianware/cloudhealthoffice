namespace CloudHealthOffice.Portal.Services;

public class TerminateEnrollmentRequest
{
    public string MemberId { get; set; } = string.Empty;
    public string CoverageId { get; set; } = string.Empty;
    public DateTime TerminationDate { get; set; }
    public string ReasonCode { get; set; } = string.Empty; // 1=Voluntary, 2=Involuntary, 3=Death, 4=Medicare, 5=Medicaid, 6=Other
    public string? Notes { get; set; }
}
