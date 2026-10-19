namespace CloudHealthOffice.Portal.Services;

public class FeeScheduleResult
{
    public string ProcedureCode { get; set; } = string.Empty;
    public string Modifier { get; set; } = string.Empty;
    public string FeeScheduleName { get; set; } = string.Empty;
    public decimal BilledAmount { get; set; }
    public decimal AllowedAmount { get; set; }
    public decimal ContractedRate { get; set; }
    public string RateBasis { get; set; } = string.Empty; // MedicareRVU, PercentOfCharge, CaseRate
    public decimal RateMultiplier { get; set; }
    public string NetworkTier { get; set; } = string.Empty;
}
