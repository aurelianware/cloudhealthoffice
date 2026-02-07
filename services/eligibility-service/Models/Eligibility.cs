using System.Text.Json.Serialization;

namespace EligibilityService.Models;

/// <summary>
/// 270 Eligibility Inquiry Request
/// </summary>
public class EligibilityInquiry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    
    public string TenantId { get; set; } = string.Empty;
    
    // Trading Partner
    public string PayerId { get; set; } = string.Empty;  // 2000A NM1*PR
    public string PayerName { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;  // 2000B NM1*1P
    public string ProviderNPI { get; set; } = string.Empty;
    
    // Subscriber (2000C)
    public string SubscriberId { get; set; } = string.Empty;  // REF*0F
    public string SubscriberFirstName { get; set; } = string.Empty;
    public string SubscriberLastName { get; set; } = string.Empty;
    public DateTime SubscriberDOB { get; set; }
    public string SubscriberGender { get; set; } = string.Empty;  // M/F
    public string GroupNumber { get; set; } = string.Empty;  // REF*1L
    
    // Dependent (2000D - optional)
    public string? DependentFirstName { get; set; }
    public string? DependentLastName { get; set; }
    public DateTime? DependentDOB { get; set; }
    public string? DependentGender { get; set; }
    public string? DependentRelationship { get; set; }  // INS02: 01=Spouse, 19=Child
    
    // Inquiry Details (2110C/D)
    public string ServiceTypeCode { get; set; } = "30";  // EB01: 30=Health Benefit Plan Coverage
    public DateTime? ServiceDateFrom { get; set; }
    public DateTime? ServiceDateTo { get; set; }
    
    // Request metadata
    public DateTime RequestDate { get; set; } = DateTime.UtcNow;
    public string ControlNumber { get; set; } = string.Empty;  // ST02/GS06
    public EligibilityInquiryStatus Status { get; set; } = EligibilityInquiryStatus.Pending;
    public DateTime? ResponseDate { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public string? ResponseId { get; set; }
    
    // Line of Business
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LineOfBusiness LineOfBusiness { get; set; } = LineOfBusiness.Commercial;
}

/// <summary>
/// 271 Eligibility Response
/// </summary>
public class EligibilityResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    
    public string InquiryId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    
    // Response status
    public string ResponseCode { get; set; } = string.Empty;  // AAA03: Y=Yes/N=No/U=Unknown
    public string StatusCode { get; set; } = string.Empty;  // EB06: 1=Active, 6=Inactive, etc.
    public string? RejectionReason { get; set; }  // AAA03-4/MSG
    
    // Coverage information
    public bool IsCovered { get; set; }
    public string CoverageLevel { get; set; } = string.Empty;  // EB03: EMP/ESP/ECH/FAM
    public string InsurancePlanName { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public DateTime? CoverageBeginDate { get; set; }  // DTP*348
    public DateTime? CoverageEndDate { get; set; }  // DTP*349
    
    // Benefit details (2110C/D loops)
    public List<EligibilityBenefit> Benefits { get; set; } = new();
    
    // Deductible/OOP information
    public DeductibleInfo? Deductible { get; set; }
    public OutOfPocketInfo? OutOfPocket { get; set; }
    
    // Additional coverage info
    public List<AdditionalInsurance>? AdditionalInsurances { get; set; }
    
    // Timestamps
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    
    // Response metadata
    public DateTime ResponseDate { get; set; } = DateTime.UtcNow;
    public string ControlNumber { get; set; } = string.Empty;
}

/// <summary>
/// Benefit segment (EB segment in 271)
/// </summary>
public class EligibilityBenefit
{
    public string ServiceTypeCode { get; set; } = string.Empty;  // EB01: 30=Health, 33=Chiro, etc.
    public string ServiceTypeName { get; set; } = string.Empty;
    public string CoverageLevel { get; set; } = string.Empty;  // EB03: IND/FAM
    public string InsuranceType { get; set; } = string.Empty;  // EB02: HLT/DEN/VIS
    public string TimePeriodQualifier { get; set; } = string.Empty;  // EB06: 26=Visit, 27=Day, 29=Year
    
    // Benefit amounts
    public decimal? MonetaryAmount { get; set; }  // EB07: Copay/coinsurance amount
    public decimal? Percentage { get; set; }  // EB08: Coinsurance %
    public string? QuantityQualifier { get; set; }  // EB09
    public decimal? Quantity { get; set; }  // EB10: Visit limits, etc.
    
    // In/Out of Network
    public string NetworkIndicator { get; set; } = "Y";  // EB12: Y=In Network, N=Out
    
    // Additional info
    public string? AuthorizationRequired { get; set; }  // MSG segment
    public DateTime? BenefitBeginDate { get; set; }
    public DateTime? BenefitEndDate { get; set; }
}

/// <summary>
/// Deductible information
/// </summary>
public class DeductibleInfo
{
    public decimal IndividualDeductible { get; set; }
    public decimal IndividualDeductibleMet { get; set; }
    public decimal IndividualDeductibleRemaining { get; set; }
    public decimal FamilyDeductible { get; set; }
    public decimal FamilyDeductibleMet { get; set; }
    public decimal FamilyDeductibleRemaining { get; set; }
    public string TimePeriod { get; set; } = "Year";  // Calendar Year, Service Year
}

/// <summary>
/// Out-of-pocket maximum information
/// </summary>
public class OutOfPocketInfo
{
    public decimal IndividualOOPMax { get; set; }
    public decimal IndividualOOPMet { get; set; }
    public decimal IndividualOOPRemaining { get; set; }
    public decimal FamilyOOPMax { get; set; }
    public decimal FamilyOOPMet { get; set; }
    public decimal FamilyOOPRemaining { get; set; }
    public string TimePeriod { get; set; } = "Year";
}

/// <summary>
/// Additional insurance (COB - Coordination of Benefits)
/// </summary>
public class AdditionalInsurance
{
    public string PayerName { get; set; } = string.Empty;
    public string PayerId { get; set; } = string.Empty;
    public string CoverageSequence { get; set; } = string.Empty;  // P=Primary, S=Secondary, T=Tertiary
    public string GroupNumber { get; set; } = string.Empty;
    public DateTime? EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public DateTime? CoverageBeginDate { get; set; }
    public DateTime? CoverageEndDate { get; set; }
}

public enum EligibilityInquiryStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
    Rejected
}

public enum LineOfBusiness
{
    Commercial,
    Medicare,
    Medicaid,
    Exchange,
    TRICARE,
    VA
}
