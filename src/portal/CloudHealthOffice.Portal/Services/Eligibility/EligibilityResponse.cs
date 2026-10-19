namespace CloudHealthOffice.Portal.Services;

public class EligibilityResponse
{
    // Coverage status (EB*1 / AAA)
    public bool IsCovered { get; set; }
    public string? StatusCode { get; set; }  // EB06: 1=Active, 6=Inactive
    public string? RejectionReason { get; set; }  // AAA03-4 / MSG

    // Plan info (EB / REF / DTP)
    public string InsurancePlanName { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;  // REF*1L
    public string CoverageLevel { get; set; } = string.Empty;  // EB03: EMP/FAM/IND
    public string? InsuranceType { get; set; }  // EB02: HLT/DEN/VIS
    public string? LineOfBusiness { get; set; }  // Commercial/Medicare/Medicaid
    public DateTime? CoverageBeginDate { get; set; }  // DTP*348
    public DateTime? CoverageEndDate { get; set; }  // DTP*349

    // Deductible & OOP (EB*C / EB*G / EB*D)
    public DeductibleInfo? Deductible { get; set; }
    public OutOfPocketInfo? OutOfPocket { get; set; }

    // Benefits (EB segments per service type)
    public List<Benefit>? Benefits { get; set; }

    // COB / Other Insurance (SB/OI loops)
    public List<AdditionalInsuranceInfo>? AdditionalInsurances { get; set; }
}
