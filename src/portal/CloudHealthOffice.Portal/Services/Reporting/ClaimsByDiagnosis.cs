namespace CloudHealthOffice.Portal.Services;

public class ClaimsByDiagnosis
{
    public string DiagnosisCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int ClaimCount { get; set; }
    public decimal TotalAmount { get; set; }
}
