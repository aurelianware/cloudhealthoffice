using System.Collections.Concurrent;
using ConsentService.Models;
using ConsentService.Repositories;

namespace ConsentService.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IConsentRepository"/> + <see cref="IConsentEventRepository"/>
/// + <see cref="IConsentEventSink"/> triple. Shared across controller and
/// integration tests. Not a production substitute — the concurrency
/// primitives here are sufficient to exercise the Race-safe expiry path
/// but don't reproduce Cosmos/Mongo operator semantics.
/// </summary>
public sealed class InMemoryConsentRepository : IConsentRepository, IConsentEventRepository, IConsentEventSink
{
    private readonly ConcurrentDictionary<string, Consent> _consents = new();
    private readonly ConcurrentBag<ConsentEvent> _events = new();
    private readonly object _sync = new();

    private static string Key(string tenantId, string consentId) => $"{tenantId}:{consentId}";

    public Task<Consent> CreateAsync(Consent consent, ConsentEvent genesisEvent)
    {
        if (string.IsNullOrEmpty(consent.Id)) consent.Id = Guid.NewGuid().ToString();
        if (consent.CreatedAt == default) consent.CreatedAt = DateTime.UtcNow;

        var clone = Clone(consent);
        _consents[Key(consent.TenantId, consent.Id)] = clone;
        AppendEvent(genesisEvent);
        return Task.FromResult(Clone(clone));
    }

    public Task<Consent?> GetByIdAsync(string tenantId, string memberId, string consentId)
    {
        _consents.TryGetValue(Key(tenantId, consentId), out var c);
        if (c is null || c.MemberId != memberId) return Task.FromResult<Consent?>(null);
        return Task.FromResult<Consent?>(Clone(c));
    }

    public Task<IReadOnlyList<Consent>> ListByMemberAsync(
        string tenantId, string memberId, bool activeOnly, DateTime? asOf = null)
    {
        var t = asOf ?? DateTime.UtcNow;
        var rows = _consents.Values
            .Where(c => c.TenantId == tenantId && c.MemberId == memberId)
            .Select(Clone)
            .ToList();

        if (activeOnly) rows = rows.Where(c => c.ObservedStatus(t) == ConsentStatus.Active).ToList();

        return Task.FromResult<IReadOnlyList<Consent>>(
            rows.OrderByDescending(c => c.CreatedAt).ToList());
    }

    public Task<Consent> TransitionStatusAsync(Consent consent, ConsentEvent auditEvent)
    {
        // Mirror the Cosmos + Mongo contract: only persist when the
        // caller-supplied from-status matches the currently persisted
        // status. A mismatch means a concurrent writer won the race.
        if (!auditEvent.FromStatus.HasValue)
        {
            throw new ArgumentException(
                "TransitionStatusAsync requires auditEvent.FromStatus to be set.",
                nameof(auditEvent));
        }
        var expectedFromStatus = auditEvent.FromStatus.Value;

        lock (_sync)
        {
            var key = Key(consent.TenantId, consent.Id);
            if (!_consents.TryGetValue(key, out var current) || current.Status != expectedFromStatus)
            {
                var actual = current?.Status ?? consent.Status;
                throw new InvalidConsentTransitionException(actual, consent.Status);
            }

            _consents[key] = Clone(consent);
            AppendEvent(auditEvent);
        }
        return Task.FromResult(Clone(consent));
    }

    public Task<bool> TryTransitionToExpiredAsync(Consent consent, ConsentEvent auditEvent)
    {
        lock (_sync)
        {
            var key = Key(consent.TenantId, consent.Id);
            if (!_consents.TryGetValue(key, out var current) || current.Status != ConsentStatus.Active)
            {
                return Task.FromResult(false);
            }

            current.Status = ConsentStatus.Expired;
            current.RevocationReasonCode = ConsentRevocationReasonCode.Expired;
            _consents[key] = current;
            AppendEvent(auditEvent);
            return Task.FromResult(true);
        }
    }

    public Task AppendAsync(ConsentEvent evt)
    {
        AppendEvent(evt);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConsentEvent>> ListByConsentAsync(
        string tenantId, string consentId, CancellationToken ct = default)
    {
        var rows = _events
            .Where(e => e.TenantId == tenantId && e.ConsentId == consentId)
            .OrderBy(e => e.OccurredAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<ConsentEvent>>(rows);
    }

    public IReadOnlyList<ConsentEvent> SnapshotEvents() =>
        _events.OrderBy(e => e.OccurredAt).ToList();

    private void AppendEvent(ConsentEvent evt)
    {
        lock (_sync)
        {
            if (_events.Any(e =>
                    e.TenantId == evt.TenantId &&
                    e.ConsentId == evt.ConsentId &&
                    e.EventId == evt.EventId))
            {
                return;
            }
            _events.Add(CloneEvent(evt));
        }
    }

    private static Consent Clone(Consent c) => new()
    {
        TenantId = c.TenantId,
        Id = c.Id,
        MemberId = c.MemberId,
        ConsentType = c.ConsentType,
        SensitiveCategory = c.SensitiveCategory,
        Status = c.Status,
        EffectiveAt = c.EffectiveAt,
        ExpiresAt = c.ExpiresAt,
        GrantedBy = c.GrantedBy,
        Reason = c.Reason,
        GrantedToName = c.GrantedToName,
        GrantedToContact = c.GrantedToContact,
        Purpose = c.Purpose,
        CreatedAt = c.CreatedAt,
        ActivatedBy = c.ActivatedBy,
        ActivatedAt = c.ActivatedAt,
        RevokedBy = c.RevokedBy,
        RevokedAt = c.RevokedAt,
        RevocationReasonCode = c.RevocationReasonCode
    };

    private static ConsentEvent CloneEvent(ConsentEvent e) => new()
    {
        Id = e.Id,
        PartitionKey = e.PartitionKey,
        TenantId = e.TenantId,
        ConsentId = e.ConsentId,
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
