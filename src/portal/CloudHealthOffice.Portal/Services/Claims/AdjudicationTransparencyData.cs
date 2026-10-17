namespace CloudHealthOffice.Portal.Services;

public class AdjudicationTransparencyData
{
    public List<AdjudicationStep> Steps { get; set; } = new();
    public List<NcciEditResult> NcciResults { get; set; } = new();
    public List<FeeScheduleResult> FeeScheduleResults { get; set; } = new();
    public BenefitCalculationResult? BenefitCalculation { get; set; }
}
