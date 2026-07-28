using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using ProviderService.Models;

namespace ProviderService.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IOrganizationRepository"/>.
/// Mirrors <see cref="ProviderRepositoryMongo"/> partition / hydration shape.
/// </summary>
public class OrganizationRepositoryMongo : IOrganizationRepository
{
    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<Organization> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<OrganizationRepositoryMongo> _logger;

    public OrganizationRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<OrganizationRepositoryMongo> logger)
    {
        _database = database;
        _collection = database.GetCollection<Organization>("Organizations");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        return string.IsNullOrEmpty(tenantId) ? string.Empty : tenantId;
    }

    private static FilterDefinition<Organization> ChainKeyFilter(string organizationId)
    {
        var b = Builders<Organization>.Filter;
        return b.Or(
            b.Eq(o => o.OrganizationId, organizationId),
            b.And(
                b.Or(b.Eq(o => o.OrganizationId, string.Empty), b.Exists(o => o.OrganizationId, false)),
                b.Eq(o => o.Id, organizationId)));
    }

    public async Task<Organization?> GetByIdAsync(string organizationId)
    {
        var tenantId = GetTenantId();
        var b = Builders<Organization>.Filter;

        var notRealDraft = b.Or(
            b.Ne(o => o.VersionState, OrganizationVersionState.Draft),
            b.Eq(o => o.VersionId, string.Empty),
            b.Exists(o => o.VersionId, false));

        var filter = b.And(
            b.Eq(o => o.TenantId, tenantId),
            ChainKeyFilter(organizationId),
            notRealDraft);

        var doc = await _collection.Find(filter)
            .SortByDescending(o => o.VersionNumber)
            .FirstOrDefaultAsync();
        return doc == null ? null : Hydrate(doc);
    }

    public async Task<Organization?> GetVersionAsync(string organizationId, string versionId)
    {
        var tenantId = GetTenantId();
        var b = Builders<Organization>.Filter;
        var filter = b.And(
            b.Eq(o => o.TenantId, tenantId),
            ChainKeyFilter(organizationId),
            b.Eq(o => o.VersionId, versionId));
        var doc = await _collection.Find(filter).FirstOrDefaultAsync();
        return doc == null ? null : Hydrate(doc);
    }

    public async Task<Organization?> GetLatestActiveAsync(string organizationId, DateTime asOf)
    {
        var tenantId = GetTenantId();
        var b = Builders<Organization>.Filter;

        var stateFilter = b.Or(
            b.Eq(o => o.VersionState, OrganizationVersionState.Active),
            b.Exists(o => o.VersionState, false));

        var filter = b.And(
            ChainKeyFilter(organizationId),
            b.Eq(o => o.TenantId, tenantId),
            stateFilter,
            b.Or(
                b.Eq(o => o.TerminationDate, null),
                b.Gte(o => o.TerminationDate, asOf)));

        var doc = await _collection.Find(filter)
            .SortByDescending(o => o.VersionNumber)
            .FirstOrDefaultAsync();
        return doc == null ? null : Hydrate(doc);
    }

    public async Task<(IReadOnlyList<Organization> Items, string? ContinuationToken)> ListVersionsAsync(
        string organizationId, int pageSize, string? continuationToken)
    {
        var tenantId = GetTenantId();
        var skip = 0;
        if (!string.IsNullOrEmpty(continuationToken) &&
            int.TryParse(continuationToken, out var parsed) && parsed > 0)
        {
            skip = parsed;
        }

        var b = Builders<Organization>.Filter;
        var filter = b.And(
            b.Eq(o => o.TenantId, tenantId),
            ChainKeyFilter(organizationId));

        var docs = await _collection.Find(filter)
            .SortByDescending(o => o.VersionNumber)
            .Skip(skip)
            .Limit(pageSize + 1)
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

    public async Task<(IReadOnlyList<Organization> Items, int? TotalCount)> ListAsync(
        NetworkType? networkType,
        LineOfBusiness? lineOfBusiness,
        string? parentOrganizationId,
        int page,
        int pageSize)
    {
        var tenantId = GetTenantId();
        var b = Builders<Organization>.Filter;

        var notRealDraft = b.Or(
            b.Ne(o => o.VersionState, OrganizationVersionState.Draft),
            b.Eq(o => o.VersionId, string.Empty),
            b.Exists(o => o.VersionId, false));

        var filter = b.And(
            b.Eq(o => o.TenantId, tenantId),
            notRealDraft);

        if (networkType.HasValue)
            filter = b.And(filter, b.Eq(o => o.NetworkType, networkType.Value));

        if (lineOfBusiness.HasValue)
            filter = b.And(filter, b.Eq(o => o.LineOfBusiness, lineOfBusiness.Value));

        if (!string.IsNullOrEmpty(parentOrganizationId))
            filter = b.And(filter, b.Eq(o => o.ParentOrganizationId, parentOrganizationId));

        // Server-side aggregation: $match → $sort → $group ($first per chain)
        // → $replaceRoot → $sort by name. This avoids transferring every
        // version document to the app and lets Mongo apply the indexes on
        // (TenantId, OrganizationId, VersionNumber) for the de-duplication.
        var heads = _collection.Aggregate()
            .Match(filter)
            .SortByDescending(o => o.VersionNumber)
            .Group(
                o => o.OrganizationId,
                g => new { Organization = g.First() })
            .ReplaceRoot(x => x.Organization)
            .SortBy(o => o.Name);

        var totalDoc = await heads.Count().FirstOrDefaultAsync();
        var total = (int)(totalDoc?.Count ?? 0);

        var pagedDocs = await heads
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();

        var paged = pagedDocs.Select(Hydrate).ToList();
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
        if (string.IsNullOrEmpty(draft.TenantId)) draft.TenantId = tenantId;
        if (string.IsNullOrEmpty(draft.Id)) draft.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(draft.OrganizationId)) draft.OrganizationId = draft.Id;
        draft.VersionState = OrganizationVersionState.Draft;
        draft.LastUpdatedDate = DateTime.UtcNow;
        await _collection.InsertOneAsync(draft);
        return draft;
    }

    public async Task<Organization> UpdateDraftAsync(Organization draft)
    {
        var b = Builders<Organization>.Filter;
        var filter = b.And(
            b.Eq(o => o.Id, draft.Id),
            b.Eq(o => o.TenantId, draft.TenantId));

        var existing = await _collection.Find(filter).FirstOrDefaultAsync()
            ?? throw new OrganizationVersionStateException(draft.OrganizationId, draft.VersionId, OrganizationVersionState.Draft,
                $"Draft {draft.VersionId} not found") { IsNotFound = true };

        if (existing.VersionState != OrganizationVersionState.Draft)
        {
            throw new OrganizationVersionStateException(
                existing.OrganizationId, existing.VersionId, existing.VersionState,
                $"Organization version {existing.VersionId} is {existing.VersionState} and cannot be edited.");
        }

        draft.LastUpdatedDate = DateTime.UtcNow;
        draft.VersionState = OrganizationVersionState.Draft;

        await _collection.ReplaceOneAsync(filter, draft);
        return draft;
    }

    public async Task<Organization> ActivateAndSupersedeAsync(Organization draftToActivate, Organization? predecessor)
    {
        if (draftToActivate.VersionState != OrganizationVersionState.Active)
        {
            throw new InvalidOperationException(
                "ActivateAndSupersedeAsync expects draftToActivate to already have VersionState=Active applied by the service layer.");
        }

        draftToActivate.LastUpdatedDate = DateTime.UtcNow;
        if (predecessor != null) predecessor.LastUpdatedDate = DateTime.UtcNow;

        var activateFilter = Builders<Organization>.Filter.And(
            Builders<Organization>.Filter.Eq(o => o.Id, draftToActivate.Id),
            Builders<Organization>.Filter.Eq(o => o.TenantId, draftToActivate.TenantId));

        try
        {
            using var session = await _database.Client.StartSessionAsync();
            session.StartTransaction();
            try
            {
                await _collection.ReplaceOneAsync(session, activateFilter, draftToActivate);

                if (predecessor != null)
                {
                    var predFilter = Builders<Organization>.Filter.And(
                        Builders<Organization>.Filter.Eq(o => o.Id, predecessor.Id),
                        Builders<Organization>.Filter.Eq(o => o.TenantId, predecessor.TenantId));
                    await _collection.ReplaceOneAsync(session, predFilter, predecessor);
                }

                await session.CommitTransactionAsync();
                return draftToActivate;
            }
            catch
            {
                await session.AbortTransactionAsync();
                throw;
            }
        }
        catch (NotSupportedException)
        {
            return await ActivateAndSupersedeWithoutTransactionAsync(draftToActivate, predecessor, activateFilter);
        }
        catch (MongoCommandException ex) when (
            ex.CodeName == "IllegalOperation" || ex.Code == 20 || ex.Code == 263)
        {
            return await ActivateAndSupersedeWithoutTransactionAsync(draftToActivate, predecessor, activateFilter);
        }
    }

    private async Task<Organization> ActivateAndSupersedeWithoutTransactionAsync(
        Organization draftToActivate,
        Organization? predecessor,
        FilterDefinition<Organization> activateFilter)
    {
        _logger.LogWarning(
            "Mongo deployment does not support transactions; activating organization {OrgId} version {VersionId} non-atomically.",
            SanitizeForLog(draftToActivate.OrganizationId), SanitizeForLog(draftToActivate.VersionId));

        await _collection.ReplaceOneAsync(activateFilter, draftToActivate);

        if (predecessor != null)
        {
            var predFilter = Builders<Organization>.Filter.And(
                Builders<Organization>.Filter.Eq(o => o.Id, predecessor.Id),
                Builders<Organization>.Filter.Eq(o => o.TenantId, predecessor.TenantId));
            await _collection.ReplaceOneAsync(predFilter, predecessor);
        }

        return draftToActivate;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    public async Task<Organization> ReplaceVersionRowAsync(Organization version)
    {
        version.LastUpdatedDate = DateTime.UtcNow;
        var filter = Builders<Organization>.Filter.And(
            Builders<Organization>.Filter.Eq(o => o.Id, version.Id),
            Builders<Organization>.Filter.Eq(o => o.TenantId, version.TenantId));
        await _collection.ReplaceOneAsync(filter, version);
        return version;
    }

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
