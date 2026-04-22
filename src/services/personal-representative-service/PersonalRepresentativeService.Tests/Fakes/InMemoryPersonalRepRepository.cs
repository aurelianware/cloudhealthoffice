using System.Collections.Concurrent;
using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Repositories;

namespace PersonalRepresentativeService.Tests.Fakes;

/// <summary>
/// In-memory triple: <see cref="IPersonalRepRepository"/> +
/// <see cref="IPersonalRepEventRepository"/> +
/// <see cref="IPersonalRepEventSink"/>. Concurrent-safe for the
/// exactly-once expiry race test. Failure-injection hooks on the pair-write
/// paths drive the atomicity tests.
/// </summary>
public sealed class InMemoryPersonalRepRepository
    : IPersonalRepRepository, IPersonalRepEventRepository, IPersonalRepEventSink
{
    private readonly ConcurrentDictionary<string, PersonalRepresentative> _reps = new();
    private readonly ConcurrentDictionary<string, PersonalRepAssociation> _associations = new();
    private readonly ConcurrentBag<PersonalRepEvent> _events = new();
    private readonly object _sync = new();

    /// <summary>Raised before forward insert; throw to simulate failure mode 2.</summary>
    public Func<PersonalRepAssociation, Task>? OnBeforeForwardInsert { get; set; }

    /// <summary>Raised before inverse insert; throw to simulate failure mode 3.</summary>
    public Func<PersonalRepAssociation, Task>? OnBeforeInverseInsert { get; set; }

    /// <summary>Raised before audit append inside pair write; throw to simulate failure mode 4.</summary>
    public Func<PersonalRepEvent, Task>? OnBeforePairAuditAppend { get; set; }

    /// <summary>Raised when pair write audit append fails. Test hook for log assertions.</summary>
    public int AuditAppendFailureCount;

    private static string RepKey(string tenantId, string repId) => $"{tenantId}:{repId}";

    public Task<PersonalRepresentative> CreateAsync(PersonalRepresentative rep, PersonalRepEvent genesisEvent, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(rep.Id)) rep.Id = Guid.NewGuid().ToString();
        if (rep.CreatedAt == default) rep.CreatedAt = DateTime.UtcNow;

        var clone = CloneRep(rep);
        _reps[RepKey(rep.TenantId, rep.Id)] = clone;
        AppendEventInternal(genesisEvent);
        return Task.FromResult(CloneRep(clone));
    }

    public Task<PersonalRepresentative?> GetByIdAsync(
        string tenantId, string repId, CancellationToken ct = default)
    {
        _reps.TryGetValue(RepKey(tenantId, repId), out var r);
        if (r is null || r.IsDeleted) return Task.FromResult<PersonalRepresentative?>(null);
        return Task.FromResult<PersonalRepresentative?>(CloneRep(r));
    }

    public Task<IReadOnlyList<PersonalRepresentative>> ListByTenantAsync(
        string tenantId, bool activeOnly = false, DateTime? asOf = null, CancellationToken ct = default)
    {
        var t = asOf ?? DateTime.UtcNow;
        var rows = _reps.Values
            .Where(r => r.TenantId == tenantId && !r.IsDeleted)
            .Select(CloneRep)
            .ToList();
        if (activeOnly) rows = rows.Where(r => r.ObservedStatus(t) == PersonalRepStatus.Active).ToList();
        return Task.FromResult<IReadOnlyList<PersonalRepresentative>>(
            rows.OrderByDescending(r => r.CreatedAt).ToList());
    }

    public Task<IReadOnlyList<PersonalRepresentative>> GetByIdsAsync(
        string tenantId, IReadOnlyList<string> repIds, CancellationToken ct = default)
    {
        var set = new HashSet<string>(repIds);
        var rows = _reps.Values
            .Where(r => r.TenantId == tenantId && !r.IsDeleted && set.Contains(r.Id))
            .Select(CloneRep)
            .ToList();
        return Task.FromResult<IReadOnlyList<PersonalRepresentative>>(rows);
    }

    public Task<PersonalRepresentative> TransitionStatusAsync(
        PersonalRepresentative rep, PersonalRepEvent auditEvent, CancellationToken ct = default)
    {
        if (!auditEvent.FromStatus.HasValue)
        {
            throw new ArgumentException(
                "TransitionStatusAsync requires auditEvent.FromStatus to be set.",
                nameof(auditEvent));
        }
        var expectedFromStatus = auditEvent.FromStatus.Value;

        lock (_sync)
        {
            var key = RepKey(rep.TenantId, rep.Id);
            if (!_reps.TryGetValue(key, out var current) || current.Status != expectedFromStatus)
            {
                var actual = current?.Status ?? rep.Status;
                throw new InvalidPersonalRepTransitionException(actual, rep.Status);
            }

            _reps[key] = CloneRep(rep);
            AppendEventInternal(auditEvent);
        }
        return Task.FromResult(CloneRep(rep));
    }

    public Task<PersonalRepresentative?> TryTransitionToInactiveAsync(
        PersonalRepresentative rep, PersonalRepEvent auditEvent)
    {
        lock (_sync)
        {
            var key = RepKey(rep.TenantId, rep.Id);
            if (!_reps.TryGetValue(key, out var current) || current.Status != PersonalRepStatus.Active)
            {
                return Task.FromResult<PersonalRepresentative?>(null);
            }

            current.Status = PersonalRepStatus.Inactive;
            current.InactivationReasonCode = PersonalRepInactivationReasonCode.Expired;
            current.InactivatedAt = DateTime.UtcNow;
            _reps[key] = current;
            AppendEventInternal(auditEvent);
            return Task.FromResult<PersonalRepresentative?>(CloneRep(current));
        }
    }

    public async Task AddAssociationPairAsync(
        PersonalRepAssociation forward,
        PersonalRepAssociation inverse,
        PersonalRepEvent auditEvent,
        CancellationToken ct = default)
    {
        if (!string.Equals(forward.TenantId, inverse.TenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "PersonalRepAssociation pair rows must share the same TenantId.");
        }

        if (OnBeforeForwardInsert is not null) await OnBeforeForwardInsert(forward);

        // Two-phase stage: write forward, then inverse. If inverse-insert
        // hook throws, roll back forward to preserve the "neither row
        // exists" invariant.
        lock (_sync)
        {
            _associations[forward.Id] = CloneAssociation(forward);
        }

        try
        {
            if (OnBeforeInverseInsert is not null) await OnBeforeInverseInsert(inverse);
        }
        catch
        {
            lock (_sync)
            {
                _associations.TryRemove(forward.Id, out _);
            }
            throw;
        }

        lock (_sync)
        {
            _associations[inverse.Id] = CloneAssociation(inverse);
        }

        try
        {
            if (OnBeforePairAuditAppend is not null) await OnBeforePairAuditAppend(auditEvent);
            AppendEventInternal(auditEvent);
        }
        catch
        {
            Interlocked.Increment(ref AuditAppendFailureCount);
            throw;
        }
    }

    public async Task RemoveAssociationPairAsync(
        string tenantId,
        string pairId,
        string removedBy,
        PersonalRepEvent auditEvent,
        CancellationToken ct = default)
    {
        var rows = _associations.Values
            .Where(a => a.TenantId == tenantId && a.PairId == pairId)
            .Select(CloneAssociation)
            .ToList();
        if (rows.Count == 0) return;

        var now = DateTime.UtcNow;
        lock (_sync)
        {
            foreach (var r in rows)
            {
                r.EffectiveTo ??= now;
                r.UpdatedAt = now;
                r.UpdatedBy = removedBy;
                _associations[r.Id] = r;
            }
        }

        try
        {
            if (OnBeforePairAuditAppend is not null) await OnBeforePairAuditAppend(auditEvent);
            AppendEventInternal(auditEvent);
        }
        catch
        {
            Interlocked.Increment(ref AuditAppendFailureCount);
            throw;
        }
    }

    public Task<IReadOnlyList<PersonalRepAssociation>> ListAssociationsForMemberAsync(
        string tenantId, string memberId, bool activeOnly = false, DateTime? asOf = null,
        CancellationToken ct = default)
    {
        var t = asOf ?? DateTime.UtcNow;
        var rows = _associations.Values
            .Where(a => a.TenantId == tenantId
                     && a.MemberId == memberId
                     && a.Direction == AssociationDirection.MemberToRep
                     && !a.IsDeleted)
            .Select(CloneAssociation)
            .ToList();
        if (activeOnly) rows = rows.Where(a =>
            a.EffectiveFrom <= t && (a.EffectiveTo == null || a.EffectiveTo > t)).ToList();
        return Task.FromResult<IReadOnlyList<PersonalRepAssociation>>(rows);
    }

    public Task<IReadOnlyList<PersonalRepAssociation>> ListAssociationsForRepAsync(
        string tenantId, string repId, bool activeOnly = false, DateTime? asOf = null,
        CancellationToken ct = default)
    {
        var t = asOf ?? DateTime.UtcNow;
        var rows = _associations.Values
            .Where(a => a.TenantId == tenantId
                     && a.RepId == repId
                     && a.Direction == AssociationDirection.RepToMember
                     && !a.IsDeleted)
            .Select(CloneAssociation)
            .ToList();
        if (activeOnly) rows = rows.Where(a =>
            a.EffectiveFrom <= t && (a.EffectiveTo == null || a.EffectiveTo > t)).ToList();
        return Task.FromResult<IReadOnlyList<PersonalRepAssociation>>(rows);
    }

    public Task<PersonalRepAssociation?> FindActiveAssociationAsync(
        string tenantId, string repId, string memberId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var row = _associations.Values.FirstOrDefault(a =>
            a.TenantId == tenantId
            && a.RepId == repId
            && a.MemberId == memberId
            && a.Direction == AssociationDirection.RepToMember
            && !a.IsDeleted
            && (a.EffectiveTo == null || a.EffectiveTo > now));
        return Task.FromResult<PersonalRepAssociation?>(row is null ? null : CloneAssociation(row));
    }

    public Task AppendAsync(PersonalRepEvent evt)
    {
        AppendEventInternal(evt);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PersonalRepEvent>> ListByRepAsync(
        string tenantId, string personalRepId, CancellationToken ct = default)
    {
        var rows = _events
            .Where(e => e.TenantId == tenantId && e.PersonalRepId == personalRepId)
            .OrderBy(e => e.OccurredAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<PersonalRepEvent>>(rows);
    }

    public IReadOnlyList<PersonalRepEvent> SnapshotEvents() =>
        _events.OrderBy(e => e.OccurredAt).ToList();

    public IReadOnlyList<PersonalRepAssociation> SnapshotAssociations() =>
        _associations.Values.Select(CloneAssociation).ToList();

    private void AppendEventInternal(PersonalRepEvent evt)
    {
        lock (_sync)
        {
            if (_events.Any(e =>
                    e.TenantId == evt.TenantId &&
                    e.PersonalRepId == evt.PersonalRepId &&
                    e.EventId == evt.EventId))
            {
                return;
            }
            _events.Add(CloneEvent(evt));
        }
    }

    private static PersonalRepresentative CloneRep(PersonalRepresentative r) => new()
    {
        TenantId = r.TenantId,
        Id = r.Id,
        Status = r.Status,
        CredentialType = r.CredentialType,
        EffectiveFrom = r.EffectiveFrom,
        EffectiveTo = r.EffectiveTo,
        ExpiresAt = r.ExpiresAt,
        ProofOfAuthorityDocumentId = r.ProofOfAuthorityDocumentId,
        FirstName = r.FirstName,
        MiddleName = r.MiddleName,
        LastName = r.LastName,
        Email = r.Email,
        PhoneNumber = r.PhoneNumber,
        MailingAddressLine1 = r.MailingAddressLine1,
        MailingAddressLine2 = r.MailingAddressLine2,
        MailingAddressCity = r.MailingAddressCity,
        MailingAddressStateCode = r.MailingAddressStateCode,
        MailingAddressPostalCode = r.MailingAddressPostalCode,
        RelationshipNotes = r.RelationshipNotes,
        CreatedAt = r.CreatedAt,
        CreatedBy = r.CreatedBy,
        UpdatedAt = r.UpdatedAt,
        UpdatedBy = r.UpdatedBy,
        ActivatedAt = r.ActivatedAt,
        ActivatedBy = r.ActivatedBy,
        InactivatedAt = r.InactivatedAt,
        InactivatedBy = r.InactivatedBy,
        InactivationReasonCode = r.InactivationReasonCode,
        DeletedAt = r.DeletedAt,
        DeletedBy = r.DeletedBy,
        DeletedReason = r.DeletedReason
    };

    private static PersonalRepAssociation CloneAssociation(PersonalRepAssociation a) => new()
    {
        Id = a.Id,
        TenantId = a.TenantId,
        PairId = a.PairId,
        RepId = a.RepId,
        MemberId = a.MemberId,
        Direction = a.Direction,
        CredentialType = a.CredentialType,
        EffectiveFrom = a.EffectiveFrom,
        EffectiveTo = a.EffectiveTo,
        CreatedAt = a.CreatedAt,
        CreatedBy = a.CreatedBy,
        UpdatedAt = a.UpdatedAt,
        UpdatedBy = a.UpdatedBy,
        DeletedAt = a.DeletedAt,
        DeletedBy = a.DeletedBy,
        DeletedReason = a.DeletedReason
    };

    private static PersonalRepEvent CloneEvent(PersonalRepEvent e) => new()
    {
        Id = e.Id,
        PartitionKey = e.PartitionKey,
        TenantId = e.TenantId,
        PersonalRepId = e.PersonalRepId,
        MemberId = e.MemberId,
        EventId = e.EventId,
        EventType = e.EventType,
        FromStatus = e.FromStatus,
        ToStatus = e.ToStatus,
        ActorId = e.ActorId,
        CorrelationId = e.CorrelationId,
        OccurredAt = e.OccurredAt,
        Payload = e.Payload
    };
}
