using Microsoft.Azure.Cosmos;
using BenefitPlanService.Models;

namespace BenefitPlanService.Repositories;

/// <summary>
/// Storage seam for the benefit-plan version chain. Each row in the
/// underlying collection is one immutable version. Default reads (the
/// non-version-aware overloads kept for backward compatibility) resolve
/// to the latest <see cref="PlanVersionState.Published"/> version
/// effective today.
/// </summary>
public interface IBenefitPlanRepository
{
    Task<BenefitPlan?> GetByIdAsync(string id, string tenantId);

    /// <summary>
    /// Backward-compat: returns the latest <see cref="PlanVersionState.Published"/>
    /// version of <paramref name="planId"/> in effect right now. New code
    /// should call <see cref="GetLatestPublishedAsync"/> with an explicit
    /// <c>asOf</c> instead.
    /// </summary>
    Task<BenefitPlan?> GetByPlanIdAsync(string planId, string tenantId);

    Task<IEnumerable<BenefitPlan>> SearchAsync(string tenantId, string? lineOfBusiness, string? planType, string? metalLevel, int page, int pageSize);
    Task<IEnumerable<Benefit>> GetBenefitsAsync(string planId, string tenantId, string? serviceCategory);
    Task<BenefitPlan> CreateAsync(BenefitPlan plan);
    Task<BenefitPlan> UpdateAsync(BenefitPlan plan);
    Task DeleteAsync(string id, string tenantId);

    // ---- Version-chain operations ------------------------------------

    /// <summary>
    /// Latest <see cref="PlanVersionState.Published"/> version of
    /// <paramref name="planId"/> whose effective window contains
    /// <paramref name="asOf"/>. Returns null when no such version exists.
    /// </summary>
    Task<BenefitPlan?> GetLatestPublishedAsync(string planId, string tenantId, DateTime asOf);

    /// <summary>Look up a single version by <c>VersionId</c>.</summary>
    Task<BenefitPlan?> GetVersionAsync(string planId, string versionId, string tenantId);

    /// <summary>
    /// Newest-first list of every version for <paramref name="planId"/>,
    /// paginated with a continuation token.
    /// </summary>
    Task<(IReadOnlyList<BenefitPlan> Items, string? ContinuationToken)> ListVersionsAsync(
        string planId, string tenantId, int pageSize, string? continuationToken);

    /// <summary>
    /// Persist a new draft. Caller is responsible for setting
    /// <c>VersionId</c>, <c>VersionNumber</c>, <c>VersionState=Draft</c>
    /// and (for amendments) <c>PredecessorVersionId</c>.
    /// </summary>
    Task<BenefitPlan> CreateDraftAsync(BenefitPlan draft);

    /// <summary>Update a Draft. Throws <see cref="PlanVersionStateException"/> if the row is not Draft.</summary>
    Task<BenefitPlan> UpdateDraftAsync(BenefitPlan draft);

    /// <summary>
    /// Atomic transition: flip <paramref name="draftToPublish"/> from Draft
    /// to Published and (if not null) flip <paramref name="predecessor"/>
    /// from Published to Superseded with
    /// <c>SupersededByVersionId = draftToPublish.VersionId</c>. Implementations
    /// use a transactional batch (Cosmos) or session transaction (Mongo)
    /// when the backend supports it; otherwise they fall back to sequential
    /// writes and log a compensating-action warning.
    /// </summary>
    Task<BenefitPlan> PublishAndSupersedeAsync(BenefitPlan draftToPublish, BenefitPlan? predecessor);

    /// <summary>
    /// Projection-metadata bypass write: replaces the
    /// <see cref="BenefitPlan.NetworkTiers"/> collection on the head
    /// Published version of <paramref name="planId"/> without going
    /// through <see cref="UpdateAsync"/>. Used by the capability 5.5
    /// network-tier <c>NetworkId</c> backfill (and only by the
    /// backfill).
    ///
    /// <para>
    /// The Cosmos impl uses <c>PatchItemAsync</c> with a single
    /// field-scoped <c>Set</c> op; the Mongo impl uses
    /// <c>FindOneAndUpdateAsync</c> with a sort on
    /// <c>VersionNumber</c> and <c>$set</c> so the head row is
    /// resolved and patched in a single round-trip. No
    /// <c>PlanVersionEvent</c> is emitted — the operation is a
    /// projection-metadata refresh, not a chain transition. See
    /// <c>docs/architecture/plan-versioning.md</c> "Projection
    /// metadata — exempt from versioning".
    /// </para>
    ///
    /// <para>
    /// Returns <c>true</c> when the head row was patched, <c>false</c>
    /// when no head Published row exists for the plan or the row was
    /// removed between lookup and patch (treated as a soft miss; the
    /// backfill records it under <c>not_found</c>).
    /// </para>
    /// </summary>
    Task<bool> UpdateNetworkTiersAsync(
        string tenantId,
        string planId,
        IReadOnlyList<NetworkTier> tiers,
        CancellationToken ct = default);

