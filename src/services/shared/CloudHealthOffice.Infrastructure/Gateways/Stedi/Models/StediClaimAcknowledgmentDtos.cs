using System.Text.Json.Serialization;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;

internal sealed class Stedi277ReportDto
{
    [JsonPropertyName("meta")]
    public Stedi277MetaDto? Meta { get; set; }

    [JsonPropertyName("transactions")]
    public List<Stedi277TransactionDto>? Transactions { get; set; }
}

internal sealed class Stedi277MetaDto
{
    [JsonPropertyName("applicationMode")]
    public string? ApplicationMode { get; set; }

    [JsonPropertyName("senderId")]
    public string? SenderId { get; set; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("transactionId")]
    public string? TransactionId { get; set; }
}

internal sealed class Stedi277TransactionDto
{
    [JsonPropertyName("controlNumber")]
    public string? ControlNumber { get; set; }

    [JsonPropertyName("payers")]
    public List<Stedi277PayerDto>? Payers { get; set; }
}

internal sealed class Stedi277PayerDto
{
    [JsonPropertyName("organizationName")]
    public string? OrganizationName { get; set; }

    [JsonPropertyName("entityIdentifierCode")]
    public string? EntityIdentifierCode { get; set; }

    [JsonPropertyName("claimStatusTransactions")]
    public List<Stedi277ClaimStatusTransactionDto>? ClaimStatusTransactions { get; set; }
}

internal sealed class Stedi277ClaimStatusTransactionDto
{
    [JsonPropertyName("claimTransactionBatchNumber")]
    public string? ClaimTransactionBatchNumber { get; set; }

    [JsonPropertyName("providerClaimStatuses")]
    public List<Stedi277ProviderClaimStatusDto>? ProviderClaimStatuses { get; set; }

    [JsonPropertyName("claimStatusDetails")]
    public List<Stedi277ClaimStatusDetailDto>? ClaimStatusDetails { get; set; }
}

internal sealed class Stedi277ProviderClaimStatusDto
{
    [JsonPropertyName("providerStatuses")]
    public List<Stedi277StatusDto>? ProviderStatuses { get; set; }

    [JsonPropertyName("statusInformationEffectiveDate")]
    public string? StatusInformationEffectiveDate { get; set; }
}

internal sealed class Stedi277ClaimStatusDetailDto
{
    [JsonPropertyName("serviceProviderClaimStatuses")]
    public List<Stedi277ProviderClaimStatusDto>? ServiceProviderClaimStatuses { get; set; }

    [JsonPropertyName("patientClaimStatusDetails")]
    public List<Stedi277PatientClaimStatusDto>? PatientClaimStatusDetails { get; set; }
}

internal sealed class Stedi277PatientClaimStatusDto
{
    [JsonPropertyName("claims")]
    public List<Stedi277ClaimDto>? Claims { get; set; }
}

internal sealed class Stedi277ClaimDto
{
    [JsonPropertyName("claimStatus")]
    public Stedi277ClaimStatusDto? ClaimStatus { get; set; }

    [JsonPropertyName("serviceLines")]
    public List<Stedi277ServiceLineDto>? ServiceLines { get; set; }
}

internal sealed class Stedi277ClaimStatusDto
{
    [JsonPropertyName("referencedTransactionTraceNumber")]
    public string? ReferencedTransactionTraceNumber { get; set; }

    [JsonPropertyName("patientAccountNumber")]
    public string? PatientAccountNumber { get; set; }

    [JsonPropertyName("tradingPartnerClaimNumber")]
    public string? TradingPartnerClaimNumber { get; set; }

    [JsonPropertyName("clearinghouseTraceNumber")]
    public string? ClearinghouseTraceNumber { get; set; }

    [JsonPropertyName("informationClaimStatuses")]
    public List<Stedi277InformationClaimStatusDto>? InformationClaimStatuses { get; set; }
}

internal sealed class Stedi277InformationClaimStatusDto
{
    [JsonPropertyName("statusInformationActionCode")]
    public string? StatusInformationActionCode { get; set; }

    [JsonPropertyName("statusInformationEffectiveDate")]
    public string? StatusInformationEffectiveDate { get; set; }

    [JsonPropertyName("informationStatuses")]
    public List<Stedi277StatusDto>? InformationStatuses { get; set; }

    [JsonPropertyName("statusMessage")]
    public string? StatusMessage { get; set; }
}

internal sealed class Stedi277ServiceLineDto
{
    [JsonPropertyName("lineItemControlNumber")]
    public string? LineItemControlNumber { get; set; }

    [JsonPropertyName("serviceClaimStatuses")]
    public List<Stedi277ServiceClaimStatusDto>? ServiceClaimStatuses { get; set; }
}

internal sealed class Stedi277ServiceClaimStatusDto
{
    [JsonPropertyName("serviceStatuses")]
    public List<Stedi277StatusDto>? ServiceStatuses { get; set; }
}

internal sealed class Stedi277StatusDto
{
    [JsonPropertyName("healthCareClaimStatusCategoryCode")]
    public string? HealthCareClaimStatusCategoryCode { get; set; }

    [JsonPropertyName("healthCareClaimStatusCategoryCodeValue")]
    public string? HealthCareClaimStatusCategoryCodeValue { get; set; }

    [JsonPropertyName("statusCode")]
    public string? StatusCode { get; set; }

    [JsonPropertyName("statusCodeValue")]
    public string? StatusCodeValue { get; set; }

    [JsonPropertyName("entityIdentifierCode")]
    public string? EntityIdentifierCode { get; set; }

    [JsonPropertyName("entityIdentifierCodeValue")]
    public string? EntityIdentifierCodeValue { get; set; }
}

internal sealed class StediPollTransactionsDto
{
    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }

    [JsonPropertyName("items")]
    public List<StediPollTransactionItemDto>? Items { get; set; }
}

internal sealed class StediPollTransactionItemDto
{
    [JsonPropertyName("transactionId")]
    public string? TransactionId { get; set; }

    [JsonPropertyName("fileExecutionId")]
    public string? FileExecutionId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    [JsonPropertyName("processedAt")]
    public string? ProcessedAt { get; set; }

    [JsonPropertyName("x12")]
    public StediPollX12Dto? X12 { get; set; }
}

internal sealed class StediPollX12Dto
{
    [JsonPropertyName("metadata")]
    public StediPollX12MetadataDto? Metadata { get; set; }

    [JsonPropertyName("transactionSetIdentifier")]
    public string? TransactionSetIdentifier { get; set; }
}

internal sealed class StediPollX12MetadataDto
{
    [JsonPropertyName("transaction")]
    public StediPollX12TransactionDto? Transaction { get; set; }
}

internal sealed class StediPollX12TransactionDto
{
    [JsonPropertyName("transactionSetIdentifier")]
    public string? TransactionSetIdentifier { get; set; }

    [JsonPropertyName("controlNumber")]
    public string? ControlNumber { get; set; }
}
