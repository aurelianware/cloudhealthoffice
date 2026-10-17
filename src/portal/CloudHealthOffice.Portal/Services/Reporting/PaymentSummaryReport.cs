namespace CloudHealthOffice.Portal.Services;

public class PaymentSummaryReport
{
    public DateTime PeriodFrom { get; set; }
    public DateTime PeriodTo { get; set; }
    public int EraCount { get; set; }
    public decimal TotalEraAmount { get; set; }
    public decimal AvgEraAmount { get; set; }
    public List<EraByPeriod> ByPeriod { get; set; } = new();
}
