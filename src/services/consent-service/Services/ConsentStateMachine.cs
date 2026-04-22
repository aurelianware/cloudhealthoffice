using ConsentService.Models;

namespace ConsentService.Services;

/// <summary>
/// Pure transition validator. No side effects — callers assemble the
/// <see cref="ConsentEvent"/> themselves and pass the (consent, new-status,
/// event) triple to the repository, which enforces atomicity.
///
/// Allowed transitions:
///   (none) -> Draft
///   Draft -> Active
///   Draft -> Revoked
///   Active -> Revoked
///   Active -> Expired   (observed at read time; persisted via TryTransitionToExpiredAsync)
///
/// Rejected:
///   Revoked -> anything            (terminal)
///   Expired -> anything            (terminal)
///   Active -> Active               (idempotent no-op at controller, NOT a transition)
///   Draft -> Draft                 (idempotent no-op)
///   Active -> Draft                (backwards)
///
/// Pending is deliberately not modeled. When a human-review workflow lands
/// (feature 5.18-followup), Pending joins the state machine then.
/// </summary>
public static class ConsentStateMachine
{
    /// <summary>Pure check — does NOT throw. Returns true iff the transition is legal.</summary>
    public static bool IsAllowed(ConsentStatus from, ConsentStatus to) =>
        (from, to) switch
        {
            (ConsentStatus.Draft,  ConsentStatus.Active)   => true,
            (ConsentStatus.Draft,  ConsentStatus.Revoked)  => true,
            (ConsentStatus.Active, ConsentStatus.Revoked)  => true,
            (ConsentStatus.Active, ConsentStatus.Expired)  => true,
            _ => false
        };

    /// <summary>
    /// Validate a transition. Throws <see cref="InvalidConsentTransitionException"/>
    /// if the transition is not allowed. Repositories and controllers call
    /// this BEFORE persisting; the same check is repeated by the repository's
    /// conditional write to close a TOCTOU window.
    /// </summary>
    public static void EnsureAllowed(ConsentStatus from, ConsentStatus to)
    {
        if (!IsAllowed(from, to))
            throw new InvalidConsentTransitionException(from, to);
    }
}
