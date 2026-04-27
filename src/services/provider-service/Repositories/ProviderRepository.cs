using Microsoft.Azure.Cosmos;
using ProviderService.Models;

namespace ProviderService.Repositories;

/// <summary>
/// Storage seam for the provider version chain. Each row in the underlying
/// collection is one immutable version. Default reads (the non-version-aware
/// overloads kept for backward compatibility) resolve to the latest
/// <see cref="ProviderVersionState.Active"/> version effective today.
/// </summary>
public interface IProviderRepository
{
    Task<Provider?> GetByIdAsync(string id);
    Task<Provider?> GetByNPIAsync(string npi);
    Task<IEnumerable<Provider>> SearchAsync(
        string? name,
        string? specialty,
        string? zipCode,
        string? state,
        string? planId,
        LineOfBusiness? lineOfBusiness,
        ProviderType? providerType,
        bool? acceptingNewPatients,
        int page,
        int pageSize);
    Task<Provider> CreateAsync(Provider provider);
    Task<Provider> UpdateAsync(Provider provider);
    Task DeleteAsync(string id);

    /// <summary>
    /// Backing query for <c>GET /api/v1/networks/{id}/roster</c>. Matches
    /// the latest non-Draft head row for each provider in the tenant that
    /// has a <see cref="NetworkParticipation"/> with
    /// <c>NetworkId == query.NetworkId</c> AND every other supplied filter
    /// AND (when <c>AsOfDate</c> is set) a participation period covering
    /// that date. Sort + paging are applied at the repository layer so
    /// the service never has to re-page.
    ///
    /// <para>
    /// <paramref name="skip"/> is the offset already decoded from the
    /// caller's cursor. The repository returns at most <c>pageSize</c>
    /// rows.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<Provider>> ListNetworkRosterAsync(
        NetworkRosterQuery query,
        NetworkRosterSort sort,
        int skip,
        CancellationToken ct = default);

    // ---- Version-chain operations ------------------------------------

    /// <summary>
    /// Latest <see cref="ProviderVersionState.Active"/> version of
    /// <paramref name="providerId"/> in effect at <paramref name="asOf"/>.
    /// Returns null when no Active version exists (terminated, suspended,
    /// or never activated).
    /// </summary>
    Task<Provider?> GetLatestActiveAsync(string providerId, DateTime asOf);

    /// <summary>Look up a single version by <c>VersionId</c>.</summary>
    Task<Provider?> GetVersionAsync(string providerId, string versionId);

    /// <summary>
    /// Newest-first list of every version for <paramref name="providerId"/>,
    /// paginated with a continuation token.
    /// </summary>
    Task<(IReadOnlyList<Provider> Items, string? ContinuationToken)> ListVersionsAsync(
        string providerId, int pageSize, string? continuationToken);

    /// <summary>
    /// Persist a new draft. Caller is responsible for setting
    /// <c>VersionId</c>, <c>VersionNumber</c>, <c>VersionState=Draft</c>
    /// and (for amendments) <c>PredecessorVersionId</c>.
    /// </summary>
    Task<Provider> CreateDraftAsync(Provider draft);

    /// <summary>Update a Draft. Throws <see cref="ProviderVersionStateException"/> if the row is not Draft.</summary>
    Task<Provider> UpdateDraftAsync(Provider draft);

    /// <summary>
    /// Atomic transition: flip <paramref name="draftToActivate"/> from Draft
    /// to Active and (if not null) flip <paramref name="predecessor"/> from
    /// Active/Suspended/Terminated to Superseded with
    /// <c>SupersededByVersionId = draftToActivate.VersionId</c>. Implementations
    /// use a transactional batch (Cosmos) or session transaction (Mongo)
    /// when the backend supports it; otherwise they fall back to sequential
    /// writes and log a compensating-action warning.
    /// </summary>
    Task<Provider> ActivateAndSupersedeAsync(Provider draftToActivate, Provider? predecessor);

    /// <summary>
    /// Persist a state-only mutation on an existing version row (Suspend
    /// or Terminate). The service layer applies the new state and
    /// timestamps before calling. Bypasses the Active-is-read-only guard
    /// in <see cref="UpdateAsync"/>.
    /// </summary>
    Task<Provider> ReplaceVersionRowAsync(Provider version);
}

