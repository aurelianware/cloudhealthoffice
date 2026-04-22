using PersonalRepresentativeService.Models;

namespace PersonalRepresentativeService.Repositories;

/// <summary>
/// Repository surface for <see cref="PersonalRepresentative"/> plus the
/// <see cref="PersonalRepAssociation"/> pair-write operations. The
/// association is a sub-concept of the rep (not its own aggregate), so it
/// lives on the same repository interface — matching the consent-service
/// posture where <c>ConsentEvent</c> is a sub-concept of <c>Consent</c>.
///
/// Lifecycle methods are verb-named. There is no generic
/// <c>UpdateAsync</c>+<c>AppendEventAsync</c>: every status mutation and
/// every association add/remove goes through a method that writes the
/// audit row in the same repository call. The audit-trail invariant is
/// enforced structurally.
/// </summary>
public interface IPersonalRepRepository
{
    Task<PersonalRepresentative> CreateAsync(PersonalRepresentative rep, PersonalRepEvent genesisEvent, CancellationToken ct = default);

    Task<PersonalRepresentative?> GetByIdAsync(string tenantId, string repId, CancellationToken ct = default);

    /// <summary>
    /// Batch-fetch reps by id list. Duplicates in <paramref name="repIds"/>
    /// are deduplicated; records not found are silently omitted.
    /// </summary>
    Task<IReadOnlyList<PersonalRepresentative>> GetByIdsAsync(
        string tenantId, IReadOnlyList<string> repIds, CancellationToken ct = default);

    Task<IReadOnlyList<PersonalRepresentative>> ListByTenantAsync(
        string tenantId, bool activeOnly = false, DateTime? asOf = null, CancellationToken ct = default);

    /// <summary>
    /// Atomic: persist the new status on <paramref name="rep"/> AND append
    /// <paramref name="auditEvent"/>. Caller must set <c>rep.Status</c> to
    /// the new value and populate any lifecycle fields (ActivatedAt,
    /// InactivatedAt, etc.) before calling. Callers MUST have validated
    /// the transition via <c>PersonalRepStateMachine</c>.
    /// </summary>
    Task<PersonalRepresentative> TransitionStatusAsync(
        PersonalRepresentative rep, PersonalRepEvent auditEvent, CancellationToken ct = default);

    /// <summary>
    /// Race-safe Active → Inactive persistence for the read-time expiry
    /// observer. Returns the updated <see cref="PersonalRepresentative"/>
    /// (with <c>Status=Inactive</c>, <c>InactivationReasonCode=Expired</c>,
    /// and <c>InactivatedAt</c> set) if this caller won the race and the
    /// audit event was appended. Returns <c>null</c> if the record was no
    /// longer Active at write time — another caller already inactivated it.
    /// Callers that receive <c>null</c> must NOT retry with a different event.
    /// </summary>
    Task<PersonalRepresentative?> TryTransitionToInactiveAsync(
        PersonalRepresentative rep, PersonalRepEvent auditEvent);

    /// <summary>
    /// Atomically persist the forward (RepToMember) and inverse
    /// (MemberToRep) rows of an association, along with the audit event.
    /// Cosmos uses <c>TransactionalBatch</c> (both rows share tenantId);
    /// Mongo uses a session with a transaction.
    ///
    /// Failure-mode contract (enforced by
    /// <c>PersonalRepAssociationPairAtomicityTests</c>):
    ///   1. Tenant mismatch → <see cref="InvalidOperationException"/>, no writes.
    ///   2. Forward insert fails → transaction aborts, no audit event.
    ///   3. Inverse insert fails → transaction aborts, no audit event.
    ///   4. Pair commits, audit append fails → pair persisted, audit
    ///      missing, <c>ILogger.LogError</c> recorded so the gap is
    ///      observable in logs; exception propagates to caller.
    /// </summary>
    Task AddAssociationPairAsync(
        PersonalRepAssociation forward,
        PersonalRepAssociation inverse,
        PersonalRepEvent auditEvent,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically soft-delete both rows of an association pair (set
    /// <see cref="PersonalRepAssociation.EffectiveTo"/>) and append the
    /// audit event. Same four-failure-mode contract as
    /// <see cref="AddAssociationPairAsync"/>.
    /// </summary>
    Task RemoveAssociationPairAsync(
        string tenantId,
        string pairId,
        string removedBy,
        PersonalRepEvent auditEvent,
        CancellationToken ct = default);

    Task<IReadOnlyList<PersonalRepAssociation>> ListAssociationsForMemberAsync(
        string tenantId,
        string memberId,
        bool activeOnly = false,
        DateTime? asOf = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<PersonalRepAssociation>> ListAssociationsForRepAsync(
        string tenantId,
        string repId,
        bool activeOnly = false,
        DateTime? asOf = null,
        CancellationToken ct = default);

    Task<PersonalRepAssociation?> FindActiveAssociationAsync(
        string tenantId,
        string repId,
        string memberId,
        CancellationToken ct = default);
}

/// <summary>Repository surface for <see cref="PersonalRepEvent"/> (audit trail reads).</summary>
public interface IPersonalRepEventRepository
{
    Task<IReadOnlyList<PersonalRepEvent>> ListByRepAsync(
        string tenantId,
        string personalRepId,
        CancellationToken ct = default);
}

/// <summary>
/// Repository-local sink for appending <see cref="PersonalRepEvent"/> rows.
/// Lets the Cosmos and Mongo <see cref="IPersonalRepRepository"/>
/// implementations share a single transition-and-append shape while
/// keeping their own storage choice for audit rows.
/// </summary>
public interface IPersonalRepEventSink
{
    Task AppendAsync(PersonalRepEvent evt);
}
