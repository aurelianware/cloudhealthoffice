using System.Text.Json.Serialization;

namespace ClaimsScrubbingService.Models;

/// <summary>
/// Base claim structure common to all 837 transaction types (837P, 837I, 837D).
/// </summary>
public class X12837Claim
{
    [JsonPropertyName("claimId")]
    public string ClaimId { get; set; } = string.Empty;

    [JsonPropertyName("claimType")]
    public string ClaimType { get; set; } = string.Empty; // 837P | 837I | 837D

    [JsonPropertyName("transactionControlNumber")]
    public string TransactionControlNumber { get; set; } = string.Empty;

    [JsonPropertyName("interchangeControlNumber")]
    public string InterchangeControlNumber { get; set; } = string.Empty;

    [JsonPropertyName("transactionDate")]
    public string TransactionDate { get; set; } = string.Empty;

    [JsonPropertyName("submitter")]
    public ClaimSubmitter Submitter { get; set; } = new();

    [JsonPropertyName("receiver")]
    public ClaimReceiver Receiver { get; set; } = new();

    [JsonPropertyName("billingProvider")]
    public BillingProvider BillingProvider { get; set; } = new();

    [JsonPropertyName("subscriber")]
    public ClaimSubscriber Subscriber { get; set; } = new();

    [JsonPropertyName("patient")]
    public ClaimPatient? Patient { get; set; }

    [JsonPropertyName("claimHeader")]
    public ClaimHeader ClaimHeader { get; set; } = new();

    [JsonPropertyName("serviceLines")]
    public List<ServiceLine> ServiceLines { get; set; } = new();

    [JsonPropertyName("totalClaimedAmount")]
    public decimal TotalClaimedAmount { get; set; }

    [JsonPropertyName("rawEdi")]
    public string? RawEdi { get; set; }

    [JsonPropertyName("parsedAt")]
    public string ParsedAt { get; set; } = string.Empty;
}

/// <summary>Submitter (Loop 1000A) information.</summary>
public class ClaimSubmitter
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("identificationCode")]
    public string IdentificationCode { get; set; } = string.Empty;

    [JsonPropertyName("identificationQualifier")]
    public string IdentificationQualifier { get; set; } = string.Empty;

    [JsonPropertyName("contact")]
    public SubmitterContact? Contact { get; set; }
}

public class SubmitterContact
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }
}

/// <summary>Receiver (Loop 1000B) information.</summary>
public class ClaimReceiver
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("identificationCode")]
    public string IdentificationCode { get; set; } = string.Empty;

    [JsonPropertyName("identificationQualifier")]
    public string IdentificationQualifier { get; set; } = string.Empty;
}

/// <summary>Billing Provider (Loop 2010AA) information.</summary>
public class BillingProvider
{
    [JsonPropertyName("npi")]
    public string Npi { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("entityType")]
    public string EntityType { get; set; } = string.Empty; // "1" | "2"

    [JsonPropertyName("taxId")]
    public string? TaxId { get; set; }

    [JsonPropertyName("taxIdQualifier")]
    public string? TaxIdQualifier { get; set; } // "EI" | "SY"

    [JsonPropertyName("address")]
    public ProviderAddress Address { get; set; } = new();

    [JsonPropertyName("taxonomyCode")]
    public string? TaxonomyCode { get; set; }

    [JsonPropertyName("payToProvider")]
    public PayToProvider? PayToProvider { get; set; }
}

public class PayToProvider
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("npi")]
    public string? Npi { get; set; }

    [JsonPropertyName("address")]
    public ProviderAddress? Address { get; set; }
}

/// <summary>Provider address structure.</summary>
public class ProviderAddress
{
    [JsonPropertyName("line1")]
    public string Line1 { get; set; } = string.Empty;

    [JsonPropertyName("line2")]
    public string? Line2 { get; set; }

    [JsonPropertyName("city")]
    public string City { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("postalCode")]
    public string PostalCode { get; set; } = string.Empty;

    [JsonPropertyName("countryCode")]
    public string? CountryCode { get; set; }
}

