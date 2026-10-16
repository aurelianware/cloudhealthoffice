using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MemberService.Models;
using MemberService.Services;
using MongoDB.Driver;

namespace MemberService.Repositories;

/// <summary>
/// MongoDB repository for <see cref="FamilyRelationship"/>. Uses multi-document
/// transactions (replica set required) to make symmetric-pair writes atomic.
///
/// Deployment requirement: MongoDB must run as a replica set. Transactions degrade
/// gracefully with a helpful error on standalone deployments — the service refuses
/// to persist a partial pair rather than silently breaking the symmetric invariant.
/// See <c>docs/migrations/family-relationships-backfill.md</c> for the Mongo
/// deployment requirement.
/// </summary>
public class FamilyRelationshipRepositoryMongo : IFamilyRelationshipRepository
{
    private readonly IMongoClient _client;
    private readonly IMongoCollection<FamilyRelationship> _collection;
    public const string CollectionName = "FamilyRelationships";

    public FamilyRelationshipRepositoryMongo(IMongoClient client, IMongoDatabase database)
    {
        _client = client;
        _collection = database.GetCollection<FamilyRelationship>(CollectionName);
    }

    public async Task CreatePairAsync(FamilyRelationship forward, FamilyRelationship inverse, CancellationToken ct = default)
    {
        if (forward.TenantId != inverse.TenantId)
            throw new InvalidOperationException("Pair rows must share TenantId.");

        forward.Revision = 0;
        inverse.Revision = 0;
        using var session = await _client.StartSessionAsync(cancellationToken: ct);
        session.StartTransaction();
        try
        {
            await _collection.InsertManyAsync(session, new[] { forward, inverse }, cancellationToken: ct);
            await session.CommitTransactionAsync(ct);
        }
        catch
        {
            await session.AbortTransactionAsync(ct);
            throw;
        }
    }

    public async Task UpdatePairAsync(FamilyRelationship forward, FamilyRelationship inverse, CancellationToken ct = default)
    {
        if (forward.TenantId != inverse.TenantId)
            throw new InvalidOperationException("Pair rows must share TenantId.");

        // Each row is replaced only while it still has the revision it was read
        // with: a concurrent update of the pair in between makes this one abort
        // (neither row written) instead of silently overwriting it.
        var forwardExpected = forward.Revision;
        var inverseExpected = inverse.Revision;
        using var session = await _client.StartSessionAsync(cancellationToken: ct);
        session.StartTransaction();
        try
        {
            await ReplaceIfUnchangedAsync(session, forward, ct);
            await ReplaceIfUnchangedAsync(session, inverse, ct);
            await session.CommitTransactionAsync(ct);
        }
        catch
        {
            forward.Revision = forwardExpected;
            inverse.Revision = inverseExpected;
            await session.AbortTransactionAsync(ct);
            throw;
        }
    }

    /// <summary>The document still has the revision the write was based on (0 matches documents written before the field existed).</summary>
    private static FilterDefinition<FamilyRelationship> RevisionIs(long expected)
    {
        var b = Builders<FamilyRelationship>.Filter;
        return expected == 0
            ? b.Or(b.Eq(x => x.Revision, 0L), b.Exists(x => x.Revision, false))
            : b.Eq(x => x.Revision, expected);
    }

    private async Task ReplaceIfUnchangedAsync(IClientSessionHandle session, FamilyRelationship row, CancellationToken ct)
    {
        var filter = Builders<FamilyRelationship>.Filter.Eq(x => x.Id, row.Id) &
                     Builders<FamilyRelationship>.Filter.Eq(x => x.TenantId, row.TenantId);
        var expected = row.Revision;
        row.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(session, filter & RevisionIs(expected), row, cancellationToken: ct);
        }
        catch (MongoException ex) when (IsWriteConflict(ex))
        {
            // Another transaction is writing this row right now: the same conflict.
            row.Revision = expected;
            throw new FamilyRelationshipConcurrencyException(row.Id, expected, ex);
        }

        if (result.MatchedCount == 0)
        {
            row.Revision = expected;
            if (await _collection.Find(session, filter).AnyAsync(ct))
                throw new FamilyRelationshipConcurrencyException(row.Id, expected);
            // Missing: unchanged behaviour (nothing written for this row, no error).
        }
    }

    private const int WriteConflictCode = 112;

    private static bool IsWriteConflict(MongoException ex) => ex switch
    {
        MongoCommandException c => c.Code == WriteConflictCode,
        MongoWriteException w => w.WriteError?.Code == WriteConflictCode,
        _ => false,
    };

    public async Task<FamilyRelationship?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var filter = Builders<FamilyRelationship>.Filter.Eq(x => x.Id, id) &
                     Builders<FamilyRelationship>.Filter.Eq(x => x.TenantId, tenantId);
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<List<FamilyRelationship>> GetPairAsync(string tenantId, string pairId, CancellationToken ct = default)
    {
        var filter = Builders<FamilyRelationship>.Filter.Eq(x => x.TenantId, tenantId) &
                     Builders<FamilyRelationship>.Filter.Eq(x => x.PairId, pairId);
        return await _collection.Find(filter).ToListAsync(ct);
    }

    public async Task<List<FamilyRelationship>> ListBySubjectAsync(
        string tenantId, string subjectMemberId, bool includeDeleted = false, CancellationToken ct = default)
    {
        var f = Builders<FamilyRelationship>.Filter.Eq(x => x.TenantId, tenantId) &
                Builders<FamilyRelationship>.Filter.Eq(x => x.SubjectMemberId, subjectMemberId);
        if (!includeDeleted)
            f &= Builders<FamilyRelationship>.Filter.Eq(x => x.DeletedAt, (DateTime?)null);
        return await _collection.Find(f).ToListAsync(ct);
    }

    public async Task<List<FamilyRelationship>> ListTouchingAsync(
        string tenantId, string memberId, bool includeDeleted = false, CancellationToken ct = default)
    {
        var fb = Builders<FamilyRelationship>.Filter;
        var f = fb.Eq(x => x.TenantId, tenantId) &
                (fb.Eq(x => x.SubjectMemberId, memberId) | fb.Eq(x => x.RelatedMemberId, memberId));
        if (!includeDeleted)
            f &= fb.Eq(x => x.DeletedAt, (DateTime?)null);
        return await _collection.Find(f).ToListAsync(ct);
    }

    public async Task<FamilyRelationship?> FindActivePairAsync(
        string tenantId, string subjectMemberId, string relatedMemberId, CancellationToken ct = default)
    {
        // Active = not soft-deleted AND (no end date OR end date is still in the future).
        // Matches FamilyRelationship.IsActive; a future EndDate must still block duplicates.
        var now = DateTime.UtcNow;
        var fb = Builders<FamilyRelationship>.Filter;
        var f = fb.Eq(x => x.TenantId, tenantId) &
                fb.Eq(x => x.SubjectMemberId, subjectMemberId) &
                fb.Eq(x => x.RelatedMemberId, relatedMemberId) &
                fb.Eq(x => x.DeletedAt, (DateTime?)null) &
                (fb.Eq(x => x.EndDate, (DateTime?)null) | fb.Gt(x => x.EndDate, now));
        return await _collection.Find(f).FirstOrDefaultAsync(ct);
    }
}
