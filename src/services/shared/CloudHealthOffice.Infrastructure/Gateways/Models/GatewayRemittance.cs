namespace CloudHealthOffice.Infrastructure.Gateways.Models;

/// <summary>
/// Vendor-neutral 835 remittance. Financial outcome of a payer's processing —
/// not a 277CA, not a 276/277 status inquiry, and not a posted payment.
/// </summary>
public sealed class GatewayRemittance
{
    public string RemittanceId { get; set; } = string.Empty;

    public string Gateway { get; set; } = string.Empty;

    public string? ExternalTransactionId { get; set; }

    public string? EventId { get; set; }

    public string? CorrelationId { get; set; }

    public string? PayerName { get; set; }

    /// <summary>Payer identifier from the ERA when present. Not a tenant id.</summary>
    public string? PayerIdentifier { get; set; }

    public string? PayeeNpi { get; set; }

    /// <summary>Check/EFT trace number. Persist for posting; never log.</summary>
    public string? PaymentIdentifier { get; set; }

    public string? PaymentMethodCode { get; set; }

    public DateOnly? PaymentDate { get; set; }

    public decimal PaymentAmount { get; set; }

    public string? CreditDebitFlag { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>Optional explicit transmission id (development injection).</summary>
    public string? TransmissionId { get; set; }

    public string? RawSourceReference { get; set; }

    /// <summary>
    /// Retrieve/normalize failure when no usable remittance body exists.
    /// Processor persists this category instead of treating empty claims as
    /// <see cref="GatewayErrorCategory.MalformedResponse"/>.
    /// </summary>
    public GatewayErrorCategory ErrorCategory { get; set; }

    public string? ErrorMessage { get; set; }

    public List<RemittedClaim> Claims { get; set; } = new();
}

public sealed class RemittanceRetrievalRequest
{
    public string ExternalRemittanceId { get; set; } = string.Empty;

    public string? EventId { get; set; }

    public string? CorrelationId { get; set; }
}

public sealed class RemittedClaim
{
    public string? ClaimId { get; set; }

    public string? TransmissionId { get; set; }

    public string? PayerClaimControlNumber { get; set; }

    public string? PatientControlNumber { get; set; }

    /// <summary>X12 CLP02 claim status code from the ERA (1, 2, 3, 4, 22, …).</summary>
    public string? ClaimStatusCode { get; set; }

    public decimal ChargedAmount { get; set; }

    public decimal? AllowedAmount { get; set; }

    public decimal PaidAmount { get; set; }

    public decimal PatientResponsibilityAmount { get; set; }

    public List<RemittanceAdjustment> Adjustments { get; set; } = new();

    public List<RemittedServiceLine> ServiceLines { get; set; } = new();

    public RemittanceClaimMatchStatus MatchStatus { get; set; } = RemittanceClaimMatchStatus.Unmatched;

    public string? MatchReason { get; set; }
}

public sealed class RemittedServiceLine
{
    public string? LineIdentifier { get; set; }

    public int? LineNumber { get; set; }

    public string? ProcedureCode { get; set; }

    public string? ProcedureQualifier { get; set; }

    public string? ToothNumber { get; set; }

    public decimal ChargedAmount { get; set; }

    public decimal? AllowedAmount { get; set; }

    public decimal PaidAmount { get; set; }

    public List<RemittanceAdjustment> Adjustments { get; set; } = new();
}

public sealed class RemittanceAdjustment
{
    public string? GroupCode { get; set; }

    public string? ReasonCode { get; set; }

    public decimal Amount { get; set; }

    public string? Description { get; set; }

    public RemittanceAdjustmentKind Kind { get; set; } = RemittanceAdjustmentKind.Other;
}

public enum RemittanceAdjustmentKind
{
    Other = 0,
    Contractual,
    PatientResponsibility,
    NonCovered,
    Deductible,
    Coinsurance,
    Copay
}

public enum RemittanceClaimMatchStatus
{
    Unmatched = 0,
    Matched,
    Ambiguous
}

/// <summary>
/// Remittance receipt lifecycle. Distinct from claim adjudication and from
/// payment posting. <c>AvailableForPosting</c> means the ERA is stored and
/// matched — posting is a later PR.
/// </summary>
public enum RemittanceLifecycleStatus
{
    Received = 0,
    Validated,
    Matched,
    AvailableForPosting,
    Unmatched,
    Rejected,
    Failed
}
