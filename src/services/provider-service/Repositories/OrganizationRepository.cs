using Microsoft.Azure.Cosmos;
using ProviderService.Models;

namespace ProviderService.Repositories;

/// <summary>
/// Storage seam for the <see cref="Organization"/> network entity. Each
/// row is one immutable version; default reads resolve to the latest
/// non-Draft head, mirroring <see cref="IProviderRepository"/>.
/// </summary>
public interface IOrganizationRepository
{
    /// <summary>Latest non-Draft version of <paramref name="organizationId"/>.</summary>
    Task<Organization?> GetByIdAsync(string organizationId);

    /// <summary>
    /// Look up a single version by <c>VersionId</c>. Returns null when
    /// either the chain key or the version id does not resolve.
    /// </summary>
    Task<Organization?> GetVersionAsync(string organizationId, string versionId);

    /// <summary>
    /// Latest <see cref="OrganizationVersionState.Active"/> version effective
    /// at <paramref name="asOf"/>. Returns null when no Active version exists.
    /// </summary>
    Task<Organization?> GetLatestActiveAsync(string organizationId, DateTime asOf);

    /// <summary>Newest-first list of every version for a chain.</summary>
    Task<(IReadOnlyList<Organization> Items, string? ContinuationToken)> ListVersionsAsync(
        string organizationId, int pageSize, string? continuationToken);

    /// <summary>
    /// List networks for the current tenant, optionally filtered by
    /// <see cref="NetworkType"/> and <see cref="LineOfBusiness"/>. Returns
    /// the head (latest non-Draft) of every chain that matches.
    /// </summary>
    Task<(IReadOnlyList<Organization> Items, int? TotalCount)> ListAsync(
        NetworkType? networkType,
        LineOfBusiness? lineOfBusiness,
        string? parentOrganizationId,
        int page,
        int pageSize);

    /// <summary>Children of <paramref name="parentOrganizationId"/> for partOf traversal.</summary>
    Task<IReadOnlyList<Organization>> GetByParentAsync(string parentOrganizationId);

    /// <summary>Persist a Draft. Caller sets identity + state.</summary>
    Task<Organization> CreateDraftAsync(Organization draft);

    /// <summary>Update a Draft. Throws when the row is not Draft.</summary>
    Task<Organization> UpdateDraftAsync(Organization draft);

    /// <summary>
    /// Atomic transition: flip <paramref name="draftToActivate"/> to Active
    /// and (if not null) <paramref name="predecessor"/> to Superseded.
    /// </summary>
    Task<Organization> ActivateAndSupersedeAsync(Organization draftToActivate, Organization? predecessor);

    /// <summary>Persist a state-only mutation on an existing version row.</summary>
    Task<Organization> ReplaceVersionRowAsync(Organization version);
}

/// <summary>
/// Thrown when a write violates the version-state invariants on the
/// network chain. Mirrors <see cref="ProviderVersionStateException"/>.
/// </summary>
public sealed class OrganizationVersionStateException : InvalidOperationException
{
    public string OrganizationId { get; }
    public string VersionId { get; }
    public OrganizationVersionState CurrentState { get; }

    public bool IsNotFound { get; init; }

    public OrganizationVersionStateException(
        string organizationId, string versionId, OrganizationVersionState currentState, string message)
        : base(message)
    {
        OrganizationId = organizationId;
        VersionId = versionId;
        CurrentState = currentState;
    }
}

