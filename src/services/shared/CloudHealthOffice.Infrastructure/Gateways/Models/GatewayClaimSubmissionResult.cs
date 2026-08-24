namespace CloudHealthOffice.Infrastructure.Gateways.Models;

/// <summary>
/// Vendor-neutral result of an outbound 837 submission. This describes
/// <b>gateway transmission</b> only — not 277CA payer acknowledgment,
/// adjudication, or payment.
/// </summary>
public sealed class GatewayClaimSubmissionResult
{
    public string ClaimId { get; set; } = string.Empty;

    public int ClaimVersion { get; set; }

    public GatewayClaimType ClaimType { get; set; }

    public GatewayClaimTransmissionStatus TransmissionStatus { get; set; } =
        GatewayClaimTransmissionStatus.Failed;

    /// <summary>Cloud Health Office transmission record id.</summary>
    public string TransmissionId { get; set; } = string.Empty;

    /// <summary>Clearinghouse / Stedi claim/correlation identifier, when returned.</summary>
    public string? SubmissionId { get; set; }

    public string? ExternalTransactionId { get; set; }

    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// True when the clearinghouse accepted the submission for processing.
    /// This is not payer acceptance (277CA), adjudication, or payment.
    /// </summary>
    public bool AcceptedForProcessing { get; set; }

    public bool ReplayOfExistingTransmission { get; set; }

    public List<string> Warnings { get; set; } = new();

    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Outbound transmission lifecycle. Distinct from claim adjudication status
/// and from 277CA acknowledgment / 835 payment.
/// </summary>
public enum GatewayClaimTransmissionStatus
{
    ReadyForSubmission,
    Queued,
    Transmitting,
    Transmitted,
    SubmissionAcceptedByGateway,
    SubmissionRejectedByGateway,
    Failed,
    AwaitingAcknowledgment,
    AcknowledgmentAccepted,
    AcknowledgmentRejected,
    AcknowledgmentPartial,
    AcknowledgmentFailed
}

/// <summary>
/// Transmission statuses that already represent a completed outbound submit.
/// A later 277CA must not reopen the same idempotency key for another 837.
/// Intentional resubmit uses a new claim version / frequency.
/// </summary>
public static class GatewayClaimTransmissionStatuses
{
    public static bool PreventsDuplicateSubmit(GatewayClaimTransmissionStatus status) =>
        status is GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway
            or GatewayClaimTransmissionStatus.Transmitted
            or GatewayClaimTransmissionStatus.AwaitingAcknowledgment
            or GatewayClaimTransmissionStatus.AcknowledgmentAccepted
            or GatewayClaimTransmissionStatus.AcknowledgmentRejected
            or GatewayClaimTransmissionStatus.AcknowledgmentPartial
            or GatewayClaimTransmissionStatus.AcknowledgmentFailed;
}
