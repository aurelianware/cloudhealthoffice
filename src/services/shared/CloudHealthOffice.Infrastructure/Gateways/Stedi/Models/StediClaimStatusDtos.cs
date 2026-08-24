using System.Text.Json.Serialization;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;

/// <summary>
/// Stedi Real-Time Claim Status (276/277) JSON request.
/// POST /2024-04-01/change/medicalnetwork/claimstatus/v2
/// </summary>
internal sealed class StediClaimStatusRequestDto
{
    [JsonPropertyName("tradingPartnerServiceId")]
    public string? TradingPartnerServiceId { get; set; }

    [JsonPropertyName("providers")]
    public List<StediClaimStatusProviderDto>? Providers { get; set; }

    [JsonPropertyName("subscriber")]
    public StediClaimStatusSubscriberDto? Subscriber { get; set; }

    [JsonPropertyName("dependent")]
    public StediClaimStatusDependentDto? Dependent { get; set; }

    [JsonPropertyName("encounter")]
    public StediClaimStatusEncounterDto? Encounter { get; set; }

    [JsonPropertyName("serviceLinesInformation")]
    public List<StediClaimStatusServiceLineDto>? ServiceLinesInformation { get; set; }
}

internal sealed class StediClaimStatusProviderDto
{
    [JsonPropertyName("npi")]
    public string? Npi { get; set; }

    [JsonPropertyName("organizationName")]
    public string? OrganizationName { get; set; }

    [JsonPropertyName("firstName")]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastName")]
    public string? LastName { get; set; }

    [JsonPropertyName("etin")]
    public string? Etin { get; set; }

    [JsonPropertyName("taxId")]
    public string? TaxId { get; set; }

    [JsonPropertyName("providerType")]
    public string ProviderType { get; set; } = "BillingProvider";
}

internal sealed class StediClaimStatusSubscriberDto
{
    [JsonPropertyName("firstName")]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastName")]
    public string? LastName { get; set; }

    [JsonPropertyName("dateOfBirth")]
    public string? DateOfBirth { get; set; }

    [JsonPropertyName("gender")]
    public string? Gender { get; set; }

    [JsonPropertyName("memberId")]
    public string? MemberId { get; set; }

    [JsonPropertyName("groupNumber")]
    public string? GroupNumber { get; set; }
}

internal sealed class StediClaimStatusDependentDto
{
    [JsonPropertyName("firstName")]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastName")]
    public string? LastName { get; set; }

    [JsonPropertyName("dateOfBirth")]
    public string? DateOfBirth { get; set; }

    [JsonPropertyName("gender")]
    public string? Gender { get; set; }
}

internal sealed class StediClaimStatusEncounterDto
{
    [JsonPropertyName("beginningDateOfService")]
    public string? BeginningDateOfService { get; set; }

    [JsonPropertyName("endDateOfService")]
    public string? EndDateOfService { get; set; }

    [JsonPropertyName("patientAccountNumber")]
    public string? PatientAccountNumber { get; set; }

    [JsonPropertyName("tradingPartnerClaimNumber")]
    public string? TradingPartnerClaimNumber { get; set; }

    [JsonPropertyName("submittedAmount")]
    public string? SubmittedAmount { get; set; }

    [JsonPropertyName("billingType")]
    public string? BillingType { get; set; }

    [JsonPropertyName("trackingNumber")]
    public string? TrackingNumber { get; set; }
}

internal sealed class StediClaimStatusServiceLineDto
{
    [JsonPropertyName("lineItemChargeAmount")]
    public string? LineItemChargeAmount { get; set; }

    [JsonPropertyName("lineItemControlNumber")]
    public string? LineItemControlNumber { get; set; }

    [JsonPropertyName("procedureCode")]
    public string? ProcedureCode { get; set; }

    [JsonPropertyName("procedureModifiers")]
    public List<string>? ProcedureModifiers { get; set; }

