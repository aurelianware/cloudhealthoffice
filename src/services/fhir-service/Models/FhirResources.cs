using System.Text.Json.Serialization;

namespace FhirService.Models;

// ============================================================================
// CHO Internal Models (input to mappers)
// ============================================================================

/// <summary>
/// CHO backend member/patient record.
/// </summary>
public record ChoMember
{
    public required string MemberId { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public string? MiddleName { get; init; }
    public required string Dob { get; init; }
    public required string Gender { get; init; }
    public string? Ssn { get; init; }
    public ChoAddress? Address { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
}

public record ChoAddress
{
    public string? Street1 { get; init; }
    public string? Street2 { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? Zip { get; init; }
}

/// <summary>
/// CHO backend coverage record.
/// </summary>
public record ChoCoverage
{
    public required string MemberId { get; init; }
    public string Status { get; init; } = "active";
}

/// <summary>
/// CHO backend claim record.
/// </summary>
public record ChoClaim
{
    public required string ClaimId { get; init; }
    public required string MemberId { get; init; }
    public required string ProviderId { get; init; }
    public required string ClaimType { get; init; }
    public required string ServiceDate { get; init; }
    public IReadOnlyList<string> DiagnosisCodes { get; init; } = [];
    public IReadOnlyList<string> ProcedureCodes { get; init; } = [];
    public decimal TotalCharged { get; init; }
    public decimal TotalPaid { get; init; }
    public string Status { get; init; } = "active";
}

/// <summary>
/// CHO backend payment document (maps to ExplanationOfBenefit).
/// </summary>
public record ChoPaymentDocument
{
    public required string PaymentId { get; init; }
    public required string ClaimId { get; init; }
    public required string MemberId { get; init; }
    public required string PaymentDate { get; init; }
    public decimal TotalPaid { get; init; }
    public string? Status { get; init; }
}

// ============================================================================
// Lightweight FHIR R4 Records (System.Text.Json serializable)
// ============================================================================

/// <summary>
/// FHIR R4 coding element.
/// </summary>
public record FhirCoding
{
    [JsonPropertyName("system")]
    public string? System { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("display")]
    public string? Display { get; init; }
}

/// <summary>
/// FHIR R4 CodeableConcept.
/// </summary>
public record FhirCodeableConcept
{
    [JsonPropertyName("coding")]
    public IReadOnlyList<FhirCoding>? Coding { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>
/// FHIR R4 Reference.
/// </summary>
public record FhirReference
{
    [JsonPropertyName("reference")]
    public string? Reference { get; init; }

    [JsonPropertyName("display")]
    public string? Display { get; init; }

    [JsonPropertyName("identifier")]
    public FhirIdentifier? Identifier { get; init; }
}

/// <summary>
/// FHIR R4 Identifier.
/// </summary>
public record FhirIdentifier
{
    [JsonPropertyName("use")]
    public string? Use { get; init; }

    [JsonPropertyName("type")]
    public FhirCodeableConcept? Type { get; init; }

    [JsonPropertyName("system")]
    public string? System { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

/// <summary>
/// FHIR R4 HumanName.
/// </summary>
public record FhirHumanName
{
    [JsonPropertyName("use")]
    public string? Use { get; init; }

    [JsonPropertyName("family")]
    public string? Family { get; init; }

    [JsonPropertyName("given")]
    public IReadOnlyList<string>? Given { get; init; }

    [JsonPropertyName("prefix")]
    public IReadOnlyList<string>? Prefix { get; init; }

    [JsonPropertyName("suffix")]
    public IReadOnlyList<string>? Suffix { get; init; }
}

/// <summary>
/// FHIR R4 Address.
/// </summary>
public record FhirAddress
{
    [JsonPropertyName("use")]
    public string? Use { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("line")]
    public IReadOnlyList<string>? Line { get; init; }

    [JsonPropertyName("city")]
    public string? City { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("postalCode")]
    public string? PostalCode { get; init; }

    [JsonPropertyName("country")]
    public string? Country { get; init; }
}

/// <summary>
/// FHIR R4 ContactPoint (telecom).
/// </summary>
public record FhirContactPoint
{
    [JsonPropertyName("system")]
    public string? System { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("use")]
    public string? Use { get; init; }
}

/// <summary>
/// FHIR R4 Meta.
/// </summary>
public record FhirMeta
{
    [JsonPropertyName("profile")]
    public IReadOnlyList<string>? Profile { get; init; }
}

/// <summary>
/// FHIR R4 Money.
/// </summary>
public record FhirMoney
{
    [JsonPropertyName("value")]
    public decimal Value { get; init; }

    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "USD";
}

/// <summary>
/// FHIR R4 Extension element.
/// </summary>
public record FhirExtension
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("valueInteger")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ValueInteger { get; init; }

    [JsonPropertyName("valueString")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValueString { get; init; }

    [JsonPropertyName("valueBoolean")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ValueBoolean { get; init; }

    [JsonPropertyName("extension")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FhirExtension>? Extension { get; init; }
}

/// <summary>
/// FHIR R4 Period.
/// </summary>
public record FhirPeriod
{
    [JsonPropertyName("start")]
    public string? Start { get; init; }

    [JsonPropertyName("end")]
    public string? End { get; init; }
}

// ── FHIR Resource Records ──────────────────────────────────────────────────

/// <summary>
/// Base for all lightweight FHIR resource records.
/// </summary>
public abstract record FhirResource
{
    [JsonPropertyName("resourceType")]
    public abstract string ResourceType { get; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("meta")]
    public FhirMeta? Meta { get; init; }

    [JsonPropertyName("extension")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FhirExtension>? Extension { get; set; }
}

/// <summary>
/// FHIR R4 Patient resource (US Core profile).
/// </summary>
public record FhirPatient : FhirResource
{
    [JsonPropertyName("resourceType")]
    public override string ResourceType => "Patient";

    [JsonPropertyName("identifier")]
    public IReadOnlyList<FhirIdentifier>? Identifier { get; init; }

    [JsonPropertyName("active")]
    public bool? Active { get; init; }

    [JsonPropertyName("name")]
    public IReadOnlyList<FhirHumanName>? Name { get; init; }

    [JsonPropertyName("gender")]
    public string? Gender { get; init; }

    [JsonPropertyName("birthDate")]
    public string? BirthDate { get; init; }

    [JsonPropertyName("address")]
    public IReadOnlyList<FhirAddress>? Address { get; init; }

    [JsonPropertyName("telecom")]
    public IReadOnlyList<FhirContactPoint>? Telecom { get; init; }
}

/// <summary>
/// FHIR R4 Coverage resource (US Core profile).
/// </summary>
public record FhirCoverage : FhirResource
{
    [JsonPropertyName("resourceType")]
    public override string ResourceType => "Coverage";

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("type")]
    public FhirCodeableConcept? Type { get; init; }

    [JsonPropertyName("beneficiary")]
    public FhirReference? Beneficiary { get; init; }

    [JsonPropertyName("subscriberId")]
    public string? SubscriberId { get; init; }

    [JsonPropertyName("payor")]
    public IReadOnlyList<FhirReference>? Payor { get; init; }
}

/// <summary>
/// FHIR R4 ExplanationOfBenefit insurance component.
/// </summary>
public record FhirEobInsurance
{
    [JsonPropertyName("focal")]
    public bool Focal { get; init; }

    [JsonPropertyName("coverage")]
    public FhirReference? Coverage { get; init; }
}

/// <summary>
/// FHIR R4 EOB payment component.
/// </summary>
public record FhirEobPayment
{
    [JsonPropertyName("amount")]
    public FhirMoney? Amount { get; init; }
}

/// <summary>
/// FHIR R4 EOB supportingInfo component.
/// </summary>
public record FhirEobSupportingInfo
{
    [JsonPropertyName("sequence")]
    public int Sequence { get; init; }

    [JsonPropertyName("category")]
    public FhirCodeableConcept? Category { get; init; }

    [JsonPropertyName("valueString")]
    public string? ValueString { get; init; }
}

/// <summary>
/// FHIR R4 ExplanationOfBenefit resource.
/// </summary>
public record FhirExplanationOfBenefit : FhirResource
{
    [JsonPropertyName("resourceType")]
    public override string ResourceType => "ExplanationOfBenefit";

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("type")]
    public FhirCodeableConcept? Type { get; init; }

    [JsonPropertyName("use")]
    public string? Use { get; init; }

    [JsonPropertyName("patient")]
    public FhirReference? Patient { get; init; }

    [JsonPropertyName("created")]
    public string? Created { get; init; }

    [JsonPropertyName("insurer")]
    public FhirReference? Insurer { get; init; }

    [JsonPropertyName("provider")]
    public FhirReference? Provider { get; init; }

    [JsonPropertyName("outcome")]
    public string? Outcome { get; init; }

    [JsonPropertyName("insurance")]
    public IReadOnlyList<FhirEobInsurance>? Insurance { get; init; }

    [JsonPropertyName("payment")]
    public FhirEobPayment? Payment { get; init; }

    [JsonPropertyName("supportingInfo")]
    public IReadOnlyList<FhirEobSupportingInfo>? SupportingInfo { get; init; }
}

// ── Bundle Records ──────────────────────────────────────────────────────────

/// <summary>
/// FHIR R4 Bundle link component.
/// </summary>
public record FhirBundleLink
{
    [JsonPropertyName("relation")]
    public string? Relation { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

/// <summary>
/// FHIR R4 Bundle entry component.
/// </summary>
public record FhirBundleEntry
{
    [JsonPropertyName("fullUrl")]
    public string? FullUrl { get; init; }

    [JsonPropertyName("resource")]
    public FhirResource? Resource { get; init; }
}

/// <summary>
/// Lightweight FHIR R4 Claim resource for System.Text.Json serialization.
/// </summary>
public record FhirClaimResource : FhirResource
{
    [JsonPropertyName("resourceType")]
    public override string ResourceType => "Claim";

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("type")]
    public FhirCodeableConcept? ClaimType { get; init; }

    [JsonPropertyName("use")]
    public string? Use { get; init; }

    [JsonPropertyName("patient")]
    public FhirReference? Patient { get; init; }

    [JsonPropertyName("provider")]
    public FhirReference? Provider { get; init; }

    [JsonPropertyName("created")]
    public string? Created { get; init; }

    [JsonPropertyName("priority")]
    public FhirCodeableConcept? Priority { get; init; }

    [JsonPropertyName("insurance")]
    public IReadOnlyList<FhirEobInsurance>? Insurance { get; init; }
}

/// <summary>
/// FHIR R4 Bundle resource.
/// </summary>
public record FhirBundle
{
    [JsonPropertyName("resourceType")]
    public string ResourceType => "Bundle";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "searchset";

    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("link")]
    public IReadOnlyList<FhirBundleLink>? Link { get; init; }

    [JsonPropertyName("entry")]
    public IReadOnlyList<FhirBundleEntry>? Entry { get; init; }
}

// ── FHIR Bundle Search Component ─────────────────────────────────────────────

/// <summary>
/// FHIR R4 Bundle entry search component.
/// </summary>
public record FhirBundleSearch
{
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }
}

/// <summary>
/// FHIR R4 Bundle entry with search component.
/// </summary>
public record FhirBundleEntryWithSearch
{
    [JsonPropertyName("fullUrl")]
    public string? FullUrl { get; init; }

    [JsonPropertyName("resource")]
    public FhirResource? Resource { get; init; }

    [JsonPropertyName("search")]
    public FhirBundleSearch? Search { get; init; }
}

/// <summary>
/// FHIR R4 searchset Bundle with search mode support.
/// </summary>
public record FhirSearchBundle
{
    [JsonPropertyName("resourceType")]
    public string ResourceType => "Bundle";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "searchset";

    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("link")]
    public IReadOnlyList<FhirBundleLink>? Link { get; init; }

    [JsonPropertyName("entry")]
    public IReadOnlyList<FhirBundleEntryWithSearch>? Entry { get; init; }
}

// ============================================================================
// Provider Directory FHIR R4 Resources
// ============================================================================

/// <summary>
/// FHIR R4 Qualification component for Practitioner.
/// </summary>
public record FhirQualification
{
    [JsonPropertyName("identifier")]
    public IReadOnlyList<FhirIdentifier>? Identifier { get; init; }

    [JsonPropertyName("code")]
    public FhirCodeableConcept? Code { get; init; }

    [JsonPropertyName("period")]
    public FhirPeriod? Period { get; init; }

    [JsonPropertyName("issuer")]
    public FhirReference? Issuer { get; init; }
}

/// <summary>
/// FHIR R4 Practitioner resource (US Core profile).
/// </summary>
public record FhirPractitioner : FhirResource
{
    [JsonPropertyName("resourceType")]
    public override string ResourceType => "Practitioner";

    [JsonPropertyName("identifier")]
    public IReadOnlyList<FhirIdentifier>? Identifier { get; init; }

    [JsonPropertyName("active")]
    public bool? Active { get; set; }

    [JsonPropertyName("name")]
    public IReadOnlyList<FhirHumanName>? Name { get; init; }

    [JsonPropertyName("gender")]
    public string? Gender { get; init; }

    [JsonPropertyName("address")]
    public IReadOnlyList<FhirAddress>? Address { get; init; }

    [JsonPropertyName("telecom")]
    public IReadOnlyList<FhirContactPoint>? Telecom { get; init; }

    [JsonPropertyName("qualification")]
    public IReadOnlyList<FhirQualification>? Qualification { get; init; }
}

/// <summary>
/// FHIR R4 Organization resource (US Core profile).
/// </summary>
public record FhirOrganization : FhirResource
{
    [JsonPropertyName("resourceType")]
    public override string ResourceType => "Organization";

    [JsonPropertyName("identifier")]
    public IReadOnlyList<FhirIdentifier>? Identifier { get; init; }

    [JsonPropertyName("active")]
    public bool? Active { get; init; }

    [JsonPropertyName("type")]
    public IReadOnlyList<FhirCodeableConcept>? Type { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("alias")]
    public IReadOnlyList<string>? Alias { get; init; }

    [JsonPropertyName("address")]
    public IReadOnlyList<FhirAddress>? Address { get; init; }

    [JsonPropertyName("telecom")]
    public IReadOnlyList<FhirContactPoint>? Telecom { get; init; }
}

/// <summary>
/// FHIR R4 PractitionerRole resource (US Core profile).
/// </summary>
public record FhirPractitionerRole : FhirResource
{
    [JsonPropertyName("resourceType")]
    public override string ResourceType => "PractitionerRole";

    [JsonPropertyName("active")]
    public bool? Active { get; init; }

    [JsonPropertyName("practitioner")]
    public FhirReference? Practitioner { get; init; }

    [JsonPropertyName("organization")]
    public FhirReference? Organization { get; init; }

    [JsonPropertyName("code")]
    public IReadOnlyList<FhirCodeableConcept>? Code { get; init; }

    [JsonPropertyName("specialty")]
    public IReadOnlyList<FhirCodeableConcept>? Specialty { get; init; }

    [JsonPropertyName("location")]
    public IReadOnlyList<FhirReference>? Location { get; init; }

    [JsonPropertyName("telecom")]
    public IReadOnlyList<FhirContactPoint>? Telecom { get; init; }
}

/// <summary>
/// FHIR R4 Location resource (US Core profile).
/// </summary>
public record FhirLocation : FhirResource
{
    [JsonPropertyName("resourceType")]
    public override string ResourceType => "Location";

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("type")]
    public IReadOnlyList<FhirCodeableConcept>? Type { get; init; }

    [JsonPropertyName("telecom")]
    public IReadOnlyList<FhirContactPoint>? Telecom { get; init; }

    [JsonPropertyName("address")]
    public FhirAddress? Address { get; init; }

    [JsonPropertyName("managingOrganization")]
    public FhirReference? ManagingOrganization { get; init; }
}

// ============================================================================
// NPPES Integration Models (Provider Directory source data)
// ============================================================================

/// <summary>
/// NPPES API response wrapper.
/// </summary>
public record NppesResponse
{
    [JsonPropertyName("result_count")]
    public int ResultCount { get; init; }

    [JsonPropertyName("results")]
    public IReadOnlyList<NppesResult>? Results { get; init; }
}

/// <summary>
/// Individual NPPES provider result.
/// </summary>
public record NppesResult
{
    [JsonPropertyName("number")]
    public string Number { get; init; } = "";

    [JsonPropertyName("enumeration_type")]
    public string EnumerationType { get; init; } = "";

    [JsonPropertyName("basic")]
    public NppesBasicInfo Basic { get; init; } = new();

    [JsonPropertyName("addresses")]
    public IReadOnlyList<NppesAddress> Addresses { get; init; } = [];

    [JsonPropertyName("taxonomies")]
    public IReadOnlyList<NppesTaxonomy> Taxonomies { get; init; } = [];

    [JsonPropertyName("other_names")]
    public IReadOnlyList<NppesOtherName>? OtherNames { get; init; }
}

/// <summary>
/// NPPES basic provider information.
/// </summary>
public record NppesBasicInfo
{
    [JsonPropertyName("first_name")]
    public string? FirstName { get; init; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; init; }

    [JsonPropertyName("middle_name")]
    public string? MiddleName { get; init; }

    [JsonPropertyName("name_prefix")]
    public string? NamePrefix { get; init; }

    [JsonPropertyName("name_suffix")]
    public string? NameSuffix { get; init; }

    [JsonPropertyName("credential")]
    public string? Credential { get; init; }

    [JsonPropertyName("organization_name")]
    public string? OrganizationName { get; init; }

    [JsonPropertyName("gender")]
    public string? Gender { get; init; }

    [JsonPropertyName("enumeration_date")]
    public string? EnumerationDate { get; init; }

    [JsonPropertyName("last_updated")]
    public string? LastUpdated { get; init; }

    [JsonPropertyName("deactivation_date")]
    public string? DeactivationDate { get; init; }

    [JsonPropertyName("reactivation_date")]
    public string? ReactivationDate { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

/// <summary>
/// NPPES address information.
/// </summary>
public record NppesAddress
{
    [JsonPropertyName("address_purpose")]
    public string AddressPurpose { get; init; } = "";

    [JsonPropertyName("address_1")]
    public string Address1 { get; init; } = "";

    [JsonPropertyName("address_2")]
    public string? Address2 { get; init; }

    [JsonPropertyName("city")]
    public string City { get; init; } = "";

    [JsonPropertyName("state")]
    public string State { get; init; } = "";

    [JsonPropertyName("postal_code")]
    public string PostalCode { get; init; } = "";

    [JsonPropertyName("country_code")]
    public string CountryCode { get; init; } = "";

    [JsonPropertyName("telephone_number")]
    public string? TelephoneNumber { get; init; }

    [JsonPropertyName("fax_number")]
    public string? FaxNumber { get; init; }
}

/// <summary>
/// NPPES taxonomy (specialty) information.
/// </summary>
public record NppesTaxonomy
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = "";

    [JsonPropertyName("desc")]
    public string Desc { get; init; } = "";

    [JsonPropertyName("primary")]
    public bool Primary { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }
}

/// <summary>
/// NPPES other name entry.
/// </summary>
public record NppesOtherName
{
    [JsonPropertyName("organization_name")]
    public string? OrganizationName { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}
