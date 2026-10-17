namespace CloudHealthOffice.Portal.Services;

public class ClaimLineAdjudicationProjection
{
    public decimal AllowedAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal PatientResponsibility { get; set; }

    /// <summary>The line's 835 CAS adjustments (group, CARC, amount, RARC).</summary>
    public List<ClaimLineAdjustment> AdjustmentReasons { get; set; } = new();
}
