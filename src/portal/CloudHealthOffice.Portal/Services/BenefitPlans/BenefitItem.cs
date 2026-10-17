namespace CloudHealthOffice.Portal.Services;

public class BenefitItem
{
    public string BenefitId { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // Medical, Pharmacy, Dental, Vision, etc.
    public string Description { get; set; } = string.Empty;
    public decimal? DefaultCopay { get; set; }
    public decimal? DefaultCoinsurance { get; set; }
    public bool RequiresPriorAuth { get; set; }
}