    /// <summary>
    /// Persists a standalone termination: <paramref name="version"/> must
    /// already have <c>VersionState=Superseded</c>, <c>SupersededAt</c> set,
    /// <c>SupersededByVersionId=null</c> (no successor -- distinguishes a
    /// terminated plan from one replaced by an amendment), and
    /// <c>IsActive=false</c>, applied by the service layer. Mirrors
    /// <see cref="PublishAndSupersedeAsync"/>'s contract of taking a fully
    /// pre-mutated object and just persisting it. Returns <c>false</c> when
    /// the row was not found.
    /// </summary>
    Task<bool> TerminateVersionAsync(BenefitPlan version);
}

/// <summary>
/// Thrown when a write violates the version-state invariants — e.g. an
/// attempt to update a Published row, to publish a non-Draft row, or to
/// reach a version that doesn't exist. The controller boundary maps
/// <see cref="IsNotFound"/> to HTTP 404 and everything else to 409.
/// </summary>
public sealed class PlanVersionStateException : InvalidOperationException
{
    public string PlanId { get; }
    public string VersionId { get; }
    public PlanVersionState CurrentState { get; }

    /// <summary>
    /// True when the underlying cause is "the requested plan/version
    /// does not exist", as opposed to a state-machine violation. Set on
    /// construction; controllers map this to HTTP 404 instead of 409.
    /// </summary>
    public bool IsNotFound { get; init; }

    public PlanVersionStateException(string planId, string versionId, PlanVersionState currentState, string message)
        : base(message)
    {
        PlanId = planId;
        VersionId = versionId;
        CurrentState = currentState;
    }
}

public class BenefitPlanRepository : IBenefitPlanRepository
{
    private readonly Container _container;
    private readonly ILogger<BenefitPlanRepository> _logger;

