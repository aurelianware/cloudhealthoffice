namespace CloudHealthOffice.Appeals.Contracts;

// ── Parallel enum definitions ───────────────────────────────────────────
// These mirror the enums in appeals-service/Models/Appeal.cs by name,
// underlying type, and numeric value. Drift tests verify parity.
//
// Why duplicate instead of share: introducing a cycle (Contracts ->
// appeals-service) or a reverse dependency (appeals-service owns the
// types, Contracts re-exports) would either fail to compile or force
// appeals-service to depend on Contracts for its own domain enums —
// an inversion the shared-project boundary doesn't justify. Two enum
// declarations are cheap; the drift test makes the parity structural.

public enum AppealType
{
    Reconsideration = 1,
    PeerReview = 2,
    ExternalReview = 3,
    Grievance = 4
}

public enum AppealLevel
{
    FirstLevel = 1,
    SecondLevel = 2,
    ExternalReview = 3
}

public enum AppealStatus
{
    Draft = 1,
    Submitted = 2,
    InReview = 3,
    PendingInfo = 4,
    Closed = 5
}

public enum AppealDecisionType
{
    Approved = 1,
    Denied = 2,
    PartialApproval = 3
}

public enum AttachmentStatus
{
    Pending = 1,
    Sent = 2,
    Acknowledged = 3,
    Rejected = 4,
    Error = 5
}

public enum LineOfBusiness
{
    Commercial = 1,
    Medicare = 2,
    Medicaid = 3,
    Marketplace = 4
}

public enum AppealClosureReasonCode
{
    Approved = 1,
    Denied = 2,
    PartialApproval = 3,
    Withdrawn = 4,
    Expired = 5,
    AdminError = 6,
    Other = 99
}

public enum AppealSource
{
    ProviderPortal = 1,
    Availity275 = 2,
    CsrTranscription = 3,
    InternalRetroReview = 4,
    ExternalReview = 5
}
