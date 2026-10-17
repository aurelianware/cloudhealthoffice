namespace CloudHealthOffice.Portal.Services;

public class MassAdjudicationPaymentDeltaBucket
{
    public string Label { get; set; } = string.Empty;
    public decimal? LowerBoundExclusive { get; set; }
    public decimal? UpperBoundInclusive { get; set; }
    public int Count { get; set; }
}
