namespace CloudHealthOffice.Portal.Services;

public class ProviderContract
{
    public string ContractId { get; set; } = string.Empty;
    public string ReimbursementMethod { get; set; } = string.Empty; // Fee Schedule, Capitation, Case Rate
    public string FeeScheduleTier { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public decimal? CapitationRate { get; set; }
}