/// <summary>Subscriber (Loop 2010BA) information.</summary>
public class ClaimSubscriber
{
    [JsonPropertyName("memberId")]
    public string MemberId { get; set; } = string.Empty;

    [JsonPropertyName("firstName")]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("lastName")]
    public string LastName { get; set; } = string.Empty;

    [JsonPropertyName("middleName")]
    public string? MiddleName { get; set; }

    [JsonPropertyName("suffix")]
    public string? Suffix { get; set; }

    [JsonPropertyName("dateOfBirth")]
    public string DateOfBirth { get; set; } = string.Empty;

    [JsonPropertyName("gender")]
    public string? Gender { get; set; } // "M" | "F" | "U"

    [JsonPropertyName("groupNumber")]
    public string? GroupNumber { get; set; }

    [JsonPropertyName("address")]
    public SubscriberAddress? Address { get; set; }

    [JsonPropertyName("payerMemberId")]
    public string? PayerMemberId { get; set; }
}

public class SubscriberAddress
{
    [JsonPropertyName("line1")]
    public string? Line1 { get; set; }

    [JsonPropertyName("line2")]
    public string? Line2 { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("postalCode")]
    public string? PostalCode { get; set; }
}

/// <summary>Patient (Loop 2010CA) information when different from subscriber.</summary>
public class ClaimPatient
{
    [JsonPropertyName("firstName")]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("lastName")]
    public string LastName { get; set; } = string.Empty;

    [JsonPropertyName("middleName")]
    public string? MiddleName { get; set; }

    [JsonPropertyName("dateOfBirth")]
    public string DateOfBirth { get; set; } = string.Empty;

    [JsonPropertyName("gender")]
    public string? Gender { get; set; } // "M" | "F" | "U"

    [JsonPropertyName("relationshipCode")]
    public string RelationshipCode { get; set; } = string.Empty;

    [JsonPropertyName("address")]
    public SubscriberAddress? Address { get; set; }
}

/// <summary>Claim header (Loop 2300) information.</summary>
public class ClaimHeader
{
    [JsonPropertyName("patientControlNumber")]
    public string PatientControlNumber { get; set; } = string.Empty;

    [JsonPropertyName("totalChargeAmount")]
    public decimal TotalChargeAmount { get; set; }

    [JsonPropertyName("placeOfServiceCode")]
    public string? PlaceOfServiceCode { get; set; }

    [JsonPropertyName("facilityTypeCode")]
    public string? FacilityTypeCode { get; set; }

    [JsonPropertyName("frequencyCode")]
    public string? FrequencyCode { get; set; }

    [JsonPropertyName("signatureOnFile")]
    public bool? SignatureOnFile { get; set; }

    [JsonPropertyName("assignmentOfBenefits")]
    public bool? AssignmentOfBenefits { get; set; }

    [JsonPropertyName("releaseOfInformation")]
    public string? ReleaseOfInformation { get; set; }

    [JsonPropertyName("principalDiagnosisCode")]
    public string? PrincipalDiagnosisCode { get; set; }

    [JsonPropertyName("admittingDiagnosisCode")]
    public string? AdmittingDiagnosisCode { get; set; }

    [JsonPropertyName("diagnosisCodes")]
    public List<DiagnosisCode>? DiagnosisCodes { get; set; }

    [JsonPropertyName("admissionDate")]
    public string? AdmissionDate { get; set; }

    [JsonPropertyName("dischargeDate")]
    public string? DischargeDate { get; set; }

    [JsonPropertyName("admissionTypeCode")]
    public string? AdmissionTypeCode { get; set; }

    [JsonPropertyName("admissionSourceCode")]
    public string? AdmissionSourceCode { get; set; }

    [JsonPropertyName("patientStatusCode")]
    public string? PatientStatusCode { get; set; }

    [JsonPropertyName("drgCode")]
    public string? DrgCode { get; set; }

    [JsonPropertyName("priorAuthorizationNumber")]
    public string? PriorAuthorizationNumber { get; set; }

    [JsonPropertyName("referralNumber")]
    public string? ReferralNumber { get; set; }

