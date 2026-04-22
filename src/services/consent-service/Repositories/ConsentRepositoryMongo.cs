using ConsentService.Models;
using MongoDB.Driver;

namespace ConsentService.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IConsentRepository"/>. The
/// transition-and-append shape is not truly atomic across the Consent and
/// ConsentEvent collections — Mongo multi-collection transactions require
/// a replica-set primary, which our dev and many production deployments do
/// not guarantee. Operationally we accept that a transition-then-event
/// crash window can drop a single audit row; the consent record update is
/// still conditional (filter on current Status) so the status invariant
/// holds. If operations observe a gap, the source of truth is the consent
/// row itself — the event log is an audit annotation, not the authoritative
/// lifecycle store.
/// </summary>
public class ConsentRepositoryMongo : IConsentRepository
{
    public const string ConsentsCollectionName = "Consents";

    private readonly IMongoCollection<Consent> _consents;
    private readonly IConsentEventSink _events;

    public ConsentRepositoryMongo(IMongoDatabase database, IConsentEventSink events)
    {
        _consents = database.GetCollection<Consent>(ConsentsCollectionName);
        _events = events;
    }

    public async Task<Consent> CreateAsync(Consent consent, ConsentEvent genesisEvent)
    {
        if (string.IsNullOrEmpty(consent.Id)) consent.Id = Guid.NewGuid().ToString();
        if (consent.CreatedAt == default) consent.CreatedAt = DateTime.UtcNow;
        await _consents.InsertOneAsync(consent);
        await _events.AppendAsync(genesisEvent);
        return consent;
    }

    public async Task<Consent?> GetByIdAsync(string tenantId, string memberId, string consentId)
    {
        var filter = Builders<Consent>.Filter.Eq(c => c.TenantId, tenantId)
                   & Builders<Consent>.Filter.Eq(c => c.MemberId, memberId)
                   & Builders<Consent>.Filter.Eq(c => c.Id, consentId);
        return await _consents.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<Consent>> ListByMemberAsync(
        string tenantId, string memberId, bool activeOnly, DateTime? asOf = null)
    {
        var filter = Builders<Consent>.Filter.Eq(c => c.TenantId, tenantId)
                   & Builders<Consent>.Filter.Eq(c => c.MemberId, memberId);

        // Server-side sort by createdAt desc — backed by the
        // (tenantId, memberId, createdAt desc) index from
        // ConsentIndexInitializer. activeOnly is computed in-memory
        // because ObservedStatus depends on ExpiresAt vs now and cannot
        // be expressed as a stable Mongo query.
        var results = await _consents.Find(filter)
            .SortByDescending(c => c.CreatedAt)
            .ToListAsync();

        var t = asOf ?? DateTime.UtcNow;
        if (activeOnly)
            results = results.Where(c => c.ObservedStatus(t) == ConsentStatus.Active).ToList();

        return results;
    }

    public async Task<Consent> TransitionStatusAsync(Consent consent, ConsentEvent auditEvent)
    {
        // Conditional on persisted Status still matching the expected
        // from-status. Closes the TOCTOU window between the controller's
        // GetByIdAsync and this write: a concurrent writer that
        // transitioned the record first causes MatchedCount == 0, and we
        // surface the lost race as InvalidConsentTransitionException
        // (mapped to 409 by the controller layer). Audit event is only
        // appended on success.
        if (!auditEvent.FromStatus.HasValue)
        {
            throw new ArgumentException(
                "TransitionStatusAsync requires auditEvent.FromStatus to be set.",
                nameof(auditEvent));
        }
        var expectedFromStatus = auditEvent.FromStatus.Value;

        var filter = Builders<Consent>.Filter.Eq(c => c.TenantId, consent.TenantId)
                   & Builders<Consent>.Filter.Eq(c => c.Id, consent.Id)
                   & Builders<Consent>.Filter.Eq(c => c.Status, expectedFromStatus);

        var replaceResult = await _consents.ReplaceOneAsync(filter, consent);
        if (replaceResult.MatchedCount == 0)
        {
            throw new InvalidConsentTransitionException(expectedFromStatus, consent.Status);
        }

        await _events.AppendAsync(auditEvent);
        return consent;
    }

    public async Task<bool> TryTransitionToExpiredAsync(Consent consent, ConsentEvent auditEvent)
    {
        // Conditional on Status == Active. If another caller already
        // expired or revoked the record, FindOneAndUpdate returns null and
        // we do NOT append the audit event — exactly-once semantics.
        var filter = Builders<Consent>.Filter.Eq(c => c.TenantId, consent.TenantId)
                   & Builders<Consent>.Filter.Eq(c => c.Id, consent.Id)
                   & Builders<Consent>.Filter.Eq(c => c.Status, ConsentStatus.Active);

        var update = Builders<Consent>.Update
            .Set(c => c.Status, ConsentStatus.Expired)
            .Set(c => c.RevocationReasonCode, ConsentRevocationReasonCode.Expired);

        var options = new FindOneAndUpdateOptions<Consent>
        {
            ReturnDocument = ReturnDocument.After
        };

        var updated = await _consents.FindOneAndUpdateAsync(filter, update, options);
        if (updated is null)
        {
            return false;
        }

        await _events.AppendAsync(auditEvent);
        return true;
    }
}
