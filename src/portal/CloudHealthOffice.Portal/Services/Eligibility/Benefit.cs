namespace CloudHealthOffice.Portal.Services;

public class Benefit
{
    public string ServiceTypeName { get; set; } = string.Empty;  // EB01 description
    public string? ServiceTypeCode { get; set; }  // EB01: 30, 33, 42, etc.
    public decimal? MonetaryAmount { get; set; }  // EB07: Copay amount
    public decimal? Percentage { get; set; }  // EB08: Coinsurance %
    public decimal? Quantity { get; set; }  // EB10: Visit/unit limit
    public string? QuantityQualifier { get; set; }  // EB09: VS=Visits, DA=Days
    public string? TimePeriod { get; set; }  // EB06: 26=Visit, 29=Year
    public string? NetworkIndicator { get; set; }  // EB12: Y=In, N=Out
    public bool AuthorizationRequired { get; set; }  // MSG segment
    public DateTime? BenefitBeginDate { get; set; }
    public DateTime? BenefitEndDate { get; set; }
}