/// <summary>
/// Cosmos DB implementation of <see cref="IOrganizationRepository"/>.
/// Mirrors <see cref="ProviderRepository"/> partition / hydration shape.
///
/// <para>
/// <b>Cosmos enum serialization caveat.</b> The default Cosmos SDK
/// serializer (Newtonsoft) ignores System.Text.Json
/// <c>[JsonConverter]</c> attributes and persists enums as integers. The
/// SQL queries below compare enum fields to <c>Enum.ToString()</c>, which
/// matches the in-memory representation but not the persisted integer
/// form. This mirrors the existing <see cref="ProviderRepository"/>
/// pattern and works in environments that configure <c>CosmosClient</c>
/// with a System.Text.Json serializer (or where Cosmos is exercised only
/// via the in-memory test fakes). A platform-wide fix — registering
/// <c>CosmosClientOptions.UseSystemTextJsonSerializerWithOptions</c> in
/// Program.cs — would address this for both repositories at once and
/// remains a follow-up beyond the scope of capability 5.3.
/// </para>
/// </summary>
public class OrganizationRepository : IOrganizationRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<OrganizationRepository> _logger;

    public OrganizationRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<OrganizationRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "ProviderDB";
        var containerName = configuration["CosmosDb:OrganizationsContainerName"] ?? "Organizations";

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

    public async Task<Organization?> GetByIdAsync(string organizationId)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND " +
                "(c.organizationId = @id OR (NOT IS_DEFINED(c.organizationId) AND c.id = @id)) AND " +
                "(NOT IS_DEFINED(c.versionState) OR c.versionState != @draft) " +
                "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@id", organizationId)
            .WithParameter("@draft", OrganizationVersionState.Draft.ToString());

        var iterator = _container.GetItemQueryIterator<Organization>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            var first = page.FirstOrDefault();
            if (first != null) return Hydrate(first);
        }
        return null;
    }

    public async Task<Organization?> GetVersionAsync(string organizationId, string versionId)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND " +
                "(c.organizationId = @id OR (NOT IS_DEFINED(c.organizationId) AND c.id = @id)) AND " +
                "c.versionId = @versionId")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@id", organizationId)
            .WithParameter("@versionId", versionId);

        var iterator = _container.GetItemQueryIterator<Organization>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            var first = page.FirstOrDefault();
            if (first != null) return Hydrate(first);
        }
        return null;
    }

    public async Task<Organization?> GetLatestActiveAsync(string organizationId, DateTime asOf)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND " +
                "(c.organizationId = @id OR (NOT IS_DEFINED(c.organizationId) AND c.id = @id)) AND " +
                "(NOT IS_DEFINED(c.versionState) OR c.versionState = @active) AND " +
                "(NOT IS_DEFINED(c.terminationDate) OR c.terminationDate = null OR c.terminationDate >= @asOf) " +
                "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@id", organizationId)
            .WithParameter("@active", OrganizationVersionState.Active.ToString())
            .WithParameter("@asOf", asOf);

        var iterator = _container.GetItemQueryIterator<Organization>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            var first = page.FirstOrDefault();
            if (first != null) return Hydrate(first);
        }
        return null;
    }

    public async Task<(IReadOnlyList<Organization> Items, string? ContinuationToken)> ListVersionsAsync(
        string organizationId, int pageSize, string? continuationToken)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND " +
                "(c.organizationId = @id OR (NOT IS_DEFINED(c.organizationId) AND c.id = @id)) " +
                "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@id", organizationId);

        var requestOptions = new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(tenantId),
            MaxItemCount = pageSize
        };

        var iterator = _container.GetItemQueryIterator<Organization>(query, continuationToken, requestOptions);
        if (!iterator.HasMoreResults)
            return (Array.Empty<Organization>(), null);

        var response = await iterator.ReadNextAsync();
        var items = response.Select(Hydrate).ToList();
        return (items, response.ContinuationToken);
    }

    public async Task<(IReadOnlyList<Organization> Items, int? TotalCount)> ListAsync(
        NetworkType? networkType,
        LineOfBusiness? lineOfBusiness,
        string? parentOrganizationId,
        int page,
        int pageSize)
    {
        var tenantId = GetTenantId();

        // Pull all non-Draft rows for the tenant under the requested filters,
        // then group by chain key in-memory and pick the highest-VersionNumber
        // row per chain. Cosmos has no GROUP BY support for documents in this
        // shape, so the de-duplication happens at the application layer.
        var conditions = new List<string>
        {
            "c.tenantId = @tenantId",
            "(NOT IS_DEFINED(c.versionState) OR c.versionState != @draft)"
        };

        if (networkType.HasValue) conditions.Add("c.networkType = @networkType");
        if (lineOfBusiness.HasValue) conditions.Add("c.lineOfBusiness = @lineOfBusiness");
        if (!string.IsNullOrEmpty(parentOrganizationId)) conditions.Add("c.parentOrganizationId = @parent");

        var queryText = $"SELECT * FROM c WHERE {string.Join(" AND ", conditions)} ORDER BY c.versionNumber DESC";
        var finalQuery = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@draft", OrganizationVersionState.Draft.ToString());
        if (networkType.HasValue)
            finalQuery = finalQuery.WithParameter("@networkType", networkType.Value.ToString());
        if (lineOfBusiness.HasValue)
            finalQuery = finalQuery.WithParameter("@lineOfBusiness", lineOfBusiness.Value.ToString());
        if (!string.IsNullOrEmpty(parentOrganizationId))
            finalQuery = finalQuery.WithParameter("@parent", parentOrganizationId);

        var iterator = _container.GetItemQueryIterator<Organization>(
            finalQuery, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var rows = new List<Organization>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            rows.AddRange(response);
        }

        var heads = rows
            .Select(Hydrate)
            .GroupBy(o => o.OrganizationId)
            .Select(g => g.OrderByDescending(o => o.VersionNumber).First())
            .OrderBy(o => o.Name)
            .ToList();

        var total = heads.Count;
        var paged = heads.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return (paged, total);
    }

    public async Task<IReadOnlyList<Organization>> GetByParentAsync(string parentOrganizationId)
    {
        var (items, _) = await ListAsync(
            networkType: null,
            lineOfBusiness: null,
            parentOrganizationId: parentOrganizationId,
            page: 1,
            pageSize: 500);
        return items;
    }

    public async Task<Organization> CreateDraftAsync(Organization draft)
    {
        var tenantId = GetTenantId();
        draft.TenantId = tenantId;
        draft.VersionState = OrganizationVersionState.Draft;
        if (string.IsNullOrEmpty(draft.Id)) draft.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(draft.OrganizationId)) draft.OrganizationId = draft.Id;
        draft.LastUpdatedDate = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(draft, new PartitionKey(tenantId));
        return response.Resource;
    }

    public async Task<Organization> UpdateDraftAsync(Organization draft)
    {
        Organization? existing;
        try
        {
            var read = await _container.ReadItemAsync<Organization>(draft.Id, new PartitionKey(draft.TenantId));
            existing = Hydrate(read.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            existing = null;
        }
        if (existing == null)
        {
            throw new OrganizationVersionStateException(draft.OrganizationId, draft.VersionId, OrganizationVersionState.Draft,
                $"Draft {draft.VersionId} not found") { IsNotFound = true };
        }
        if (existing.VersionState != OrganizationVersionState.Draft)
        {
            throw new OrganizationVersionStateException(
                existing.OrganizationId, existing.VersionId, existing.VersionState,
                $"Organization version {existing.VersionId} is {existing.VersionState} and cannot be edited.");
        }

        draft.LastUpdatedDate = DateTime.UtcNow;
        draft.VersionState = OrganizationVersionState.Draft;

        var response = await _container.ReplaceItemAsync(draft, draft.Id, new PartitionKey(draft.TenantId));
        return response.Resource;
    }

    public async Task<Organization> ActivateAndSupersedeAsync(Organization draftToActivate, Organization? predecessor)
    {
        if (draftToActivate.VersionState != OrganizationVersionState.Active)
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
            throw new OrganizationVersionStateException(
                draftToActivate.OrganizationId, draftToActivate.VersionId, draftToActivate.VersionState,
                $"Atomic activate/supersede failed: {response.StatusCode}");
        }

        return draftToActivate;
    }

    public async Task<Organization> ReplaceVersionRowAsync(Organization version)
    {
        version.LastUpdatedDate = DateTime.UtcNow;
        var response = await _container.ReplaceItemAsync(
            version, version.Id, new PartitionKey(version.TenantId));
        return response.Resource;
    }

    /// <summary>
    /// Backfills identity fields on legacy rows that predate the versioning
    /// fields and keeps <see cref="Organization.Status"/> in sync with
    /// <see cref="Organization.VersionState"/> for downstream consumers.
    /// </summary>
    private static Organization Hydrate(Organization org)
    {
        if (string.IsNullOrEmpty(org.OrganizationId))
        {
            org.OrganizationId = org.Id;
        }
        if (string.IsNullOrEmpty(org.VersionId))
        {
            org.VersionId = org.Id;
            org.VersionNumber = org.VersionNumber <= 0 ? 1 : org.VersionNumber;
            org.VersionState = org.Status switch
            {
                OrganizationStatus.Terminated => OrganizationVersionState.Terminated,
                OrganizationStatus.Inactive => OrganizationVersionState.Suspended,
                OrganizationStatus.Pending => OrganizationVersionState.Draft,
                _ => OrganizationVersionState.Active
            };
        }

        org.Status = org.VersionState switch
        {
            OrganizationVersionState.Active => OrganizationStatus.Active,
            OrganizationVersionState.Suspended => OrganizationStatus.Inactive,
            OrganizationVersionState.Terminated => OrganizationStatus.Terminated,
            OrganizationVersionState.Superseded => OrganizationStatus.Inactive,
            OrganizationVersionState.Draft => OrganizationStatus.Pending,
            _ => org.Status
        };

        return org;
    }
}
