using ConsentService.Models;

namespace ConsentService.Repositories;

/// <summary>
/// Repository surface for <see cref="Consent"/>. Mirrors the shape of
/// <c>MemberService.Repositories.IMemberAlertRepository</c>: tenantId is
/// always the first positional argument; reads return <c>Task&lt;T?&gt;</c> or
/// <c>Task&lt;IReadOnlyList&lt;T&gt;&gt;</c>; lifecycle methods are
/// verb-named (no generic <c>UpdateAsync</c>).
///
/// <see cref="TransitionStatusAsync"/> is the single lifecycle write: it
/// updates <see cref="Consent.Status"/> AND appends a <see cref="ConsentEvent"/>
/// row atomically. There is no separate <c>UpdateAsync</c>+<c>AppendEventAsync</c>
/// pair — the audit-trail invariant is enforced structurally.
///
/// <see cref="TryTransitionToExpiredAsync"/> is conditional on current
/// status so concurrent readers that observe the expiry transition can
/// race to persist it exactly once.
/// </summary>
public interface IConsentRepository
{
    Task<Consent> CreateAsync(Consent consent, ConsentEvent genesisEvent);

    Task<Consent?> GetByIdAsync(string tenantId, string memberId, string consentId);

    Task<IReadOnlyList<Consent>> ListByMemberAsync(
        string tenantId,
        string memberId,
        bool activeOnly,
        DateTime? asOf = null);

    /// <summary>
    /// Atomic: persist the new status on <paramref name="consent"/> AND
    /// append <paramref name="auditEvent"/>. Caller must set
    /// <c>consent.Status</c> to the new value and populate any lifecycle
    /// fields (ActivatedAt, RevokedAt, etc.) before calling. Callers MUST
    /// have validated the transition via <c>ConsentStateMachine</c>.
    /// </summary>
    Task<Consent> TransitionStatusAsync(Consent consent, ConsentEvent auditEvent);

    /// <summary>
    /// Race-safe Active -> Expired persistence. Returns <c>true</c> iff this
    /// caller won the race to persist the transition (and the audit event
    /// was appended). Returns <c>false</c> if the record was no longer
    /// Active at write time — another caller already expired it. Callers
    /// that get <c>false</c> must NOT retry with a different event.
    /// </summary>
    Task<bool> TryTransitionToExpiredAsync(Consent consent, ConsentEvent auditEvent);
}

/// <summary>Repository surface for <see cref="ConsentEvent"/> (audit trail reads).</summary>
public interface IConsentEventRepository
{
    Task<IReadOnlyList<ConsentEvent>> ListByConsentAsync(
        string tenantId,
        string consentId,
        CancellationToken ct = default);
}
