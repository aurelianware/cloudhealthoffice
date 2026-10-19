using BenefitPlanService.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BenefitPlanService.Repositories;

public class BenefitPlanRepositoryMongo : IBenefitPlanRepository
{
    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<BenefitPlan> _collection;
    private readonly ILogger<BenefitPlanRepositoryMongo> _logger;

    public BenefitPlanRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<BenefitPlanRepositoryMongo> logger)
    {
        var collectionName = configuration["CosmosDb:ContainerName"] ?? "BenefitPlans";
        _database = database;
        _collection = database.GetCollection<BenefitPlan>(collectionName);
        _logger = logger;
    }

    public async Task<BenefitPlan?> GetByIdAsync(string id, string tenantId)
    {
        var filter = Builders<BenefitPlan>.Filter.And(
            Builders<BenefitPlan>.Filter.Eq(x => x.Id, id),
            Builders<BenefitPlan>.Filter.Eq(x => x.TenantId, tenantId)
        );
        var doc = await _collection.Find(filter).FirstOrDefaultAsync();
        return doc == null ? null : Hydrate(doc);
    }

    public Task<BenefitPlan?> GetByPlanIdAsync(string planId, string tenantId)
        => GetLatestPublishedAsync(planId, tenantId, DateTime.UtcNow);

    public async Task<BenefitPlan?> GetLatestPublishedAsync(string planId, string tenantId, DateTime asOf)
    {
        var b = Builders<BenefitPlan>.Filter;

        // Legacy rows lack versionState entirely. Match either Published
        // explicitly or rows where the field is absent.
        var stateFilter = b.Or(
            b.Eq(x => x.VersionState, PlanVersionState.Published),
            b.Exists(x => x.VersionState, false));

        var filter = b.And(
            b.Eq(x => x.PlanId, planId),
            b.Eq(x => x.TenantId, tenantId),
            stateFilter,
            b.Lte(x => x.EffectiveDate, asOf),
            b.Or(
                b.Eq(x => x.TerminationDate, null),
                b.Gte(x => x.TerminationDate, asOf)));

        var doc = await _collection.Find(filter)
            .SortByDescending(x => x.VersionNumber)
            .FirstOrDefaultAsync();
        return doc == null ? null : Hydrate(doc);
    }

    public async Task<BenefitPlan?> GetVersionAsync(string planId, string versionId, string tenantId)
    {
        var b = Builders<BenefitPlan>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.PlanId, planId),
            b.Eq(x => x.VersionId, versionId));
        var doc = await _collection.Find(filter).FirstOrDefaultAsync();
        return doc == null ? null : Hydrate(doc);
    }

    public async Task<(IReadOnlyList<BenefitPlan> Items, string? ContinuationToken)> ListVersionsAsync(
        string planId, string tenantId, int pageSize, string? continuationToken)
    {
        var skip = 0;
        if (!string.IsNullOrEmpty(continuationToken) &&
            int.TryParse(continuationToken, out var parsed) && parsed > 0)
        {
            skip = parsed;
        }

        var b = Builders<BenefitPlan>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.PlanId, planId));

        var docs = await _collection.Find(filter)
            .SortByDescending(x => x.VersionNumber)
            .Skip(skip)
            .Limit(pageSize + 1) // peek one extra to know whether to emit a continuation
            .ToListAsync();

        string? next = null;
        if (docs.Count > pageSize)
        {
            docs.RemoveAt(docs.Count - 1);
            next = (skip + pageSize).ToString();
        }

        var hydrated = docs.Select(Hydrate).ToList();
        return (hydrated, next);
    }

    public async Task<IEnumerable<BenefitPlan>> SearchAsync(
        string tenantId,
        string? lineOfBusiness,
        string? planType,
        string? metalLevel,
        int page,
        int pageSize)
    {
        var filter = Builders<BenefitPlan>.Filter.Eq(x => x.TenantId, tenantId);

        if (!string.IsNullOrEmpty(lineOfBusiness) && Enum.TryParse<LineOfBusiness>(lineOfBusiness, true, out var lob))
        {
            filter &= Builders<BenefitPlan>.Filter.Eq(x => x.LineOfBusiness, lob);
        }

        if (!string.IsNullOrEmpty(planType) && Enum.TryParse<PlanType>(planType, true, out var pt))
        {
            filter &= Builders<BenefitPlan>.Filter.Eq(x => x.PlanType, pt);
        }

        if (!string.IsNullOrEmpty(metalLevel) && Enum.TryParse<MetalLevel>(metalLevel, true, out var ml))
        {
            filter &= Builders<BenefitPlan>.Filter.Eq(x => x.MetalLevel, ml);
        }

        var docs = await _collection.Find(filter)
            .SortBy(x => x.PlanName)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
        return docs.Select(Hydrate).ToList();
    }

    public async Task<IEnumerable<Benefit>> GetBenefitsAsync(string planId, string tenantId, string? serviceCategory)
    {
        var plan = await GetByPlanIdAsync(planId, tenantId);
        if (plan == null || plan.Benefits == null)
        {
            return Enumerable.Empty<Benefit>();
        }

        var benefits = plan.Benefits.AsEnumerable();

        if (!string.IsNullOrEmpty(serviceCategory))
        {
            benefits = benefits.Where(b => b.ServiceCategory == serviceCategory);
        }

        return benefits;
    }

    public async Task<BenefitPlan> CreateAsync(BenefitPlan plan)
    {
        plan.Id ??= Guid.NewGuid().ToString();
        plan.Revision = 0;
        await _collection.InsertOneAsync(plan);
        return plan;
    }

    private static FilterDefinition<BenefitPlan> RowIs(BenefitPlan plan) =>
        Builders<BenefitPlan>.Filter.And(
            Builders<BenefitPlan>.Filter.Eq(x => x.Id, plan.Id),
            Builders<BenefitPlan>.Filter.Eq(x => x.TenantId, plan.TenantId));

    /// <summary>The document still has the revision the write was based on (0 matches documents written before the field existed).</summary>
    private static FilterDefinition<BenefitPlan> RevisionIs(long expected)
    {
        var b = Builders<BenefitPlan>.Filter;
        return expected == 0
            ? b.Or(b.Eq(x => x.Revision, 0L), b.Exists(x => x.Revision, false))
            : b.Eq(x => x.Revision, expected);
    }

    /// <summary>
    /// The row is still a real Draft (a Draft-valued row without a
    /// <c>VersionId</c> is a legacy row that <see cref="Hydrate"/> reads as
    /// Published).
    /// </summary>
    private static FilterDefinition<BenefitPlan> IsDraft()
    {
        var b = Builders<BenefitPlan>.Filter;
        return b.And(
            b.Eq(x => x.VersionState, PlanVersionState.Draft),
            b.Nin(x => x.VersionId, new string?[] { null, string.Empty }));
    }

    /// <summary>The row is still Published, including legacy rows that <see cref="Hydrate"/> reads as Published.</summary>
    private static FilterDefinition<BenefitPlan> IsPublished()
    {
        var b = Builders<BenefitPlan>.Filter;
        return b.Or(
            b.Eq(x => x.VersionState, PlanVersionState.Published),
            b.Exists(x => x.VersionState, false),
            b.In(x => x.VersionId, new string?[] { null, string.Empty }));
    }

    /// <summary>
    /// A conditional replace matched nothing: a conflict when the row still
    /// exists (another write changed its revision or state), otherwise
    /// <paramref name="notFound"/>.
    /// </summary>
    private async Task<Exception> NoMatchAsync(IClientSessionHandle? session, BenefitPlan plan, long expected, Func<Exception> notFound)
    {
        var exists = session == null
            ? await _collection.Find(RowIs(plan)).AnyAsync()
            : await _collection.Find(session, RowIs(plan)).AnyAsync();
        return exists ? new BenefitPlanConcurrencyException(plan.Id, expected) : notFound();
    }

    private static PlanVersionStateException VersionNotFound(BenefitPlan plan) =>
        new(plan.PlanId, plan.VersionId, plan.VersionState, $"Version {plan.VersionId} not found") { IsNotFound = true };

    /// <summary>A MongoDB write conflict: another transaction wrote the same document first.</summary>
    private static bool IsWriteConflict(Exception ex) =>
        ex is MongoCommandException { Code: 112 }
        || ex is MongoWriteException { WriteError.Code: 112 };

    public async Task<BenefitPlan> UpdateAsync(BenefitPlan plan)
    {
        var existing = await GetByIdAsync(plan.Id, plan.TenantId);
        if (existing != null && existing.VersionState == PlanVersionState.Published)
        {
            throw new PlanVersionStateException(
                existing.PlanId, existing.VersionId, existing.VersionState,
                $"Plan version {existing.VersionId} is Published and cannot be updated. Create an amendment via POST /amend.");
        }
        if (existing != null && existing.VersionState == PlanVersionState.Superseded)
        {
            throw new PlanVersionStateException(
                existing.PlanId, existing.VersionId, existing.VersionState,
                $"Plan version {existing.VersionId} is Superseded and is read-only.");
        }

        // Conditional on the state the guard above approved (only a Draft gets
        // here) and on the revision the caller read: a publish or another edit
        // in between makes this one fail instead of overwriting a Published
        // version or the other edit.
        var expected = plan.Revision;
        plan.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(RowIs(plan) & IsDraft() & RevisionIs(expected), plan);
        }
        catch
        {
            plan.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            plan.Revision = expected;
            // A missing row stays a silent no-op, as before.
            if (await _collection.Find(RowIs(plan)).AnyAsync())
                throw new BenefitPlanConcurrencyException(plan.Id, expected);
        }
        return plan;
    }

    public async Task DeleteAsync(string id, string tenantId)
    {
        var filter = Builders<BenefitPlan>.Filter.And(
            Builders<BenefitPlan>.Filter.Eq(x => x.Id, id),
            Builders<BenefitPlan>.Filter.Eq(x => x.TenantId, tenantId)
        );
        await _collection.DeleteOneAsync(filter);
    }

    public async Task<BenefitPlan> CreateDraftAsync(BenefitPlan draft)
    {
        draft.Id = string.IsNullOrEmpty(draft.Id) ? Guid.NewGuid().ToString() : draft.Id;
        draft.VersionState = PlanVersionState.Draft;
        draft.CreatedDate = DateTime.UtcNow;
        draft.ModifiedDate = DateTime.UtcNow;
        draft.Revision = 0;
        await _collection.InsertOneAsync(draft);
        return draft;
    }

    public async Task<BenefitPlan> UpdateDraftAsync(BenefitPlan draft)
    {
        var existing = await GetByIdAsync(draft.Id, draft.TenantId)
            ?? throw new PlanVersionStateException(draft.PlanId, draft.VersionId, PlanVersionState.Draft,
                $"Draft {draft.VersionId} not found") { IsNotFound = true };

        if (existing.VersionState != PlanVersionState.Draft)
        {
            throw new PlanVersionStateException(
                existing.PlanId, existing.VersionId, existing.VersionState,
                $"Plan version {existing.VersionId} is {existing.VersionState} and cannot be edited.");
        }

        draft.ModifiedDate = DateTime.UtcNow;
        draft.VersionState = PlanVersionState.Draft;

        // Conditional on the row still being a Draft at the revision the
        // caller read: an edit that lands after the draft was published (or
        // after another edit) fails instead of turning the Published version
        // back into a Draft or erasing the other edit.
        var expected = draft.Revision;
        draft.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(RowIs(draft) & IsDraft() & RevisionIs(expected), draft);
        }
        catch
        {
            draft.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            draft.Revision = expected;
            throw await NoMatchAsync(null, draft, expected, () => new PlanVersionStateException(
                draft.PlanId, draft.VersionId, PlanVersionState.Draft, $"Draft {draft.VersionId} not found") { IsNotFound = true });
        }
        return draft;
    }

    public async Task<BenefitPlan> PublishAndSupersedeAsync(BenefitPlan draftToPublish, BenefitPlan? predecessor)
    {
        if (draftToPublish.VersionState != PlanVersionState.Published)
        {
            throw new InvalidOperationException(
                "PublishAndSupersedeAsync expects draftToPublish to already have VersionState=Published applied by the service layer.");
        }

        draftToPublish.ModifiedDate = DateTime.UtcNow;
        if (predecessor != null) predecessor.ModifiedDate = DateTime.UtcNow;

        // Each write is conditional on the state the service checked on its
        // read (the draft is still a Draft, the predecessor still Published)
        // and on the revision it read. Of two concurrent publishes of the same
        // draft, or of two drafts amending the same Published version, only one
        // can match; the other fails with a conflict instead of both
        // succeeding.
        var draftExpected = draftToPublish.Revision;
        var predExpected = predecessor?.Revision ?? 0;
        var publishFilter = RowIs(draftToPublish) & IsDraft() & RevisionIs(draftExpected);
        var predFilter = predecessor == null ? null : RowIs(predecessor) & IsPublished() & RevisionIs(predExpected);

        draftToPublish.Revision = draftExpected + 1;
        if (predecessor != null) predecessor.Revision = predExpected + 1;
        try
        {
            return await PublishAndSupersedeCoreAsync(draftToPublish, draftExpected, publishFilter, predecessor, predExpected, predFilter);
        }
        catch
        {
            draftToPublish.Revision = draftExpected;
            if (predecessor != null) predecessor.Revision = predExpected;
            throw;
        }
    }

    private async Task<BenefitPlan> PublishAndSupersedeCoreAsync(
        BenefitPlan draftToPublish, long draftExpected, FilterDefinition<BenefitPlan> publishFilter,
        BenefitPlan? predecessor, long predExpected, FilterDefinition<BenefitPlan>? predFilter)
    {
        // Try a session transaction (replica set required); fall back to
        // sequential writes when the deployment is a single-node Mongo
        // instance — log a compensating-action warning so ops can spot it.
        try
        {
            using var session = await _database.Client.StartSessionAsync();
            session.StartTransaction();
            try
            {
                var published = await _collection.ReplaceOneAsync(session, publishFilter, draftToPublish);
                if (published.MatchedCount == 0)
                    throw await NoMatchAsync(session, draftToPublish, draftExpected, () => VersionNotFound(draftToPublish));

                if (predecessor != null)
                {
                    var superseded = await _collection.ReplaceOneAsync(session, predFilter!, predecessor);
                    if (superseded.MatchedCount == 0)
                        throw await NoMatchAsync(session, predecessor, predExpected, () => VersionNotFound(predecessor));
                }

                await session.CommitTransactionAsync();
                return draftToPublish;
            }
            catch (Exception ex) when (IsWriteConflict(ex))
            {
                // Another transaction wrote one of the two rows first.
                await session.AbortTransactionAsync();
                throw new BenefitPlanConcurrencyException(draftToPublish.Id, draftExpected, ex);
            }
            catch
            {
                await session.AbortTransactionAsync();
                throw;
            }
        }
        catch (NotSupportedException)
        {
            return await PublishAndSupersedeWithoutTransactionAsync(draftToPublish, draftExpected, publishFilter, predecessor, predExpected, predFilter);
        }
        catch (MongoCommandException ex) when (
            ex.CodeName == "IllegalOperation" || ex.Code == 20 || ex.Code == 263)
        {
            // Mongo errors when transactions aren't supported on the deployment.
            return await PublishAndSupersedeWithoutTransactionAsync(draftToPublish, draftExpected, publishFilter, predecessor, predExpected, predFilter);
        }
    }

    private async Task<BenefitPlan> PublishAndSupersedeWithoutTransactionAsync(
        BenefitPlan draftToPublish, long draftExpected, FilterDefinition<BenefitPlan> publishFilter,
        BenefitPlan? predecessor, long predExpected, FilterDefinition<BenefitPlan>? predFilter)
    {
        _logger.LogWarning(
            "Mongo deployment does not support transactions; publishing plan {PlanId} version {VersionId} non-atomically. " +
            "Operators must verify the predecessor was superseded after the call.",
            draftToPublish.PlanId, draftToPublish.VersionId);

        if (predecessor == null)
        {
            var genesis = await _collection.ReplaceOneAsync(publishFilter, draftToPublish);
            if (genesis.MatchedCount == 0)
                throw await NoMatchAsync(null, draftToPublish, draftExpected, () => VersionNotFound(draftToPublish));
            return draftToPublish;
        }

        // Without a transaction the predecessor is superseded first: its
        // conditional write decides which of two concurrent publishes wins, so
        // the loser stops before writing anything and the chain never has two
        // Published heads. The stored predecessor is kept so that, if the
        // draft write then fails, the supersede can be put back.
        var before = await _collection.Find(predFilter!).FirstOrDefaultAsync();
        var superseded = before == null
            ? null
            : await _collection.ReplaceOneAsync(predFilter!, predecessor);
        if (superseded == null || superseded.MatchedCount == 0)
            throw await NoMatchAsync(null, predecessor, predExpected, () => VersionNotFound(predecessor));

        var published = await _collection.ReplaceOneAsync(publishFilter, draftToPublish);
        if (published.MatchedCount == 0)
        {
            before!.Revision = predExpected + 2;
            var restored = await _collection.ReplaceOneAsync(
                RowIs(predecessor) & RevisionIs(predExpected + 1), before);
            if (restored.MatchedCount == 0)
            {
                _logger.LogError(
                    "Publishing plan {PlanId} version {VersionId} failed after its predecessor {PredecessorVersionId} was superseded, and the predecessor could not be restored; the plan has no Published version until it is repaired.",
                    draftToPublish.PlanId, draftToPublish.VersionId, predecessor.VersionId);
            }
            throw await NoMatchAsync(null, draftToPublish, draftExpected, () => VersionNotFound(draftToPublish));
        }

        return draftToPublish;
    }

    public async Task<bool> UpdateNetworkTiersAsync(
        string tenantId,
        string planId,
        IReadOnlyList<NetworkTier> tiers,
        CancellationToken ct = default)
    {
        // $set on the NetworkTiers collection only — bypasses the
        // version-state guard on UpdateAsync. Targets the head
        // Published version of the chain. Legacy-row hydration rule
        // mirrors GetLatestPublishedAsync (versionState = Published OR
        // missing) plus effective-window guards.
        //
        // FindOneAndUpdate lets us sort by VersionNumber desc within
        // the same round-trip as the patch — same idiom as
        // ProviderRepositoryMongo.UpdateIntegrityProjectionAsync.
        var asOf = DateTime.UtcNow;
        var b = Builders<BenefitPlan>.Filter;
        var stateFilter = b.Or(
            b.Eq(x => x.VersionState, PlanVersionState.Published),
            b.Exists(x => x.VersionState, false));
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.PlanId, planId),
            stateFilter,
            b.Lte(x => x.EffectiveDate, asOf),
            b.Or(
                b.Eq(x => x.TerminationDate, null),
                b.Gte(x => x.TerminationDate, asOf)));

        var update = Builders<BenefitPlan>.Update
            .Set(x => x.NetworkTiers, tiers.ToList())
            .Set(x => x.ModifiedDate, DateTime.UtcNow)
            // Bumped: a supersede or termination based on a read from before
            // the backfill then conflicts instead of erasing the new tiers.
            .Inc(x => x.Revision, 1L);

        var options = new FindOneAndUpdateOptions<BenefitPlan>
        {
            Sort = Builders<BenefitPlan>.Sort.Descending(x => x.VersionNumber),
            ReturnDocument = ReturnDocument.After,
        };
        var updated = await _collection.FindOneAndUpdateAsync(filter, update, options, ct);
        return updated != null;
    }

    public async Task<bool> TerminateVersionAsync(BenefitPlan version)
    {
        if (version.VersionState != PlanVersionState.Superseded)
        {
            throw new InvalidOperationException(
                "TerminateVersionAsync expects version to already have VersionState=Superseded applied by the service layer.");
        }

        version.ModifiedDate = DateTime.UtcNow;

        // Conditional on the row still being Published at the revision the
        // service read: a termination racing a publish that superseded the
        // same version (or another termination) fails instead of overwriting
        // the supersede.
        var expected = version.Revision;
        version.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(RowIs(version) & IsPublished() & RevisionIs(expected), version);
        }
        catch
        {
            version.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            version.Revision = expected;
            // A missing row is still "not found" (false), not a conflict.
            if (await _collection.Find(RowIs(version)).AnyAsync())
                throw new BenefitPlanConcurrencyException(version.Id, expected);
            return false;
        }
        return true;
    }

    private static BenefitPlan Hydrate(BenefitPlan plan)
    {
        if (string.IsNullOrEmpty(plan.VersionId))
        {
            plan.VersionId = plan.Id;
            plan.VersionNumber = plan.VersionNumber <= 0 ? 1 : plan.VersionNumber;
            plan.VersionState = PlanVersionState.Published;
        }
        return plan;
    }
}
