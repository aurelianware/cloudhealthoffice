namespace CloudHealthOffice.Portal.Services;

public class AdditionalInsuranceInfo
{
    public string PayerName { get; set; } = string.Empty;
    public string? PayerId { get; set; }
    public string CoverageSequence { get; set; } = string.Empty;  // P/S/T
    public string? GroupNumber { get; set; }
    public DateTime? CoverageBeginDate { get; set; }
    public DateTime? CoverageEndDate { get; set; }
    public bool IsMedicare { get; set; }
}
