using AppealsService.Models;

namespace AppealsService.Services;

/// <summary>
/// Pure transition validator. No side effects — callers assemble the
/// <see cref="AppealEvent"/> themselves and pass the (appeal, new-status,
/// event) triple to the repository, which enforces atomicity.
///
/// Allowed transitions:
///   (none)      -> Draft        (genesis, via CreateAsync)
///   Draft       -> Submitted    (submit)
///   Draft       -> Closed       (withdraw from draft)
///   Submitted   -> InReview     (begin-review)
///   Submitted   -> Closed       (withdraw from submitted)
///   InReview    -> PendingInfo  (request-info)
///   InReview    -> Closed       (close with decision, or withdraw)
///   PendingInfo -> InReview     (resume-review)
///   PendingInfo -> Closed       (close, or expire)
///
/// Rejected:
///   Closed      -> anything     (terminal — no re-entry)
///   *           -> *self        (idempotent no-op handled at controller,
///                                NOT a legal state-machine transition)
///   Any         -> Draft        (genesis cannot be re-entered)
///   Backwards   transitions     (InReview -> Submitted, etc.)
///
/// Overdue is NOT a state. It is a read-time projection on
/// <see cref="Appeal.IsOverdue"/> and drives the one-shot
/// <c>AppealOverdueObserved</c> audit event via
/// <c>IAppealRepository.TryTransitionToOverdueAsync</c>. A status transition
/// is NOT emitted for overdue — the appeal remains in Submitted/InReview/
/// PendingInfo until an explicit close.
/// </summary>
/// <remarks>
/// Divergence from <c>ConsentStateMachine</c>: consent models Expired as a
/// distinct terminal status because consent has exactly two termination
/// reasons (Revoked, Expired). Appeals has six+ termination reasons
/// (Approved, Denied, PartialApproval, Withdrawn, Expired-by-deadline,
/// AdminError, Other) and promoting any of them to a status would be
/// arbitrary. We keep the status enum lean (Draft/Submitted/InReview/
/// PendingInfo/Closed) and carry the discriminator on
/// <see cref="AppealClosureReasonCode"/>. Same pattern
/// <c>PersonalRepStateMachine</c> established for the same reason.
/// Reviewers coming from consent-service: this is deliberate, not an
/// oversight.
///
/// Idempotent same-status requests (e.g. submit when already Submitted)
/// are handled at the controller layer by short-circuiting BEFORE calling
/// <see cref="EnsureAllowed"/> — the state machine rejects X->X as illegal.
/// This keeps the state machine strict (it is the invariant enforcer) and
/// puts UX concerns like idempotency where they belong.
/// </remarks>
public static class AppealStateMachine
{
    /// <summary>Pure check — does NOT throw. Returns true iff the transition is legal.</summary>
    public static bool IsAllowed(AppealStatus from, AppealStatus to) =>
        (from, to) switch
        {
            (AppealStatus.Draft,       AppealStatus.Submitted)   => true,
            (AppealStatus.Draft,       AppealStatus.Closed)      => true,
            (AppealStatus.Submitted,   AppealStatus.InReview)    => true,
            (AppealStatus.Submitted,   AppealStatus.Closed)      => true,
            (AppealStatus.InReview,    AppealStatus.PendingInfo) => true,
            (AppealStatus.InReview,    AppealStatus.Closed)      => true,
            (AppealStatus.PendingInfo, AppealStatus.InReview)    => true,
            (AppealStatus.PendingInfo, AppealStatus.Closed)      => true,
            _ => false
        };

    /// <summary>
    /// Validate a transition. Throws
    /// <see cref="InvalidAppealTransitionException"/> if the transition is
    /// not allowed. Repositories and controllers call this BEFORE
    /// persisting; the same check is repeated by the repository's
    /// conditional write to close a TOCTOU window.
    /// </summary>
    public static void EnsureAllowed(AppealStatus from, AppealStatus to)
    {
        if (!IsAllowed(from, to))
            throw new InvalidAppealTransitionException(from, to);
    }

    /// <summary>
    /// Valid closure-reason codes for a given source status. Enforces the
    /// rule that <c>Withdrawn</c> is the only reason valid from Draft or
    /// Submitted (a withdrawal before review, not a decision); decision-
    /// bearing reasons are only valid from InReview or PendingInfo; and
    /// <c>Expired</c> is only valid from PendingInfo (a no-response close).
    /// </summary>
    public static bool IsClosureReasonAllowed(AppealStatus from, AppealClosureReasonCode reason) =>
        (from, reason) switch
        {
            (AppealStatus.Draft,       AppealClosureReasonCode.Withdrawn)       => true,
            (AppealStatus.Submitted,   AppealClosureReasonCode.Withdrawn)       => true,

            (AppealStatus.InReview,    AppealClosureReasonCode.Approved)        => true,
            (AppealStatus.InReview,    AppealClosureReasonCode.Denied)          => true,
            (AppealStatus.InReview,    AppealClosureReasonCode.PartialApproval) => true,
            (AppealStatus.InReview,    AppealClosureReasonCode.Withdrawn)       => true,
            (AppealStatus.InReview,    AppealClosureReasonCode.AdminError)      => true,
            (AppealStatus.InReview,    AppealClosureReasonCode.Other)           => true,

            (AppealStatus.PendingInfo, AppealClosureReasonCode.Approved)        => true,
            (AppealStatus.PendingInfo, AppealClosureReasonCode.Denied)          => true,
            (AppealStatus.PendingInfo, AppealClosureReasonCode.PartialApproval) => true,
            (AppealStatus.PendingInfo, AppealClosureReasonCode.Withdrawn)       => true,
            (AppealStatus.PendingInfo, AppealClosureReasonCode.Expired)         => true,
            (AppealStatus.PendingInfo, AppealClosureReasonCode.AdminError)      => true,
            (AppealStatus.PendingInfo, AppealClosureReasonCode.Other)           => true,

            _ => false
        };
}