/// <summary>
/// Thrown when a write violates the version-state invariants — e.g. an
/// attempt to update an Active row, to activate a non-Draft row, or to
/// reach a version that doesn't exist. The controller boundary maps
/// <see cref="IsNotFound"/> to HTTP 404 and everything else to 409.
/// </summary>
public sealed class ProviderVersionStateException : InvalidOperationException
{
    public string ProviderId { get; }
    public string VersionId { get; }
    public ProviderVersionState CurrentState { get; }

    /// <summary>
    /// True when the underlying cause is "the requested provider/version
    /// does not exist", as opposed to a state-machine violation. Set on
    /// construction; controllers map this to HTTP 404 instead of 409.
    /// </summary>
    public bool IsNotFound { get; init; }

    public ProviderVersionStateException(string providerId, string versionId, ProviderVersionState currentState, string message)
        : base(message)
    {
        ProviderId = providerId;
        VersionId = versionId;
        CurrentState = currentState;
    }
}

public class ProviderRepository : IProviderRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ProviderRepository> _logger;

    public ProviderRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ProviderRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "ProviderDB";
        var containerName = configuration["CosmosDb:ContainerName"] ?? "Providers";

        _container = cosmosClient.GetContainer(databaseName, containerName);
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new InvalidOperationException("TenantId not found in request context");
        }
        return tenantId;
    }

    public async Task<Provider?> GetByIdAsync(string id)
    {
        // Resolves the chain key (ProviderId) to the latest non-Draft row.
        // For legacy single-row chains where ProviderId is empty on disk,
        // hydration restores ProviderId = Id, so the same call returns the
        // same row it always did.
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND " +
                "(c.providerId = @id OR (NOT IS_DEFINED(c.providerId) AND c.id = @id)) AND " +
                "(NOT IS_DEFINED(c.versionState) OR c.versionState != @draft) " +
                "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@id", id)
            .WithParameter("@draft", ProviderVersionState.Draft.ToString());

        var iterator = _container.GetItemQueryIterator<Provider>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            var first = page.FirstOrDefault();
            if (first != null) return Hydrate(first);
        }
        return null;
    }

    public async Task<Provider?> GetByNPIAsync(string npi)
    {
        var tenantId = GetTenantId();

        // Skip Draft rows so NPI lookups consistently resolve to the head
        // non-Draft version (Active / Suspended / Terminated / Superseded).
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.npi = @npi AND " +
            "(NOT IS_DEFINED(c.versionState) OR c.versionState != @draft) " +
            "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@npi", npi)
            .WithParameter("@draft", ProviderVersionState.Draft.ToString());

        var iterator = _container.GetItemQueryIterator<Provider>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        var results = new List<Provider>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results.Select(Hydrate).FirstOrDefault();
    }

    public async Task<IEnumerable<Provider>> SearchAsync(
        string? name,
        string? specialty,
        string? zipCode,
        string? state,
        string? planId,
        LineOfBusiness? lineOfBusiness,
        ProviderType? providerType,
        bool? acceptingNewPatients,
        int page,
        int pageSize)
    {
        var tenantId = GetTenantId();

        // Build dynamic query
        var conditions = new List<string> { "c.tenantId = @tenantId", "c.status = 'Active'" };
        var queryDef = new QueryDefinition("SELECT * FROM c WHERE ");
        queryDef.WithParameter("@tenantId", tenantId);

        if (!string.IsNullOrEmpty(name))
        {
            conditions.Add("(CONTAINS(LOWER(c.firstName), LOWER(@name)) OR CONTAINS(LOWER(c.lastName), LOWER(@name)) OR CONTAINS(LOWER(c.organizationName), LOWER(@name)))");
            queryDef.WithParameter("@name", name);
        }

        if (!string.IsNullOrEmpty(specialty))
        {
            conditions.Add("CONTAINS(LOWER(c.primarySpecialty), LOWER(@specialty))");
            queryDef.WithParameter("@specialty", specialty);
        }

        if (!string.IsNullOrEmpty(zipCode))
        {
            conditions.Add("c.zipCode = @zipCode");
            queryDef.WithParameter("@zipCode", zipCode);
        }

        if (!string.IsNullOrEmpty(state))
        {
            conditions.Add("c.state = @state");
            queryDef.WithParameter("@state", state);
        }

        if (providerType.HasValue)
        {
            conditions.Add("c.providerType = @providerType");
            queryDef.WithParameter("@providerType", providerType.Value.ToString());
        }

        if (acceptingNewPatients.HasValue)
        {
            conditions.Add("c.acceptingNewPatients = @acceptingNewPatients");
            queryDef.WithParameter("@acceptingNewPatients", acceptingNewPatients.Value);
        }

        // Network participation filter (array search)
        if (!string.IsNullOrEmpty(planId) || lineOfBusiness.HasValue)
        {
            if (!string.IsNullOrEmpty(planId) && lineOfBusiness.HasValue)
            {
                conditions.Add("EXISTS(SELECT VALUE n FROM n IN c.networkParticipations WHERE n.planId = @planId AND n.lineOfBusiness = @lineOfBusiness)");
                queryDef.WithParameter("@planId", planId);
                queryDef.WithParameter("@lineOfBusiness", lineOfBusiness.Value.ToString());
            }
            else if (!string.IsNullOrEmpty(planId))
            {
                conditions.Add("EXISTS(SELECT VALUE n FROM n IN c.networkParticipations WHERE n.planId = @planId)");
                queryDef.WithParameter("@planId", planId);
            }
            else if (lineOfBusiness.HasValue)
            {
                conditions.Add("EXISTS(SELECT VALUE n FROM n IN c.networkParticipations WHERE n.lineOfBusiness = @lineOfBusiness)");
                queryDef.WithParameter("@lineOfBusiness", lineOfBusiness.Value.ToString());
            }
        }

        var queryText = $"SELECT * FROM c WHERE {string.Join(" AND ", conditions)} ORDER BY c.lastName, c.organizationName OFFSET {(page - 1) * pageSize} LIMIT {pageSize}";
        var finalQuery = new QueryDefinition(queryText);

        // Re-apply all parameters to final query
        foreach (var (name2, value) in queryDef.GetQueryParameters())
        {
            finalQuery.WithParameter(name2, value);
        }

        var iterator = _container.GetItemQueryIterator<Provider>(finalQuery);
        var results = new List<Provider>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results.Select(Hydrate).ToList();
    }

    public async Task<Provider> CreateAsync(Provider provider)
    {
        var tenantId = GetTenantId();
        provider.TenantId = tenantId;

        var response = await _container.CreateItemAsync(provider, new PartitionKey(tenantId));
        return response.Resource;
    }

    public async Task<Provider> UpdateAsync(Provider provider)
    {
        var tenantId = GetTenantId();
        provider.TenantId = tenantId;

        // Reject mutations on non-Draft rows. Hydration normalizes legacy
        // rows to Active, which means updates against legacy data also
        // surface 409 — callers must amend through the new draft path.
        Provider? existing;
        try
        {
            var read = await _container.ReadItemAsync<Provider>(provider.Id, new PartitionKey(tenantId));
            existing = Hydrate(read.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            existing = null;
        }

        if (existing != null && existing.VersionState != ProviderVersionState.Draft)
        {
            throw new ProviderVersionStateException(
                existing.ProviderId, existing.VersionId, existing.VersionState,
                $"Provider version {existing.VersionId} is {existing.VersionState} and cannot be updated. Create an amendment via POST /amend.");
        }

        var response = await _container.ReplaceItemAsync(
            provider,
            provider.Id,
            new PartitionKey(tenantId));
        return response.Resource;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        await _container.DeleteItemAsync<Provider>(id, new PartitionKey(tenantId));
    }

    public async Task<IReadOnlyList<Provider>> ListNetworkRosterAsync(
        NetworkRosterQuery query,
        NetworkRosterSort sort,
        int skip,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(query.TenantId))
            throw new ArgumentException("NetworkRosterQuery.TenantId is required.", nameof(query));
        if (string.IsNullOrEmpty(query.NetworkId))
            throw new ArgumentException("NetworkRosterQuery.NetworkId is required.", nameof(query));

        // Defensive clamp; the controller already enforces this. Skipping
        // negative offsets prevents accidental SQL injection via the
        // OFFSET/LIMIT literals below (we accept only int values).
        var effectivePageSize = Math.Clamp(query.PageSize, 1, NetworkRosterDefaults.MaxPageSize);
        var safeSkip = Math.Max(skip, 0);
        var asOf = (query.AsOfDate ?? DateTime.UtcNow).ToUniversalTime();

        var parameters = new List<(string Name, object Value)>
        {
            ("@tenantId", query.TenantId),
            ("@networkId", query.NetworkId),
            ("@active", ProviderVersionState.Active.ToString()),
            ("@statusActive", ProviderStatus.Active.ToString()),
            ("@asOf", asOf),
        };

        // Participation-level filters live inside an EXISTS subquery so a
        // single row matches when at least one participation satisfies
        // every supplied filter. Provider-level filters stay on the outer.
        var participationConditions = new List<string>
        {
            "n.networkId = @networkId",
            "(NOT IS_DEFINED(n.effectiveDate) OR n.effectiveDate <= @asOf)",
            "(NOT IS_DEFINED(n.terminationDate) OR n.terminationDate = null OR n.terminationDate >= @asOf)",
        };
        if (query.LineOfBusiness.HasValue)
        {
            participationConditions.Add("n.lineOfBusiness = @lineOfBusiness");
            parameters.Add(("@lineOfBusiness", query.LineOfBusiness.Value.ToString()));
        }
        if (!string.IsNullOrEmpty(query.Tier))
        {
            participationConditions.Add("n.networkTier = @tier");
            parameters.Add(("@tier", query.Tier));
        }
        if (query.AcceptingNewPatients.HasValue)
        {
            participationConditions.Add("n.acceptingNewPatients = @participationAcceptingNew");
            parameters.Add(("@participationAcceptingNew", query.AcceptingNewPatients.Value));
        }

        var existsClause =
            $"EXISTS(SELECT VALUE n FROM n IN c.networkParticipations WHERE {string.Join(" AND ", participationConditions)})";

        var conditions = new List<string>
        {
            "c.tenantId = @tenantId",
            // "Active" matches three shapes (mirrors Hydrate()):
            //   1. versionState == Active (current versioned shape)
            //   2. versionState absent (legacy)
            //   3. versionId missing/null/empty AND status == 'Active' (legacy
            //      row where versionState defaulted to enum-zero on read)
            // Without (3) these legacy rows would be wrongly excluded.
            "(c.versionState = @active OR NOT IS_DEFINED(c.versionState) " +
                "OR ((NOT IS_DEFINED(c.versionId) OR c.versionId = null OR c.versionId = \"\") AND c.status = @statusActive))",
            "(NOT IS_DEFINED(c.terminationDate) OR c.terminationDate = null OR c.terminationDate >= @asOf)",
            existsClause,
        };

        if (!string.IsNullOrEmpty(query.Specialty))
        {
            conditions.Add(
                "(CONTAINS(LOWER(c.primarySpecialty), LOWER(@specialty)) OR CONTAINS(LOWER(c.taxonomyCode), LOWER(@specialty)))");
            parameters.Add(("@specialty", query.Specialty));
        }

        if (query.AcceptingNewPatients.HasValue)
        {
            conditions.Add("c.acceptingNewPatients = @providerAcceptingNew");
            parameters.Add(("@providerAcceptingNew", query.AcceptingNewPatients.Value));
        }

        var orderBy = sort switch
        {
            NetworkRosterSort.NameDesc =>
                "ORDER BY c.lastName DESC, c.organizationName DESC, c.id DESC",
            NetworkRosterSort.IntegrityScoreDesc =>
                // Cosmos can store integrityScore as null (field present
                // but null) or absent entirely. IS_DEFINED returns 1 for
                // both cases when null; IS_NUMBER returns true only for
                // actual numeric values so providers with null or missing
                // scores get hasScore=0 and sort last — nulls-last before
                // the OFFSET/LIMIT clause.
                "ORDER BY (IS_NUMBER(c.integrityScore) ? 1 : 0) DESC, c.integrityScore DESC, c.id ASC",
            _ =>
                "ORDER BY c.lastName ASC, c.organizationName ASC, c.id ASC",
        };

        var sql =
            "SELECT * FROM c WHERE " + string.Join(" AND ", conditions) + " " +
            orderBy + " " +
            $"OFFSET {safeSkip} LIMIT {effectivePageSize}";

        var queryDef = new QueryDefinition(sql);
        foreach (var (name, value) in parameters)
        {
            queryDef = queryDef.WithParameter(name, value);
        }

        var iterator = _container.GetItemQueryIterator<Provider>(
            queryDef,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(query.TenantId),
                MaxItemCount = effectivePageSize,
            });

        var results = new List<Provider>(effectivePageSize);
        while (iterator.HasMoreResults)
        {
            ct.ThrowIfCancellationRequested();
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page.Select(Hydrate));
            if (results.Count >= effectivePageSize) break;
        }

        return results;
    }

    public async Task<Provider?> GetLatestActiveAsync(string providerId, DateTime asOf)
    {
        var tenantId = GetTenantId();

        // Hydration rule: rows missing versionState are treated as Active
        // (legacy data). The query also accepts legacy rows where
        // providerId is unset by falling back to the row's own id.
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND " +
                "(c.providerId = @providerId OR (NOT IS_DEFINED(c.providerId) AND c.id = @providerId)) AND " +
                "(NOT IS_DEFINED(c.versionState) OR c.versionState = @active) AND " +
                "(NOT IS_DEFINED(c.terminationDate) OR c.terminationDate = null OR c.terminationDate >= @asOf) " +
                "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@providerId", providerId)
            .WithParameter("@active", ProviderVersionState.Active.ToString())
            .WithParameter("@asOf", asOf);

        var iterator = _container.GetItemQueryIterator<Provider>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            var first = page.FirstOrDefault();
            if (first != null) return Hydrate(first);
        }
        return null;
    }

    public async Task<Provider?> GetVersionAsync(string providerId, string versionId)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND " +
                "(c.providerId = @providerId OR (NOT IS_DEFINED(c.providerId) AND c.id = @providerId)) AND " +
                "c.versionId = @versionId")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@providerId", providerId)
            .WithParameter("@versionId", versionId);

        var iterator = _container.GetItemQueryIterator<Provider>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            var first = page.FirstOrDefault();
            if (first != null) return Hydrate(first);
        }
        return null;
    }

    public async Task<(IReadOnlyList<Provider> Items, string? ContinuationToken)> ListVersionsAsync(
        string providerId, int pageSize, string? continuationToken)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND " +
                "(c.providerId = @providerId OR (NOT IS_DEFINED(c.providerId) AND c.id = @providerId)) " +
                "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@providerId", providerId);

        var requestOptions = new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(tenantId),
            MaxItemCount = pageSize
        };

        var iterator = _container.GetItemQueryIterator<Provider>(query, continuationToken, requestOptions);
        if (!iterator.HasMoreResults)
            return (Array.Empty<Provider>(), null);

        var response = await iterator.ReadNextAsync();
        var items = response.Select(Hydrate).ToList();
        return (items, response.ContinuationToken);
    }

    public async Task<Provider> CreateDraftAsync(Provider draft)
    {
        var tenantId = GetTenantId();
        draft.TenantId = tenantId;
        draft.VersionState = ProviderVersionState.Draft;
        if (string.IsNullOrEmpty(draft.Id)) draft.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(draft.ProviderId)) draft.ProviderId = draft.Id;
        draft.LastUpdatedDate = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(draft, new PartitionKey(tenantId));
        return response.Resource;
    }

    public async Task<Provider> UpdateDraftAsync(Provider draft)
    {
        Provider? existing;
        try
        {
            var read = await _container.ReadItemAsync<Provider>(draft.Id, new PartitionKey(draft.TenantId));
            existing = Hydrate(read.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            existing = null;
        }
        if (existing == null)
        {
            throw new ProviderVersionStateException(draft.ProviderId, draft.VersionId, ProviderVersionState.Draft,
                $"Draft {draft.VersionId} not found") { IsNotFound = true };
        }
        if (existing.VersionState != ProviderVersionState.Draft)
        {
            throw new ProviderVersionStateException(
                existing.ProviderId, existing.VersionId, existing.VersionState,
                $"Provider version {existing.VersionId} is {existing.VersionState} and cannot be edited.");
        }

        draft.LastUpdatedDate = DateTime.UtcNow;
        draft.VersionState = ProviderVersionState.Draft;

        var response = await _container.ReplaceItemAsync(draft, draft.Id, new PartitionKey(draft.TenantId));
        return response.Resource;
    }

    public async Task<Provider> ActivateAndSupersedeAsync(Provider draftToActivate, Provider? predecessor)
    {
        if (draftToActivate.VersionState != ProviderVersionState.Active)
        {
            throw new InvalidOperationException(
                "ActivateAndSupersedeAsync expects draftToActivate to already have VersionState=Active applied by the service layer.");
        }

        draftToActivate.LastUpdatedDate = DateTime.UtcNow;

        var batch = _container.CreateTransactionalBatch(new PartitionKey(draftToActivate.TenantId))
            .ReplaceItem(draftToActivate.Id, draftToActivate);

        if (predecessor != null)
        {
            predecessor.LastUpdatedDate = DateTime.UtcNow;
            batch = batch.ReplaceItem(predecessor.Id, predecessor);
        }

        using var response = await batch.ExecuteAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new ProviderVersionStateException(
                draftToActivate.Id, draftToActivate.VersionId, draftToActivate.VersionState,
                $"Atomic activate/supersede failed: {response.StatusCode}");
        }

        return draftToActivate;
    }

    public async Task<Provider> ReplaceVersionRowAsync(Provider version)
    {
        version.LastUpdatedDate = DateTime.UtcNow;
        var response = await _container.ReplaceItemAsync(
            version,
            version.Id,
            new PartitionKey(version.TenantId));
        return response.Resource;
    }

    /// <summary>
    /// Backfill identity fields on legacy rows that predate this feature
    /// and keep the legacy <see cref="Provider.Status"/> in sync with
    /// <see cref="Provider.VersionState"/> so existing consumers (search
    /// filter, PcpAssignmentService) keep working unchanged.
    /// </summary>
    private static Provider Hydrate(Provider provider)
    {
        if (string.IsNullOrEmpty(provider.ProviderId))
        {
            // Legacy single-row chain: the document Id is also the chain key.
            provider.ProviderId = provider.Id;
        }

        if (string.IsNullOrEmpty(provider.VersionId))
        {
            provider.VersionId = provider.Id;
            provider.VersionNumber = provider.VersionNumber <= 0 ? 1 : provider.VersionNumber;
            // Map the legacy ProviderStatus onto the version state so
            // pre-existing rows hydrate with a sensible state.
            provider.VersionState = provider.Status switch
            {
                ProviderStatus.Terminated => ProviderVersionState.Terminated,
                ProviderStatus.Inactive => ProviderVersionState.Suspended,
                ProviderStatus.Pending => ProviderVersionState.Draft,
                _ => ProviderVersionState.Active
            };
        }

        // Keep Status synced with VersionState for downstream consumers.
        provider.Status = provider.VersionState switch
        {
            ProviderVersionState.Active => ProviderStatus.Active,
            ProviderVersionState.Suspended => ProviderStatus.Inactive,
            ProviderVersionState.Terminated => ProviderStatus.Terminated,
            ProviderVersionState.Superseded => ProviderStatus.Inactive,
            ProviderVersionState.Draft => ProviderStatus.Pending,
            _ => provider.Status
        };

        return provider;
    }
}

// Extension method to get query parameters (for debugging/logging)
public static class QueryDefinitionExtensions
{
    public static IEnumerable<(string, object)> GetQueryParameters(this QueryDefinition queryDef)
    {
        // Note: QueryDefinition doesn't expose parameters publicly
        // This is a placeholder - in production, track parameters separately or use logging
        return new List<(string, object)>();
    }
}
