using PersonalRepresentativeService.Models;

namespace PersonalRepresentativeService.Services;

/// <summary>
/// Pure transition validator. No side effects — callers assemble the
/// <see cref="PersonalRepEvent"/> themselves and pass the (rep, new-status,
/// event) triple to the repository, which enforces atomicity.
///
/// Allowed transitions:
///   (none) -> Draft
///   Draft  -> Active
///   Draft  -> Inactive   (revoke before activation)
///   Active -> Inactive   (revoke, expiry, guardianship ended, etc.)
///
/// Rejected:
///   Inactive -> anything           (terminal)
///   Active   -> Active             (idempotent no-op at controller, NOT a transition)
///   Draft    -> Draft              (idempotent no-op)
///   Active   -> Draft              (backwards)
/// </summary>
/// <remarks>
/// Divergence from ConsentStateMachine: consent models Expired as a distinct
/// terminal status because consent has exactly two termination reasons
/// (Revoked, Expired) and promoting both to status is cheap. PersonalRep has
/// five+ inactivation reasons (RepDeceased, PoaRevoked, GuardianshipEnded,
/// Expired, AdminError, Other) and promoting one of them to a status would
/// be arbitrary. We keep the status enum lean (Draft/Active/Inactive) and
/// carry the discriminator on <see cref="PersonalRepInactivationReasonCode"/>.
/// Reviewers coming from consent-service: this is deliberate, not an
/// oversight.
///
/// TODO(review-workflow-followup): Multi-step approval ("legal review
/// pending", "awaiting notary verification") is not in this state machine.
/// When a review workflow lands, it joins between Draft and Active as a
/// new state.
/// </remarks>
public static class PersonalRepStateMachine
{
    /// <summary>Pure check — does NOT throw. Returns true iff the transition is legal.</summary>
    public static bool IsAllowed(PersonalRepStatus from, PersonalRepStatus to) =>
        (from, to) switch
        {
            (PersonalRepStatus.Draft,  PersonalRepStatus.Active)   => true,
            (PersonalRepStatus.Draft,  PersonalRepStatus.Inactive) => true,
            (PersonalRepStatus.Active, PersonalRepStatus.Inactive) => true,
            _ => false
        };

    /// <summary>
    /// Validate a transition. Throws
    /// <see cref="InvalidPersonalRepTransitionException"/> if the transition
    /// is not allowed. Repositories and controllers call this BEFORE
    /// persisting; the same check is repeated by the repository's
    /// conditional write to close a TOCTOU window.
    /// </summary>
    public static void EnsureAllowed(PersonalRepStatus from, PersonalRepStatus to)
    {
        if (!IsAllowed(from, to))
            throw new InvalidPersonalRepTransitionException(from, to);
    }
}
