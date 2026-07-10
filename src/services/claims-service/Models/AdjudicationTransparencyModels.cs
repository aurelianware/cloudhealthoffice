namespace ClaimsService.Models;

public class NcciEditResult
{
    public string EditCode { get; set; } = string.Empty;
    public string EditType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string? FailureReason { get; set; }
    public string? AffectedProcedureCode { get; set; }
    public string? AffectedModifier { get; set; }
    public string? ResolutionApplied { get; set; }
}

public class FeeScheduleResult
{
    public string ProcedureCode { get; set; } = string.Empty;
    public string Modifier { get; set; } = string.Empty;
    public string FeeScheduleName { get; set; } = string.Empty;
    public decimal BilledAmount { get; set; }
    public decimal AllowedAmount { get; set; }
    public decimal ContractedRate { get; set; }
    public string RateBasis { get; set; } = string.Empty;
    public decimal RateMultiplier { get; set; }
    public string NetworkTier { get; set; } = string.Empty;
}

public class AccumulatorUpdate
{
    public string AccumulatorType { get; set; } = string.Empty;
    public decimal AmountApplied { get; set; }
    public decimal NewBalance { get; set; }
    public decimal Limit { get; set; }
}

public class BenefitCalculationResult
{
    public string ServiceType { get; set; } = string.Empty;
    public string BenefitRuleApplied { get; set; } = string.Empty;
    public string NetworkTier { get; set; } = string.Empty;
    public decimal AllowedAmount { get; set; }
    public decimal DeductibleApplied { get; set; }
    public decimal DeductibleRemaining { get; set; }
    public decimal CopayAmount { get; set; }
    public decimal CoinsuranceAmount { get; set; }
    public decimal PlanPayment { get; set; }
    public decimal MemberResponsibility { get; set; }
    public bool DeductibleMet { get; set; }
    public bool OopMaxMet { get; set; }
    public decimal IndividualDeductibleBalance { get; set; }
    public decimal IndividualDeductibleLimit { get; set; }
    public decimal IndividualOopBalance { get; set; }
    public decimal IndividualOopLimit { get; set; }
    public List<AccumulatorUpdate> AccumulatorUpdates { get; set; } = new();
}

public class AdjudicationStep
{
    public string StepName { get; set; } = string.Empty;
    public int StepNumber { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? Timestamp { get; set; }
    public int? DurationMs { get; set; }
    public string? Summary { get; set; }
    public string? ErrorDetail { get; set; }
}

public class AdjudicationTransparencyData
{
    public List<AdjudicationStep> Steps { get; set; } = new();
    public List<NcciEditResult> NcciResults { get; set; } = new();
    public List<FeeScheduleResult> FeeScheduleResults { get; set; } = new();
    public BenefitCalculationResult? BenefitCalculation { get; set; }
}
