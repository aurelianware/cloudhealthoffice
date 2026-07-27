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
        await _collection.InsertOneAsync(plan);
        return plan;
    }

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

        var filter = Builders<BenefitPlan>.Filter.And(
            Builders<BenefitPlan>.Filter.Eq(x => x.Id, plan.Id),
            Builders<BenefitPlan>.Filter.Eq(x => x.TenantId, plan.TenantId)
        );
        await _collection.ReplaceOneAsync(filter, plan);
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

        var filter = Builders<BenefitPlan>.Filter.And(
            Builders<BenefitPlan>.Filter.Eq(x => x.Id, draft.Id),
            Builders<BenefitPlan>.Filter.Eq(x => x.TenantId, draft.TenantId));
        await _collection.ReplaceOneAsync(filter, draft);
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

        var publishFilter = Builders<BenefitPlan>.Filter.And(
            Builders<BenefitPlan>.Filter.Eq(x => x.Id, draftToPublish.Id),
            Builders<BenefitPlan>.Filter.Eq(x => x.TenantId, draftToPublish.TenantId));

        // Try a session transaction (replica set required); fall back to
        // sequential writes when the deployment is a single-node Mongo
        // instance — log a compensating-action warning so ops can spot it.
        try
        {
            using var session = await _database.Client.StartSessionAsync();
            session.StartTransaction();
            try
            {
                await _collection.ReplaceOneAsync(session, publishFilter, draftToPublish);

                if (predecessor != null)
                {
                    var predFilter = Builders<BenefitPlan>.Filter.And(
                        Builders<BenefitPlan>.Filter.Eq(x => x.Id, predecessor.Id),
                        Builders<BenefitPlan>.Filter.Eq(x => x.TenantId, predecessor.TenantId));
                    await _collection.ReplaceOneAsync(session, predFilter, predecessor);
                }

                await session.CommitTransactionAsync();
                return draftToPublish;
            }
            catch
            {
                await session.AbortTransactionAsync();
                throw;
            }
        }
        catch (NotSupportedException)
        {
            return await PublishAndSupersedeWithoutTransactionAsync(draftToPublish, predecessor, publishFilter);
        }
        catch (MongoCommandException ex) when (
            ex.CodeName == "IllegalOperation" || ex.Code == 20 || ex.Code == 263)
        {
            // Mongo errors when transactions aren't supported on the deployment.
            return await PublishAndSupersedeWithoutTransactionAsync(draftToPublish, predecessor, publishFilter);
        }
    }

    private async Task<BenefitPlan> PublishAndSupersedeWithoutTransactionAsync(
        BenefitPlan draftToPublish,
        BenefitPlan? predecessor,
        FilterDefinition<BenefitPlan> publishFilter)
    {
        _logger.LogWarning(
            "Mongo deployment does not support transactions; publishing plan {PlanId} version {VersionId} non-atomically. " +
            "Operators must verify the predecessor was superseded after the call.",
            draftToPublish.PlanId, draftToPublish.VersionId);

        await _collection.ReplaceOneAsync(publishFilter, draftToPublish);

        if (predecessor != null)
        {
            var predFilter = Builders<BenefitPlan>.Filter.And(
                Builders<BenefitPlan>.Filter.Eq(x => x.Id, predecessor.Id),
                Builders<BenefitPlan>.Filter.Eq(x => x.TenantId, predecessor.TenantId));
            await _collection.ReplaceOneAsync(predFilter, predecessor);
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
            .Set(x => x.ModifiedDate, DateTime.UtcNow);

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
        var filter = Builders<BenefitPlan>.Filter.And(
            Builders<BenefitPlan>.Filter.Eq(x => x.Id, version.Id),
            Builders<BenefitPlan>.Filter.Eq(x => x.TenantId, version.TenantId));
        var result = await _collection.ReplaceOneAsync(filter, version);
        return result.MatchedCount > 0;
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
