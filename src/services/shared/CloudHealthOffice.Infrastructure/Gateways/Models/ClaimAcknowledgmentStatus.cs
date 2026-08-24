namespace CloudHealthOffice.Infrastructure.Gateways.Models;

/// <summary>
/// 277CA acknowledgment outcome. Distinct from
/// <see cref="GatewayClaimTransmissionStatus"/> (outbound 837), claim
/// adjudication, and payment/835.
/// </summary>
public enum ClaimAcknowledgmentStatus
{
    Pending,
    Accepted,
    AcceptedWithWarnings,
    Rejected,
    Partial,
    UnableToMatch,
    Malformed
}

public enum ClaimAcknowledgmentLineStatus
{
    LineAccepted,
    LineRejected,
    LineWarning
}

/// <summary>
/// Vendor-neutral grouping of 277CA issue codes. Unmapped codes stay
/// <see cref="Other"/>; original category/status codes are preserved on the issue.
/// </summary>
public enum ClaimAcknowledgmentErrorCategory
{
    Other,
    InvalidMember,
    InvalidProvider,
    InvalidPayer,
    InvalidClaimData,
    MissingRequiredField,
    InvalidDiagnosis,
    InvalidProcedure,
    DuplicateClaim,
    InvalidSubscriber
}
