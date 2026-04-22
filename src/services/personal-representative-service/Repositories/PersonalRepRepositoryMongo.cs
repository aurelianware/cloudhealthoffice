using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Services;
using MongoDB.Driver;
using Microsoft.Extensions.Logging;

namespace PersonalRepresentativeService.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IPersonalRepRepository"/>.
///
/// Association pair writes use a session transaction (replica set required
/// in non-dev). Multi-document transactions degrade with a helpful error on
/// standalone deployments — the service refuses to persist a partial pair
/// rather than silently breaking the symmetric invariant. The audit event
/// append happens OUTSIDE the transaction (it's a separate collection;
/// keeping the pair write itself atomic is the primary concern). Four
/// failure modes are explicitly tested in
/// <c>PersonalRepAssociationPairAtomicityTests</c>:
///   1. Tenant mismatch → <see cref="InvalidOperationException"/>, no writes.
///   2. Forward insert fails inside session → transaction aborts,
///      inverse rolled back, audit not written.
///   3. Inverse insert fails inside session → transaction aborts, audit
///      not written.
///   4. Pair commits, audit append fails → pair persisted, audit missing,
///      ILogger.LogError recorded, exception propagates.
///
/// The same Mongo "transition + audit isn't fully atomic" caveat from
/// consent-service applies for <see cref="TransitionStatusAsync"/>: a
/// crash between the conditional ReplaceOne and the audit append drops a
/// single audit row. Source of truth is the rep row itself.
/// </summary>
public class PersonalRepRepositoryMongo : IPersonalRepRepository
{
    public const string PersonalRepsCollectionName = "PersonalRepresentatives";
    public const string PersonalRepAssociationsCollectionName = "PersonalRepAssociations";

    private readonly IMongoClient _client;
    private readonly IMongoCollection<PersonalRepresentative> _reps;
    private readonly IMongoCollection<PersonalRepAssociation> _associations;
    private readonly IPersonalRepEventSink _events;
    private readonly ILogger<PersonalRepRepositoryMongo> _logger;

    public PersonalRepRepositoryMongo(
        IMongoClient client,
        IMongoDatabase database,
        IPersonalRepEventSink events,
        ILogger<PersonalRepRepositoryMongo> logger)
    {
        _client = client;
        _reps = database.GetCollection<PersonalRepresentative>(PersonalRepsCollectionName);
        _associations = database.GetCollection<PersonalRepAssociation>(PersonalRepAssociationsCollectionName);
        _events = events;
        _logger = logger;
    }