    [JsonPropertyName("productOrServiceIDQualifier")]
    public string? ProductOrServiceIDQualifier { get; set; }

    [JsonPropertyName("revenueCode")]
    public string? RevenueCode { get; set; }

    [JsonPropertyName("serviceLineDate")]
    public string? ServiceLineDate { get; set; }

    [JsonPropertyName("serviceLineEndDate")]
    public string? ServiceLineEndDate { get; set; }

    [JsonPropertyName("unitsOfServiceCount")]
    public string? UnitsOfServiceCount { get; set; }
}

internal sealed class StediClaimStatusResponseDto
{
    [JsonPropertyName("controlNumber")]
    public string? ControlNumber { get; set; }

    [JsonPropertyName("tradingPartnerServiceId")]
    public string? TradingPartnerServiceId { get; set; }

    [JsonPropertyName("claims")]
    public List<StediClaimStatusClaimDto>? Claims { get; set; }

    [JsonPropertyName("errors")]
    public List<StediClaimStatusErrorDto>? Errors { get; set; }

    [JsonPropertyName("meta")]
    public StediClaimStatusMetaDto? Meta { get; set; }
}

internal sealed class StediClaimStatusClaimDto
{
    [JsonPropertyName("claimStatus")]
    public StediClaimStatusDetailDto? ClaimStatus { get; set; }

    [JsonPropertyName("serviceDetails")]
    public List<StediClaimStatusServiceDetailDto>? ServiceDetails { get; set; }
}

internal sealed class StediClaimStatusDetailDto
{
    [JsonPropertyName("statusCategoryCode")]
    public string? StatusCategoryCode { get; set; }

    [JsonPropertyName("statusCategoryCodeValue")]
    public string? StatusCategoryCodeValue { get; set; }

    [JsonPropertyName("statusCode")]
    public string? StatusCode { get; set; }

    [JsonPropertyName("statusCodeValue")]
    public string? StatusCodeValue { get; set; }

    [JsonPropertyName("effectiveDate")]
    public string? EffectiveDate { get; set; }

    [JsonPropertyName("submittedAmount")]
    public string? SubmittedAmount { get; set; }

    [JsonPropertyName("amountPaid")]
    public string? AmountPaid { get; set; }

    [JsonPropertyName("tradingPartnerClaimNumber")]
    public string? TradingPartnerClaimNumber { get; set; }

    [JsonPropertyName("patientAccountNumber")]
    public string? PatientAccountNumber { get; set; }

    [JsonPropertyName("trackingNumber")]
    public string? TrackingNumber { get; set; }

    [JsonPropertyName("claimServiceDate")]
    public string? ClaimServiceDate { get; set; }

    [JsonPropertyName("statusInformationEffectiveDate")]
    public string? StatusInformationEffectiveDate { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal sealed class StediClaimStatusServiceDetailDto
{
    [JsonPropertyName("serviceIdQualifier")]
    public string? ServiceIdQualifier { get; set; }

    [JsonPropertyName("procedureId")]
    public string? ProcedureId { get; set; }

    [JsonPropertyName("procedureCode")]
    public string? ProcedureCode { get; set; }

    [JsonPropertyName("procedureModifiers")]
    public List<string>? ProcedureModifiers { get; set; }

    [JsonPropertyName("submittedAmount")]
    public string? SubmittedAmount { get; set; }

    [JsonPropertyName("amountPaid")]
    public string? AmountPaid { get; set; }

    [JsonPropertyName("lineItemControlNumber")]
    public string? LineItemControlNumber { get; set; }

    [JsonPropertyName("status")]
    public List<StediClaimStatusDetailDto>? Status { get; set; }
}

internal sealed class StediClaimStatusErrorDto
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("field")]
    public string? Field { get; set; }
}

internal sealed class StediClaimStatusMetaDto
{
    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("transactionId")]
    public string? TransactionId { get; set; }
}
