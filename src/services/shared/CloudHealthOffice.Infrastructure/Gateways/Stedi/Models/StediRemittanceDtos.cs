using System.Text.Json.Serialization;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;

/// <summary>
/// Stedi 835 ERA Report JSON (CHC-compatible).
/// GET /2024-04-01/change/medicalnetwork/reports/v2/{transactionId}/835
/// </summary>
internal sealed class Stedi835ReportDto
{
    [JsonPropertyName("meta")]
    public Stedi277MetaDto? Meta { get; set; }

    [JsonPropertyName("transactions")]
    public List<Stedi835TransactionDto>? Transactions { get; set; }
}

internal sealed class Stedi835TransactionDto
{
    [JsonPropertyName("controlNumber")]
    public string? ControlNumber { get; set; }

    [JsonPropertyName("payer")]
    public Stedi835PartyDto? Payer { get; set; }

    [JsonPropertyName("payee")]
    public Stedi835PartyDto? Payee { get; set; }

    [JsonPropertyName("paymentAndRemitReassociationDetails")]
    public Stedi835TraceDto? PaymentAndRemitReassociationDetails { get; set; }

    [JsonPropertyName("financialInformation")]
    public Stedi835FinancialDto? FinancialInformation { get; set; }

    [JsonPropertyName("detailInfo")]
    public List<Stedi835DetailDto>? DetailInfo { get; set; }
}

internal sealed class Stedi835PartyDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("organizationName")]
    public string? OrganizationName { get; set; }

    [JsonPropertyName("payerId")]
    public string? PayerId { get; set; }

    [JsonPropertyName("npi")]
    public string? Npi { get; set; }
}

internal sealed class Stedi835TraceDto
{
    [JsonPropertyName("traceTypeCode")]
    public string? TraceTypeCode { get; set; }

    [JsonPropertyName("checkOrEFTTraceNumber")]
    public string? CheckOrEFTTraceNumber { get; set; }

    [JsonPropertyName("traceNumber")]
    public string? TraceNumber { get; set; }
}

internal sealed class Stedi835FinancialDto
{
    [JsonPropertyName("transactionHandlingCode")]
    public string? TransactionHandlingCode { get; set; }

    [JsonPropertyName("totalActualProviderPaymentAmount")]
    public string? TotalActualProviderPaymentAmount { get; set; }

    [JsonPropertyName("creditOrDebitFlagCode")]
    public string? CreditOrDebitFlagCode { get; set; }

    [JsonPropertyName("paymentMethodCode")]
    public string? PaymentMethodCode { get; set; }

    [JsonPropertyName("checkIssueOrEFTEffectiveDate")]
    public string? CheckIssueOrEFTEffectiveDate { get; set; }
}

internal sealed class Stedi835DetailDto
{
    [JsonPropertyName("assignedNumber")]
    public string? AssignedNumber { get; set; }

    [JsonPropertyName("paymentInfo")]
    public List<Stedi835PaymentInfoDto>? PaymentInfo { get; set; }
}

internal sealed class Stedi835PaymentInfoDto
{
    [JsonPropertyName("claimPaymentInfo")]
    public Stedi835ClaimPaymentInfoDto? ClaimPaymentInfo { get; set; }

    [JsonPropertyName("claimAdjustments")]
    public List<Stedi835AdjustmentDto>? ClaimAdjustments { get; set; }

    [JsonPropertyName("serviceLines")]
    public List<Stedi835ServiceLineDto>? ServiceLines { get; set; }
}

internal sealed class Stedi835ClaimPaymentInfoDto
{
    [JsonPropertyName("patientControlNumber")]
    public string? PatientControlNumber { get; set; }

    [JsonPropertyName("claimStatusCode")]
    public string? ClaimStatusCode { get; set; }

    [JsonPropertyName("totalClaimChargeAmount")]
    public string? TotalClaimChargeAmount { get; set; }

    [JsonPropertyName("claimPaymentAmount")]
    public string? ClaimPaymentAmount { get; set; }

    [JsonPropertyName("patientResponsibilityAmount")]
    public string? PatientResponsibilityAmount { get; set; }

    [JsonPropertyName("payerClaimControlNumber")]
    public string? PayerClaimControlNumber { get; set; }

    [JsonPropertyName("claimFilingIndicatorCode")]
    public string? ClaimFilingIndicatorCode { get; set; }
}

internal sealed class Stedi835AdjustmentDto
{
    [JsonPropertyName("claimAdjustmentGroupCode")]
    public string? ClaimAdjustmentGroupCode { get; set; }

    [JsonPropertyName("adjustmentGroupCode")]
    public string? AdjustmentGroupCode { get; set; }

    [JsonPropertyName("adjustmentReasonCode1")]
    public string? AdjustmentReasonCode1 { get; set; }

    [JsonPropertyName("adjustmentAmount1")]
    public string? AdjustmentAmount1 { get; set; }

    [JsonPropertyName("adjustmentReasonCode2")]
    public string? AdjustmentReasonCode2 { get; set; }

    [JsonPropertyName("adjustmentAmount2")]
    public string? AdjustmentAmount2 { get; set; }

    [JsonPropertyName("adjustmentReasonCode3")]
    public string? AdjustmentReasonCode3 { get; set; }

    [JsonPropertyName("adjustmentAmount3")]
    public string? AdjustmentAmount3 { get; set; }

    [JsonPropertyName("adjustmentReasonCode4")]
    public string? AdjustmentReasonCode4 { get; set; }

    [JsonPropertyName("adjustmentAmount4")]
    public string? AdjustmentAmount4 { get; set; }

    [JsonPropertyName("adjustmentReasonCode5")]
    public string? AdjustmentReasonCode5 { get; set; }

    [JsonPropertyName("adjustmentAmount5")]
    public string? AdjustmentAmount5 { get; set; }

    [JsonPropertyName("adjustmentReasonCode6")]
    public string? AdjustmentReasonCode6 { get; set; }

    [JsonPropertyName("adjustmentAmount6")]
    public string? AdjustmentAmount6 { get; set; }
}

internal sealed class Stedi835ServiceLineDto
{
    [JsonPropertyName("serviceIdQualifier")]
    public string? ServiceIdQualifier { get; set; }

    [JsonPropertyName("adjudicatedProcedureCode")]
    public string? AdjudicatedProcedureCode { get; set; }

    [JsonPropertyName("procedureCode")]
    public string? ProcedureCode { get; set; }

    [JsonPropertyName("lineItemControlNumber")]
    public string? LineItemControlNumber { get; set; }

    [JsonPropertyName("lineItemChargeAmount")]
    public string? LineItemChargeAmount { get; set; }

    [JsonPropertyName("lineItemProviderPaymentAmount")]
    public string? LineItemProviderPaymentAmount { get; set; }

    [JsonPropertyName("toothCode")]
    public string? ToothCode { get; set; }

    [JsonPropertyName("oralCavityDesignation")]
    public string? OralCavityDesignation { get; set; }

    [JsonPropertyName("serviceAdjustments")]
    public List<Stedi835AdjustmentDto>? ServiceAdjustments { get; set; }
}
