using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using ProviderService.Models;
using CloudHealthOffice.Infrastructure.Logging;

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
        // The tenant comes from the validated token (UseChoAuthentication). A
        // missing tenant is an error, never an empty-tenant query or write.
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("TenantId not found in request context");
        return tenantId;
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
        // Always the request tenant, never a body-supplied one.
        draft.TenantId = GetTenantId();
        if (string.IsNullOrEmpty(draft.Id)) draft.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(draft.OrganizationId)) draft.OrganizationId = draft.Id;
        draft.VersionState = OrganizationVersionState.Draft;
        draft.LastUpdatedDate = DateTime.UtcNow;
        draft.Revision = 0;
        await _collection.InsertOneAsync(draft);
        return draft;
    }

    private static FilterDefinition<Organization> RowIs(Organization org) =>
        Builders<Organization>.Filter.And(
            Builders<Organization>.Filter.Eq(o => o.Id, org.Id),
            Builders<Organization>.Filter.Eq(o => o.TenantId, org.TenantId));

    /// <summary>The document still has the revision the write was based on (0 matches documents written before the field existed).</summary>
    private static FilterDefinition<Organization> RevisionIs(long expected)
    {
        var b = Builders<Organization>.Filter;
        return expected == 0
            ? b.Or(b.Eq(o => o.Revision, 0L), b.Exists(o => o.Revision, false))
            : b.Eq(o => o.Revision, expected);
    }

    /// <summary>
    /// The row has not left Draft: its stored state is still Draft, or it is a
    /// legacy row with no stored state that <see cref="Hydrate"/> reads as a
    /// Draft (status Pending). The revision catches every transition in
    /// between, because each one bumps it.
    /// </summary>
    private static FilterDefinition<Organization> IsDraft()
    {
        var b = Builders<Organization>.Filter;
        return b.Or(
            b.Eq(o => o.VersionState, OrganizationVersionState.Draft),
            b.And(b.Exists(o => o.VersionState, false), b.Eq(o => o.Status, OrganizationStatus.Pending)));
    }

    /// <summary>
    /// A conditional replace matched nothing: a conflict when the row still
    /// exists (another write changed its revision or state), otherwise
    /// <paramref name="notFound"/>.
    /// </summary>
    private async Task<Exception> NoMatchAsync(IClientSessionHandle? session, Organization org, long expected, Func<Exception> notFound)
    {
        var exists = session == null
            ? await _collection.Find(RowIs(org)).AnyAsync()
            : await _collection.Find(session, RowIs(org)).AnyAsync();
        return exists ? new ProviderConcurrencyException("Organization", org.Id, expected) : notFound();
    }

    private static OrganizationVersionStateException VersionNotFound(Organization org) =>
        new(org.OrganizationId, org.VersionId, org.VersionState, $"Version {org.VersionId} not found") { IsNotFound = true };

    /// <summary>A MongoDB write conflict: another transaction wrote the same document first.</summary>
    private static bool IsWriteConflict(Exception ex) =>
        ex is MongoCommandException { Code: 112 }
        || ex is MongoWriteException { WriteError.Code: 112 };

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

        // Conditional on the row still being a Draft at the revision the
        // caller read: an edit that lands after the draft was activated (or
        // after another edit) fails instead of turning the Active version back
        // into a Draft or erasing the other edit.
        var expected = draft.Revision;
        draft.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(filter & b.Eq(o => o.VersionState, OrganizationVersionState.Draft) & RevisionIs(expected), draft);
        }
        catch
        {
            draft.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            draft.Revision = expected;
            throw await NoMatchAsync(null, draft, expected, () => new OrganizationVersionStateException(
                draft.OrganizationId, draft.VersionId, OrganizationVersionState.Draft, $"Draft {draft.VersionId} not found") { IsNotFound = true });
        }
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

        // Each write is conditional on the state the service checked on its
        // read (the draft is still a Draft, the predecessor not yet
        // Superseded) and on the revision it read. Of two concurrent updates
        // of the same organization (each a new draft superseding the same
        // head) only one can match; the other fails with a conflict instead of
        // both succeeding.
        var draftExpected = draftToActivate.Revision;
        var predExpected = predecessor?.Revision ?? 0;
        var activateFilter = RowIs(draftToActivate) & IsDraft() & RevisionIs(draftExpected);
        var predFilter = predecessor == null
            ? null
            : RowIs(predecessor) & Builders<Organization>.Filter.Ne(o => o.VersionState, OrganizationVersionState.Superseded) & RevisionIs(predExpected);

        draftToActivate.Revision = draftExpected + 1;
        if (predecessor != null) predecessor.Revision = predExpected + 1;
        try
        {
            return await ActivateAndSupersedeCoreAsync(draftToActivate, draftExpected, activateFilter, predecessor, predExpected, predFilter);
        }
        catch
        {
            draftToActivate.Revision = draftExpected;
            if (predecessor != null) predecessor.Revision = predExpected;
            throw;
        }
    }

    private async Task<Organization> ActivateAndSupersedeCoreAsync(
        Organization draftToActivate, long draftExpected, FilterDefinition<Organization> activateFilter,
        Organization? predecessor, long predExpected, FilterDefinition<Organization>? predFilter)
    {
        try
        {
            using var session = await _database.Client.StartSessionAsync();
            session.StartTransaction();
            try
            {
                var activated = await _collection.ReplaceOneAsync(session, activateFilter, draftToActivate);
                if (activated.MatchedCount == 0)
                    throw await NoMatchAsync(session, draftToActivate, draftExpected, () => VersionNotFound(draftToActivate));

                if (predecessor != null)
                {
                    var superseded = await _collection.ReplaceOneAsync(session, predFilter!, predecessor);
                    if (superseded.MatchedCount == 0)
                        throw await NoMatchAsync(session, predecessor, predExpected, () => VersionNotFound(predecessor));
                }

                await session.CommitTransactionAsync();
                return draftToActivate;
            }
            catch (Exception ex) when (IsWriteConflict(ex))
            {
                // Another transaction wrote one of the two rows first.
                await session.AbortTransactionAsync();
                throw new ProviderConcurrencyException("Organization", draftToActivate.Id, draftExpected, ex);
            }
            catch
            {
                await session.AbortTransactionAsync();
                throw;
            }
        }
        catch (NotSupportedException)
        {
            return await ActivateAndSupersedeWithoutTransactionAsync(draftToActivate, draftExpected, activateFilter, predecessor, predExpected, predFilter);
        }
        catch (MongoCommandException ex) when (
            ex.CodeName == "IllegalOperation" || ex.Code == 20 || ex.Code == 263)
        {
            return await ActivateAndSupersedeWithoutTransactionAsync(draftToActivate, draftExpected, activateFilter, predecessor, predExpected, predFilter);
        }
    }

    private async Task<Organization> ActivateAndSupersedeWithoutTransactionAsync(
        Organization draftToActivate, long draftExpected, FilterDefinition<Organization> activateFilter,
        Organization? predecessor, long predExpected, FilterDefinition<Organization>? predFilter)
    {
        _logger.LogWarning(
            "Mongo deployment does not support transactions; activating organization {OrgId} version {VersionId} non-atomically.",
            LogSanitizer.Sanitize(draftToActivate.OrganizationId), LogSanitizer.Sanitize(draftToActivate.VersionId));

        if (predecessor == null)
        {
            var genesis = await _collection.ReplaceOneAsync(activateFilter, draftToActivate);
            if (genesis.MatchedCount == 0)
                throw await NoMatchAsync(null, draftToActivate, draftExpected, () => VersionNotFound(draftToActivate));
            return draftToActivate;
        }

        // Without a transaction the predecessor is superseded first: its
        // conditional write decides which of two concurrent updates wins, so
        // the loser stops before activating its draft and the chain never has
        // two Active heads. The stored predecessor is kept so that, if the
        // draft write then fails, the supersede can be put back.
        var before = await _collection.Find(predFilter!).FirstOrDefaultAsync();
        var superseded = before == null
            ? null
            : await _collection.ReplaceOneAsync(predFilter!, predecessor);
        if (superseded == null || superseded.MatchedCount == 0)
            throw await NoMatchAsync(null, predecessor, predExpected, () => VersionNotFound(predecessor));

        var activated = await _collection.ReplaceOneAsync(activateFilter, draftToActivate);
        if (activated.MatchedCount == 0)
        {
            before!.Revision = predExpected + 2;
            var restored = await _collection.ReplaceOneAsync(
                RowIs(predecessor) & RevisionIs(predExpected + 1), before);
            if (restored.MatchedCount == 0)
            {
                _logger.LogError(
                    "Activating organization {OrgId} version {VersionId} failed after its predecessor {PredecessorVersionId} was superseded, and the predecessor could not be restored; the chain has no head until it is repaired.",
                    LogSanitizer.Sanitize(draftToActivate.OrganizationId), LogSanitizer.Sanitize(draftToActivate.VersionId), LogSanitizer.Sanitize(predecessor.VersionId));
            }
            throw await NoMatchAsync(null, draftToActivate, draftExpected, () => VersionNotFound(draftToActivate));
        }

        return draftToActivate;
    }

    public async Task<Organization> ReplaceVersionRowAsync(Organization version)
    {
        version.LastUpdatedDate = DateTime.UtcNow;

        // Conditional on the revision the service read when it checked the
        // row's state: a termination racing an update that superseded the row
        // (or another transition) fails instead of overwriting it. The
        // expected state is not passed in, so the revision is the guard; every
        // write to the row bumps it.
        var expected = version.Revision;
        version.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(RowIs(version) & RevisionIs(expected), version);
        }
        catch
        {
            version.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            version.Revision = expected;
            // A missing row stays a silent no-op, as before.
            if (await _collection.Find(RowIs(version)).AnyAsync())
                throw new ProviderConcurrencyException("Organization", version.Id, expected);
        }
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