    [JsonPropertyName("accidentInfo")]
    public AccidentInfo? AccidentInfo { get; set; }

    [JsonPropertyName("referringProvider")]
    public ReferringProvider? ReferringProvider { get; set; }

    [JsonPropertyName("renderingProvider")]
    public RenderingProvider? RenderingProvider { get; set; }

    [JsonPropertyName("serviceFacilityLocation")]
    public ServiceFacilityLocation? ServiceFacilityLocation { get; set; }
}

public class DiagnosisCode
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("qualifier")]
    public string Qualifier { get; set; } = string.Empty; // "ABK" | "BK" | "ABF"

    [JsonPropertyName("pointer")]
    public int? Pointer { get; set; }

    [JsonPropertyName("presentOnAdmission")]
    public string? PresentOnAdmission { get; set; } // "Y" | "N" | "U" | "W"
}

public class AccidentInfo
{
    [JsonPropertyName("type")]
    public string? Type { get; set; } // "auto" | "employment" | "other"

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }
}

public class ReferringProvider
{
    [JsonPropertyName("npi")]
    public string Npi { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class RenderingProvider
{
    [JsonPropertyName("npi")]
    public string Npi { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("taxonomyCode")]
    public string? TaxonomyCode { get; set; }
}

public class ServiceFacilityLocation
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("npi")]
    public string? Npi { get; set; }

    [JsonPropertyName("address")]
    public ProviderAddress Address { get; set; } = new();
}

/// <summary>Service line (Loop 2400) information.</summary>
public class ServiceLine
{
    [JsonPropertyName("lineNumber")]
    public int LineNumber { get; set; }

    [JsonPropertyName("procedureCode")]
    public string ProcedureCode { get; set; } = string.Empty;

    [JsonPropertyName("procedureCodeQualifier")]
    public string? ProcedureCodeQualifier { get; set; }

    [JsonPropertyName("modifiers")]
    public List<string>? Modifiers { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("serviceDate")]
    public string ServiceDate { get; set; } = string.Empty;

    [JsonPropertyName("serviceDateEnd")]
    public string? ServiceDateEnd { get; set; }

    [JsonPropertyName("chargeAmount")]
    public decimal ChargeAmount { get; set; }

    [JsonPropertyName("units")]
    public decimal Units { get; set; }

    [JsonPropertyName("unitType")]
    public string? UnitType { get; set; }

    [JsonPropertyName("placeOfService")]
    public string? PlaceOfService { get; set; }

    [JsonPropertyName("revenueCode")]
    public string? RevenueCode { get; set; }

    [JsonPropertyName("diagnosisPointers")]
    public List<int>? DiagnosisPointers { get; set; }

    [JsonPropertyName("renderingProvider")]
    public ServiceLineProvider? RenderingProvider { get; set; }

    [JsonPropertyName("ndcCode")]
    public string? NdcCode { get; set; }

    [JsonPropertyName("ndcQuantity")]
    public NdcInfo? NdcQuantity { get; set; }

    [JsonPropertyName("priorAuthorizationNumber")]
    public string? PriorAuthorizationNumber { get; set; }

    [JsonPropertyName("emergencyIndicator")]
    public bool? EmergencyIndicator { get; set; }

    [JsonPropertyName("epsdtIndicator")]
    public bool? EpsdtIndicator { get; set; }

    [JsonPropertyName("familyPlanningIndicator")]
    public bool? FamilyPlanningIndicator { get; set; }

    [JsonPropertyName("toothInfo")]
    public ToothInfo? ToothInfo { get; set; }
}

public class ServiceLineProvider
{
    [JsonPropertyName("npi")]
    public string Npi { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class NdcInfo
{
    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("unitOfMeasure")]
    public string UnitOfMeasure { get; set; } = string.Empty;
}

public class ToothInfo
{
    [JsonPropertyName("toothNumber")]
    public string? ToothNumber { get; set; }

    [JsonPropertyName("toothSurfaces")]
    public List<string>? ToothSurfaces { get; set; }

    [JsonPropertyName("oralCavityDesignation")]
    public string? OralCavityDesignation { get; set; }
}