    public BenefitPlanRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        ILogger<BenefitPlanRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"];
        var containerName = configuration["CosmosDb:ContainerName"];
        _container = cosmosClient.GetContainer(databaseName, containerName);
        _logger = logger;
    }

    public async Task<BenefitPlan?> GetByIdAsync(string id, string tenantId)
    {
        try
        {
            var response = await _container.ReadItemAsync<BenefitPlan>(
                id,
                new PartitionKey(tenantId));
            return Hydrate(response.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task<BenefitPlan?> GetByPlanIdAsync(string planId, string tenantId)
        => GetLatestPublishedAsync(planId, tenantId, DateTime.UtcNow);

    public async Task<BenefitPlan?> GetLatestPublishedAsync(string planId, string tenantId, DateTime asOf)
    {
        // Hydration rule: rows missing versionState are treated as Published
        // (legacy data). The query below selects either Published rows or
        // rows where versionState is unset.
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.planId = @planId " +
                "AND (NOT IS_DEFINED(c.versionState) OR c.versionState = @published) " +
                "AND (NOT IS_DEFINED(c.effectiveDate) OR c.effectiveDate <= @asOf) " +
                "AND (NOT IS_DEFINED(c.terminationDate) OR c.terminationDate = null OR c.terminationDate >= @asOf) " +
                "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@planId", planId)
            .WithParameter("@published", PlanVersionState.Published.ToString())
            .WithParameter("@asOf", asOf);

        var iterator = _container.GetItemQueryIterator<BenefitPlan>(query);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            var first = page.FirstOrDefault();
            if (first != null) return Hydrate(first);
        }
        return null;
    }

    public async Task<BenefitPlan?> GetVersionAsync(string planId, string versionId, string tenantId)
    {
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.planId = @planId AND c.versionId = @versionId")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@planId", planId)
            .WithParameter("@versionId", versionId);

        var iterator = _container.GetItemQueryIterator<BenefitPlan>(query);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            var first = page.FirstOrDefault();
            if (first != null) return Hydrate(first);
        }
        return null;
    }

    public async Task<(IReadOnlyList<BenefitPlan> Items, string? ContinuationToken)> ListVersionsAsync(
        string planId, string tenantId, int pageSize, string? continuationToken)
    {
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.planId = @planId ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@planId", planId);

        var requestOptions = new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(tenantId),
            MaxItemCount = pageSize
        };

        var iterator = _container.GetItemQueryIterator<BenefitPlan>(query, continuationToken, requestOptions);
        if (!iterator.HasMoreResults)
            return (Array.Empty<BenefitPlan>(), null);

        var response = await iterator.ReadNextAsync();
        var items = response.Select(Hydrate).ToList();
        return (items, response.ContinuationToken);
    }

    public async Task<IEnumerable<BenefitPlan>> SearchAsync(
        string tenantId,
        string? lineOfBusiness,
        string? planType,
        string? metalLevel,
        int page,
        int pageSize)
    {
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId";
        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId);

        if (!string.IsNullOrEmpty(lineOfBusiness))
        {
            queryText += " AND c.lineOfBusiness = @lineOfBusiness";
            queryDef = new QueryDefinition(queryText)
                .WithParameter("@tenantId", tenantId)
                .WithParameter("@lineOfBusiness", lineOfBusiness);
        }

        if (!string.IsNullOrEmpty(planType))
        {
            queryText += " AND c.planType = @planType";
            queryDef = new QueryDefinition(queryText)
                .WithParameter("@tenantId", tenantId);
            if (!string.IsNullOrEmpty(lineOfBusiness))
                queryDef = queryDef.WithParameter("@lineOfBusiness", lineOfBusiness);
            queryDef = queryDef.WithParameter("@planType", planType);
        }

        if (!string.IsNullOrEmpty(metalLevel))
        {
            queryText += " AND c.metalLevel = @metalLevel";
            queryDef = new QueryDefinition(queryText)
                .WithParameter("@tenantId", tenantId);
            if (!string.IsNullOrEmpty(lineOfBusiness))
                queryDef = queryDef.WithParameter("@lineOfBusiness", lineOfBusiness);
            if (!string.IsNullOrEmpty(planType))
                queryDef = queryDef.WithParameter("@planType", planType);
            queryDef = queryDef.WithParameter("@metalLevel", metalLevel);
        }

        queryText += " ORDER BY c.planName OFFSET @offset LIMIT @limit";
        queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId);
        if (!string.IsNullOrEmpty(lineOfBusiness))
            queryDef = queryDef.WithParameter("@lineOfBusiness", lineOfBusiness);
        if (!string.IsNullOrEmpty(planType))
            queryDef = queryDef.WithParameter("@planType", planType);
        if (!string.IsNullOrEmpty(metalLevel))
            queryDef = queryDef.WithParameter("@metalLevel", metalLevel);
        queryDef = queryDef
            .WithParameter("@offset", (page - 1) * pageSize)
            .WithParameter("@limit", pageSize);

        var iterator = _container.GetItemQueryIterator<BenefitPlan>(queryDef);
        var results = new List<BenefitPlan>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response.Select(Hydrate));
        }

        return results;
    }

    public async Task<IEnumerable<Benefit>> GetBenefitsAsync(string planId, string tenantId, string? serviceCategory)
    {
        var plan = await GetByPlanIdAsync(planId, tenantId);
        if (plan == null)
        {
            return Enumerable.Empty<Benefit>();
        }

        var benefits = plan.Benefits ?? new List<Benefit>();

        if (!string.IsNullOrEmpty(serviceCategory))
        {
            benefits = benefits.Where(b => b.ServiceCategory == serviceCategory).ToList();
        }

        return benefits;
    }

    public async Task<BenefitPlan> CreateAsync(BenefitPlan plan)
    {
        plan.Id = string.IsNullOrEmpty(plan.Id) ? Guid.NewGuid().ToString() : plan.Id;
        plan.CreatedDate = DateTime.UtcNow;
        plan.ModifiedDate = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(
            plan,
            new PartitionKey(plan.TenantId));

        return response.Resource;
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

        plan.ModifiedDate = DateTime.UtcNow;

        var response = await _container.ReplaceItemAsync(
            plan,
            plan.Id,
            new PartitionKey(plan.TenantId));

        return response.Resource;
    }

    public async Task DeleteAsync(string id, string tenantId)
    {
        await _container.DeleteItemAsync<BenefitPlan>(
            id,
            new PartitionKey(tenantId));
    }

    public async Task<BenefitPlan> CreateDraftAsync(BenefitPlan draft)
    {
        draft.VersionState = PlanVersionState.Draft;
        draft.Id = string.IsNullOrEmpty(draft.Id) ? Guid.NewGuid().ToString() : draft.Id;
        draft.CreatedDate = DateTime.UtcNow;
        draft.ModifiedDate = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(draft, new PartitionKey(draft.TenantId));
        return response.Resource;
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

        var response = await _container.ReplaceItemAsync(draft, draft.Id, new PartitionKey(draft.TenantId));
        return response.Resource;
    }

    public async Task<BenefitPlan> PublishAndSupersedeAsync(BenefitPlan draftToPublish, BenefitPlan? predecessor)
    {
        if (draftToPublish.VersionState != PlanVersionState.Published)
        {
            throw new InvalidOperationException(
                "PublishAndSupersedeAsync expects draftToPublish to already have VersionState=Published applied by the service layer.");
        }

        draftToPublish.ModifiedDate = DateTime.UtcNow;

        var batch = _container.CreateTransactionalBatch(new PartitionKey(draftToPublish.TenantId))
            .ReplaceItem(draftToPublish.Id, draftToPublish);

        if (predecessor != null)
        {
            predecessor.ModifiedDate = DateTime.UtcNow;
            batch = batch.ReplaceItem(predecessor.Id, predecessor);
        }

        using var response = await batch.ExecuteAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new PlanVersionStateException(
                draftToPublish.PlanId, draftToPublish.VersionId, draftToPublish.VersionState,
                $"Atomic publish/supersede failed: {response.StatusCode}");
        }

        return draftToPublish;
    }

    public async Task<bool> UpdateNetworkTiersAsync(
        string tenantId,
        string planId,
        IReadOnlyList<NetworkTier> tiers,
        CancellationToken ct = default)
    {
        // Resolve the head Published row by chain key. PatchItemAsync is
        // keyed on the per-row document Id, so we look up the row id
        // first. The lookup uses the same legacy-aware predicate as
        // GetLatestPublishedAsync — versionState = Published OR missing,
        // restricted to the active effective window.
        var asOf = DateTime.UtcNow;
        var query = new QueryDefinition(
                "SELECT TOP 1 c.id FROM c WHERE c.tenantId = @tenantId AND c.planId = @planId AND " +
                "(NOT IS_DEFINED(c.versionState) OR c.versionState = @published) AND " +
                "(NOT IS_DEFINED(c.effectiveDate) OR c.effectiveDate <= @asOf) AND " +
                "(NOT IS_DEFINED(c.terminationDate) OR c.terminationDate = null OR c.terminationDate >= @asOf) " +
                "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@planId", planId)
            .WithParameter("@published", PlanVersionState.Published.ToString())
            .WithParameter("@asOf", asOf);

        string? rowId = null;
        var iterator = _container.GetItemQueryIterator<HeadIdResult>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        if (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            rowId = page.FirstOrDefault()?.Id;
        }

        if (string.IsNullOrEmpty(rowId)) return false;

        var ops = new List<PatchOperation>
        {
            PatchOperation.Set("/networkTiers", tiers),
            PatchOperation.Set("/modifiedDate", DateTime.UtcNow),
        };

        try
        {
            await _container.PatchItemAsync<BenefitPlan>(
                rowId,
                new PartitionKey(tenantId),
                ops,
                cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Row was deleted between lookup and patch.
            return false;
        }
    }

    public async Task<bool> TerminateVersionAsync(BenefitPlan version)
    {
        if (version.VersionState != PlanVersionState.Superseded)
        {
            throw new InvalidOperationException(
                "TerminateVersionAsync expects version to already have VersionState=Superseded applied by the service layer.");
        }

        version.ModifiedDate = DateTime.UtcNow;
        try
        {
            await _container.ReplaceItemAsync(version, version.Id, new PartitionKey(version.TenantId));
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// Projection of the per-row document id used by the head-row
    /// lookup in <see cref="UpdateNetworkTiersAsync"/>.
    /// </summary>
    private sealed record HeadIdResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;
    }

    private static BenefitPlan Hydrate(BenefitPlan plan)
    {
        // VersionId is always written on new docs, so an empty value
        // unambiguously identifies a legacy row that predates this feature.
        // Treat it as a single Published version for backward-compat.
        if (string.IsNullOrEmpty(plan.VersionId))
        {
            plan.VersionId = plan.Id;
            plan.VersionNumber = plan.VersionNumber <= 0 ? 1 : plan.VersionNumber;
            plan.VersionState = PlanVersionState.Published;
        }
        return plan;
    }
}