    public async Task<PersonalRepresentative> CreateAsync(
        PersonalRepresentative rep, PersonalRepEvent genesisEvent, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(rep.Id)) rep.Id = Guid.NewGuid().ToString();
        if (rep.CreatedAt == default) rep.CreatedAt = DateTime.UtcNow;
        await _reps.InsertOneAsync(rep, cancellationToken: ct);
        await _events.AppendAsync(genesisEvent);
        return rep;
    }

    public async Task<PersonalRepresentative?> GetByIdAsync(
        string tenantId, string repId, CancellationToken ct = default)
    {
        var filter = Builders<PersonalRepresentative>.Filter.Eq(r => r.TenantId, tenantId)
                   & Builders<PersonalRepresentative>.Filter.Eq(r => r.Id, repId)
                   & Builders<PersonalRepresentative>.Filter.Eq(r => r.DeletedAt, (DateTime?)null);
        return await _reps.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<PersonalRepresentative>> GetByIdsAsync(
        string tenantId, IReadOnlyList<string> repIds, CancellationToken ct = default)
    {
        if (repIds.Count == 0) return Array.Empty<PersonalRepresentative>();

        var deduped = repIds.Distinct().ToList();
        var filter = Builders<PersonalRepresentative>.Filter.Eq(r => r.TenantId, tenantId)
                   & Builders<PersonalRepresentative>.Filter.In(r => r.Id, deduped)
                   & Builders<PersonalRepresentative>.Filter.Eq(r => r.DeletedAt, (DateTime?)null);
        return await _reps.Find(filter).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PersonalRepresentative>> ListByTenantAsync(
        string tenantId, bool activeOnly = false, DateTime? asOf = null, CancellationToken ct = default)
    {
        var filter = Builders<PersonalRepresentative>.Filter.Eq(r => r.TenantId, tenantId)
                   & Builders<PersonalRepresentative>.Filter.Eq(r => r.DeletedAt, (DateTime?)null);

        var results = await _reps.Find(filter)
            .SortByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

        var t = asOf ?? DateTime.UtcNow;
        if (activeOnly)
            results = results.Where(r => r.ObservedStatus(t) == PersonalRepStatus.Active).ToList();

        return results;
    }

    public async Task<PersonalRepresentative> TransitionStatusAsync(
        PersonalRepresentative rep, PersonalRepEvent auditEvent, CancellationToken ct = default)
    {
        if (!auditEvent.FromStatus.HasValue)
        {
            throw new ArgumentException(
                "TransitionStatusAsync requires auditEvent.FromStatus to be set.",
                nameof(auditEvent));
        }
        var expectedFromStatus = auditEvent.FromStatus.Value;

        var filter = Builders<PersonalRepresentative>.Filter.Eq(r => r.TenantId, rep.TenantId)
                   & Builders<PersonalRepresentative>.Filter.Eq(r => r.Id, rep.Id)
                   & Builders<PersonalRepresentative>.Filter.Eq(r => r.Status, expectedFromStatus);

        var replaceResult = await _reps.ReplaceOneAsync(filter, rep, cancellationToken: ct);
        if (replaceResult.MatchedCount == 0)
        {
            throw new InvalidPersonalRepTransitionException(expectedFromStatus, rep.Status);
        }

        await _events.AppendAsync(auditEvent);
        return rep;
    }

    public async Task<PersonalRepresentative?> TryTransitionToInactiveAsync(
        PersonalRepresentative rep, PersonalRepEvent auditEvent)
    {
        var filter = Builders<PersonalRepresentative>.Filter.Eq(r => r.TenantId, rep.TenantId)
                   & Builders<PersonalRepresentative>.Filter.Eq(r => r.Id, rep.Id)
                   & Builders<PersonalRepresentative>.Filter.Eq(r => r.Status, PersonalRepStatus.Active);

        var update = Builders<PersonalRepresentative>.Update
            .Set(r => r.Status, PersonalRepStatus.Inactive)
            .Set(r => r.InactivationReasonCode, PersonalRepInactivationReasonCode.Expired)
            .Set(r => r.InactivatedAt, DateTime.UtcNow);

        var options = new FindOneAndUpdateOptions<PersonalRepresentative>
        {
            ReturnDocument = ReturnDocument.After
        };

        var updated = await _reps.FindOneAndUpdateAsync(filter, update, options);
        if (updated is null)
        {
            return null;
        }

        await _events.AppendAsync(auditEvent);
        return updated;
    }

    public async Task AddAssociationPairAsync(
        PersonalRepAssociation forward,
        PersonalRepAssociation inverse,
        PersonalRepEvent auditEvent,
        CancellationToken ct = default)
    {
        EnsureSameTenant(forward, inverse);

        // Session transaction: both inserts commit atomically or neither
        // does. On standalone Mongo (no replica set) StartTransaction
        // throws — this is intentional; we refuse to persist a partial
        // pair in an environment that can't guarantee atomicity.
        using var session = await _client.StartSessionAsync(cancellationToken: ct);
        session.StartTransaction();
        try
        {
            await _associations.InsertManyAsync(session, new[] { forward, inverse }, cancellationToken: ct);
            await session.CommitTransactionAsync(ct);
        }
        catch
        {
            await session.AbortTransactionAsync(ct);
            throw;
        }

        try
        {
            await _events.AppendAsync(auditEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Personal Rep association pair committed but audit append failed " +
                "(tenantId={TenantId}, pairId={PairId}, eventId={EventId}, correlationId={CorrelationId}). " +
                "Pair persisted; audit row missing — manual reconciliation required.",
                LogSanitizer.SafeForLog(forward.TenantId),
                LogSanitizer.SafeForLog(forward.PairId),
                LogSanitizer.SafeForLog(auditEvent.EventId),
                LogSanitizer.SafeForLog(auditEvent.CorrelationId));
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
        var pairFilter = Builders<PersonalRepAssociation>.Filter.Eq(a => a.TenantId, tenantId)
                       & Builders<PersonalRepAssociation>.Filter.Eq(a => a.PairId, pairId);
        var rows = await _associations.Find(pairFilter).ToListAsync(ct);
        if (rows.Count == 0) return;

        var now = DateTime.UtcNow;
        using var session = await _client.StartSessionAsync(cancellationToken: ct);
        session.StartTransaction();
        try
        {
            foreach (var row in rows)
            {
                var filter = Builders<PersonalRepAssociation>.Filter.Eq(a => a.Id, row.Id)
                           & Builders<PersonalRepAssociation>.Filter.Eq(a => a.TenantId, tenantId);
                var update = Builders<PersonalRepAssociation>.Update
                    .Set(a => a.EffectiveTo, row.EffectiveTo ?? now)
                    .Set(a => a.UpdatedAt, now)
                    .Set(a => a.UpdatedBy, removedBy);
                await _associations.UpdateOneAsync(session, filter, update, cancellationToken: ct);
            }
            await session.CommitTransactionAsync(ct);
        }
        catch
        {
            await session.AbortTransactionAsync(ct);
            throw;
        }

        try
        {
            await _events.AppendAsync(auditEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Personal Rep association pair removed but audit append failed " +
                "(tenantId={TenantId}, pairId={PairId}, eventId={EventId}, correlationId={CorrelationId}). " +
                "Pair soft-deleted; audit row missing — manual reconciliation required.",
                LogSanitizer.SafeForLog(tenantId),
                LogSanitizer.SafeForLog(pairId),
                LogSanitizer.SafeForLog(auditEvent.EventId),
                LogSanitizer.SafeForLog(auditEvent.CorrelationId));
            throw;
        }
    }

    public async Task<IReadOnlyList<PersonalRepAssociation>> ListAssociationsForMemberAsync(
        string tenantId, string memberId, bool activeOnly = false, DateTime? asOf = null,
        CancellationToken ct = default)
    {
        var filter = Builders<PersonalRepAssociation>.Filter.Eq(a => a.TenantId, tenantId)
                   & Builders<PersonalRepAssociation>.Filter.Eq(a => a.MemberId, memberId)
                   & Builders<PersonalRepAssociation>.Filter.Eq(a => a.Direction, AssociationDirection.MemberToRep)
                   & Builders<PersonalRepAssociation>.Filter.Eq(a => a.DeletedAt, (DateTime?)null);

        var results = await _associations.Find(filter).ToListAsync(ct);

        var t = asOf ?? DateTime.UtcNow;
        if (activeOnly)
            results = results.Where(a =>
                a.EffectiveFrom <= t && (a.EffectiveTo == null || a.EffectiveTo > t)).ToList();

        return results;
    }

    public async Task<IReadOnlyList<PersonalRepAssociation>> ListAssociationsForRepAsync(
        string tenantId, string repId, bool activeOnly = false, DateTime? asOf = null,
        CancellationToken ct = default)
    {
        var filter = Builders<PersonalRepAssociation>.Filter.Eq(a => a.TenantId, tenantId)
                   & Builders<PersonalRepAssociation>.Filter.Eq(a => a.RepId, repId)
                   & Builders<PersonalRepAssociation>.Filter.Eq(a => a.Direction, AssociationDirection.RepToMember)
                   & Builders<PersonalRepAssociation>.Filter.Eq(a => a.DeletedAt, (DateTime?)null);

        var results = await _associations.Find(filter).ToListAsync(ct);

        var t = asOf ?? DateTime.UtcNow;
        if (activeOnly)
            results = results.Where(a =>
                a.EffectiveFrom <= t && (a.EffectiveTo == null || a.EffectiveTo > t)).ToList();

        return results;
    }

    public async Task<PersonalRepAssociation?> FindActiveAssociationAsync(
        string tenantId, string repId, string memberId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var fb = Builders<PersonalRepAssociation>.Filter;
        var filter = fb.Eq(a => a.TenantId, tenantId)
                   & fb.Eq(a => a.RepId, repId)
                   & fb.Eq(a => a.MemberId, memberId)
                   & fb.Eq(a => a.Direction, AssociationDirection.RepToMember)
                   & fb.Eq(a => a.DeletedAt, (DateTime?)null)
                   & (fb.Eq(a => a.EffectiveTo, (DateTime?)null) | fb.Gt(a => a.EffectiveTo, now));
        return await _associations.Find(filter).FirstOrDefaultAsync(ct);
    }

    private static void EnsureSameTenant(PersonalRepAssociation a, PersonalRepAssociation b)
    {
        if (!string.Equals(a.TenantId, b.TenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "PersonalRepAssociation pair rows must share the same TenantId. " +
                "Cross-tenant representatives are not supported.");
        }
    }
}
