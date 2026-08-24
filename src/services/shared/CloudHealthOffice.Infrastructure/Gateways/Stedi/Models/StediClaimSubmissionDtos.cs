using System.Text.Json.Serialization;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;

internal sealed class StediClaimSubmissionRequestDto
{
    [JsonPropertyName("usageIndicator")]
    public string? UsageIndicator { get; set; }

    [JsonPropertyName("tradingPartnerServiceId")]
    public string TradingPartnerServiceId { get; set; } = string.Empty;

    [JsonPropertyName("tradingPartnerName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TradingPartnerName { get; set; }

    [JsonPropertyName("submitter")]
    public StediClaimSubmitterDto Submitter { get; set; } = new();

    [JsonPropertyName("receiver")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StediClaimReceiverDto? Receiver { get; set; }

    [JsonPropertyName("billing")]
    public StediClaimBillingDto Billing { get; set; } = new();

    [JsonPropertyName("subscriber")]
    public StediClaimSubscriberDto Subscriber { get; set; } = new();

    [JsonPropertyName("dependent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StediClaimDependentDto? Dependent { get; set; }

    [JsonPropertyName("rendering")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StediClaimRenderingDto? Rendering { get; set; }

    [JsonPropertyName("claimInformation")]
    public StediClaimInformationDto ClaimInformation { get; set; } = new();
}

internal sealed class StediClaimSubmitterDto
{
    [JsonPropertyName("organizationName")]
    public string? OrganizationName { get; set; }

    [JsonPropertyName("submitterIdentification")]
    public string? SubmitterIdentification { get; set; }

    [JsonPropertyName("contactInformation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StediClaimContactDto? ContactInformation { get; set; }
}

internal sealed class StediClaimReceiverDto
{
    [JsonPropertyName("organizationName")]
    public string? OrganizationName { get; set; }
}

internal sealed class StediClaimContactDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }
}

internal sealed class StediClaimBillingDto
{
    [JsonPropertyName("providerType")]
    public string ProviderType { get; set; } = "BillingProvider";

    [JsonPropertyName("npi")]
    public string? Npi { get; set; }

    [JsonPropertyName("employerId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EmployerId { get; set; }

    [JsonPropertyName("taxonomyCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaxonomyCode { get; set; }

    [JsonPropertyName("organizationName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OrganizationName { get; set; }

    [JsonPropertyName("address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StediClaimAddressDto? Address { get; set; }

    [JsonPropertyName("contactInformation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StediClaimContactDto? ContactInformation { get; set; }
}

internal sealed class StediClaimRenderingDto
{
    [JsonPropertyName("npi")]
    public string? Npi { get; set; }

    [JsonPropertyName("organizationName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OrganizationName { get; set; }

    [JsonPropertyName("lastName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastName { get; set; }

    [JsonPropertyName("firstName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FirstName { get; set; }
}

internal sealed class StediClaimAddressDto
{
    [JsonPropertyName("address1")]
    public string? Address1 { get; set; }

    [JsonPropertyName("address2")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Address2 { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("postalCode")]
    public string? PostalCode { get; set; }
}

internal sealed class StediClaimSubscriberDto
{
    [JsonPropertyName("memberId")]
    public string? MemberId { get; set; }

    [JsonPropertyName("paymentResponsibilityLevelCode")]
    public string PaymentResponsibilityLevelCode { get; set; } = "P";

    [JsonPropertyName("firstName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastName { get; set; }

    [JsonPropertyName("dateOfBirth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateOfBirth { get; set; }

    [JsonPropertyName("groupNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupNumber { get; set; }
}

internal sealed class StediClaimDependentDto
{
    [JsonPropertyName("firstName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastName { get; set; }

    [JsonPropertyName("dateOfBirth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateOfBirth { get; set; }

    [JsonPropertyName("relationshipToSubscriberCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RelationshipToSubscriberCode { get; set; }
}

internal sealed class StediClaimInformationDto
{
    [JsonPropertyName("patientControlNumber")]
    public string? PatientControlNumber { get; set; }

    [JsonPropertyName("claimChargeAmount")]
    public string? ClaimChargeAmount { get; set; }

    [JsonPropertyName("placeOfServiceCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlaceOfServiceCode { get; set; }

    [JsonPropertyName("claimFrequencyCode")]
    public string ClaimFrequencyCode { get; set; } = "1";

    [JsonPropertyName("signatureIndicator")]
    public string SignatureIndicator { get; set; } = "Y";

    [JsonPropertyName("planParticipationCode")]
    public string PlanParticipationCode { get; set; } = "A";

    [JsonPropertyName("benefitsAssignmentCertificationIndicator")]
    public string BenefitsAssignmentCertificationIndicator { get; set; } = "Y";

    [JsonPropertyName("releaseInformationCode")]
    public string ReleaseInformationCode { get; set; } = "Y";

    [JsonPropertyName("claimFilingCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClaimFilingCode { get; set; }

    [JsonPropertyName("healthCareCodeInformation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StediDiagnosisDto>? HealthCareCodeInformation { get; set; }

    [JsonPropertyName("principalDiagnosis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StediPrincipalDiagnosisDto? PrincipalDiagnosis { get; set; }

    [JsonPropertyName("claimSupplementalInformation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StediClaimSupplementalDto? ClaimSupplementalInformation { get; set; }

    [JsonPropertyName("claimDateInformation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StediClaimDateInformationDto? ClaimDateInformation { get; set; }

    [JsonPropertyName("serviceFacilityLocation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? ServiceFacilityLocation { get; set; }

    [JsonPropertyName("serviceLines")]
    public List<StediClaimServiceLineDto> ServiceLines { get; set; } = new();
}

internal sealed class StediDiagnosisDto
{
    [JsonPropertyName("diagnosisTypeCode")]
    public string DiagnosisTypeCode { get; set; } = "ABK";

    [JsonPropertyName("diagnosisCode")]
    public string DiagnosisCode { get; set; } = string.Empty;
}

internal sealed class StediPrincipalDiagnosisDto
{
    [JsonPropertyName("qualifierCode")]
    public string QualifierCode { get; set; } = "ABK";

    [JsonPropertyName("principalDiagnosisCode")]
    public string PrincipalDiagnosisCode { get; set; } = string.Empty;
}

internal sealed class StediClaimSupplementalDto
{
    [JsonPropertyName("priorAuthorizationNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PriorAuthorizationNumber { get; set; }

    [JsonPropertyName("referralNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReferralNumber { get; set; }
}

internal sealed class StediClaimDateInformationDto
{
    [JsonPropertyName("statementBeginDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatementBeginDate { get; set; }

    [JsonPropertyName("statementEndDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatementEndDate { get; set; }

    [JsonPropertyName("admissionDateAndHour")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdmissionDateAndHour { get; set; }

    [JsonPropertyName("dischargeHour")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DischargeHour { get; set; }
}

internal sealed class StediClaimServiceLineDto
{
    [JsonPropertyName("assignedNumber")]
    public string? AssignedNumber { get; set; }

    [JsonPropertyName("serviceDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceDate { get; set; }

    [JsonPropertyName("providerControlNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderControlNumber { get; set; }

    [JsonPropertyName("professionalService")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StediProfessionalServiceDto? ProfessionalService { get; set; }

    [JsonPropertyName("institutionalService")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StediInstitutionalServiceDto? InstitutionalService { get; set; }

    [JsonPropertyName("dentalService")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StediDentalServiceDto? DentalService { get; set; }
}

internal sealed class StediProfessionalServiceDto
{
    [JsonPropertyName("procedureIdentifier")]
    public string ProcedureIdentifier { get; set; } = "HC";

    [JsonPropertyName("procedureCode")]
    public string ProcedureCode { get; set; } = string.Empty;

    [JsonPropertyName("procedureModifiers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ProcedureModifiers { get; set; }

    [JsonPropertyName("lineItemChargeAmount")]
    public string? LineItemChargeAmount { get; set; }

    [JsonPropertyName("measurementUnit")]
    public string MeasurementUnit { get; set; } = "UN";

    [JsonPropertyName("serviceUnitCount")]
    public string? ServiceUnitCount { get; set; }

    [JsonPropertyName("compositeDiagnosisCodePointers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StediDiagnosisPointersDto? CompositeDiagnosisCodePointers { get; set; }
}

internal sealed class StediInstitutionalServiceDto
{
    [JsonPropertyName("procedureCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProcedureCode { get; set; }

    [JsonPropertyName("serviceLineRevenueCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceLineRevenueCode { get; set; }

    [JsonPropertyName("lineItemChargeAmount")]
    public string? LineItemChargeAmount { get; set; }

    [JsonPropertyName("measurementUnit")]
    public string MeasurementUnit { get; set; } = "UN";

    [JsonPropertyName("serviceUnitCount")]
    public string? ServiceUnitCount { get; set; }
}

internal sealed class StediDentalServiceDto
{
    [JsonPropertyName("procedureCode")]
    public string ProcedureCode { get; set; } = string.Empty;

    [JsonPropertyName("procedureModifiers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ProcedureModifiers { get; set; }

    [JsonPropertyName("lineItemChargeAmount")]
    public string? LineItemChargeAmount { get; set; }

    [JsonPropertyName("oralCavityDesignation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? OralCavityDesignation { get; set; }

    [JsonPropertyName("toothCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToothCode { get; set; }

    [JsonPropertyName("toothSurface")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToothSurface { get; set; }
}

internal sealed class StediDiagnosisPointersDto
{
    [JsonPropertyName("diagnosisCodePointers")]
    public List<string> DiagnosisCodePointers { get; set; } = new();
}

internal sealed class StediClaimSubmissionResponseDto
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("controlNumber")]
    public string? ControlNumber { get; set; }

    [JsonPropertyName("httpStatusCode")]
    public string? HttpStatusCode { get; set; }

    [JsonPropertyName("claimReference")]
    public StediClaimReferenceDto? ClaimReference { get; set; }

    [JsonPropertyName("meta")]
    public StediClaimMetaDto? Meta { get; set; }

    [JsonPropertyName("errors")]
    public List<StediClaimErrorDto>? Errors { get; set; }
}

internal sealed class StediClaimReferenceDto
{
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("patientControlNumber")]
    public string? PatientControlNumber { get; set; }

    [JsonPropertyName("rhClaimNumber")]
    public string? RhClaimNumber { get; set; }

    [JsonPropertyName("timeOfResponse")]
    public string? TimeOfResponse { get; set; }

    public string? SubmissionId =>
        !string.IsNullOrWhiteSpace(CorrelationId) ? CorrelationId : RhClaimNumber;
}

internal sealed class StediClaimMetaDto
{
    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }
}

internal sealed class StediClaimErrorDto
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
