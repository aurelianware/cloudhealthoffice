namespace CloudHealthOffice.Infrastructure.Gateways.Models;

/// <summary>
/// Vendor-neutral 277CA claim acknowledgment. This is an acceptance or
/// rejection into downstream processing — not adjudication and not payment.
/// </summary>
public sealed class GatewayClaimAcknowledgment
{
    public string AcknowledgmentId { get; set; } = string.Empty;

    public string Gateway { get; set; } = string.Empty;

    /// <summary>Gateway transaction id of this acknowledgment (not the original 837).</summary>
    public string? ExternalTransactionId { get; set; }

    /// <summary>Clearinghouse / correlation id from the original 837 submission.</summary>
    public string? OriginalSubmissionId { get; set; }

    public string? ClaimId { get; set; }

    public GatewayClaimType? ClaimType { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public ClaimAcknowledgmentStatus Status { get; set; } = ClaimAcknowledgmentStatus.Pending;

    public string? PatientControlNumber { get; set; }

    /// <summary>Payer-assigned claim control number, when the 277CA includes one.</summary>
    public string? ClaimControlNumber { get; set; }

    public string? CorrelationId { get; set; }

    /// <summary>Webhook / discovery event id, used for duplicate delivery detection.</summary>
    public string? EventId { get; set; }

    /// <summary>
    /// Optional explicit transmission id (development injection). When set,
    /// matching uses this id instead of inbound identifiers.
    /// </summary>
    public string? TransmissionId { get; set; }

    /// <summary>Pointer to the source transaction (id only — not the raw payload).</summary>
    public string? RawSourceReference { get; set; }

    public List<GatewayClaimAcknowledgmentClaimResult> ClaimLevelResults { get; set; } = new();

    public List<GatewayClaimAcknowledgmentLineResult> ServiceLineResults { get; set; } = new();

    public List<GatewayClaimAcknowledgmentIssue> Errors { get; set; } = new();

    public List<GatewayClaimAcknowledgmentIssue> Warnings { get; set; } = new();
}

public sealed class GatewayClaimAcknowledgmentClaimResult
{
    public ClaimAcknowledgmentStatus Status { get; set; }

    public string? PatientControlNumber { get; set; }

    public string? ClaimControlNumber { get; set; }

    public string? OriginalSubmissionId { get; set; }

    public List<GatewayClaimAcknowledgmentIssue> Errors { get; set; } = new();

    public List<GatewayClaimAcknowledgmentIssue> Warnings { get; set; } = new();
}

public sealed class GatewayClaimAcknowledgmentLineResult
{
    public ClaimAcknowledgmentLineStatus Status { get; set; }

    /// <summary>Line item control number submitted on the 837 (CHO line number).</summary>
    public string? LineItemControlNumber { get; set; }

    public int? LineNumber { get; set; }

    public List<GatewayClaimAcknowledgmentIssue> Errors { get; set; } = new();
}

public sealed class GatewayClaimAcknowledgmentIssue
{
    public string? CategoryCode { get; set; }

    public string? StatusCode { get; set; }

    public string? Description { get; set; }

    public string? EntityCode { get; set; }

    public ClaimAcknowledgmentErrorCategory Category { get; set; } =
        ClaimAcknowledgmentErrorCategory.Other;
}

public sealed class ClaimAcknowledgmentRetrievalRequest
{
    public string ExternalAcknowledgmentId { get; set; } = string.Empty;

    public string? EventId { get; set; }

    public string? CorrelationId { get; set; }
}

/// <summary>
/// Pointer to a gateway-discovered claim response (webhook or poll item).
/// Does not carry the 277CA contents.
/// </summary>
public sealed class ClaimAcknowledgmentDiscovery
{
    public string GatewayName { get; set; } = string.Empty;

    public string ExternalAcknowledgmentId { get; set; } = string.Empty;

    public string? EventId { get; set; }

    public string? TransactionSetIdentifier { get; set; }

    public string? Direction { get; set; }

    public string? CorrelationId { get; set; }
}

/// <summary>Development-only synthetic 277CA injection body.</summary>
public sealed class GatewayClaimAcknowledgmentInjection
{
    public string? AcknowledgmentId { get; set; }

    public ClaimAcknowledgmentStatus Status { get; set; } = ClaimAcknowledgmentStatus.Accepted;

    public string? ClaimControlNumber { get; set; }

    public string? OriginalSubmissionId { get; set; }

    public List<GatewayClaimAcknowledgmentIssue>? Errors { get; set; }

    public List<GatewayClaimAcknowledgmentLineResult>? ServiceLineResults { get; set; }
}
