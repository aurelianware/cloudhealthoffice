using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using ProviderService.Models;

namespace ProviderService.Repositories;

public class ProviderRepositoryMongo : IProviderRepository
{
    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<Provider> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ProviderRepositoryMongo> _logger;

    public ProviderRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ProviderRepositoryMongo> logger)
    {
        _database = database;
        _collection = database.GetCollection<Provider>("Providers");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
        {
           // For migration safety, we might return a default or just let it fail at runtime if strictly required.
           // throw new InvalidOperationException("TenantId not found in request context");
           return string.Empty;
        }
        return tenantId;
    }

    public async Task<Provider?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var b = Builders<Provider>.Filter;

        // The chain key is ProviderId. Legacy single-row chains have
        // ProviderId empty on disk and rely on hydration setting it to
        // Id; we accept both shapes here.
        var chainFilter = b.Or(
            b.Eq(p => p.ProviderId, id),
            b.And(b.Or(b.Eq(p => p.ProviderId, string.Empty), b.Exists(p => p.ProviderId, false)),
                  b.Eq(p => p.Id, id)));

        // Exclude real Draft rows (non-empty VersionId + Draft state).
        // Legacy rows have VersionId = "" and default VersionState = Draft (0)
        // but they are not actual drafts — hydration will normalize them to Active.
        var notRealDraft = b.Or(
            b.Ne(p => p.VersionState, ProviderVersionState.Draft),
            b.Eq(p => p.VersionId, string.Empty),
            b.Exists(p => p.VersionId, false));

        var filter = b.And(
            b.Eq(p => p.TenantId, tenantId),
            chainFilter,
            notRealDraft);

        var doc = await _collection.Find(filter)
            .SortByDescending(p => p.VersionNumber)
            .FirstOrDefaultAsync();
        return doc == null ? null : Hydrate(doc);
    }

    public async Task<Provider?> GetByNPIAsync(string npi)
    {
        var tenantId = GetTenantId();
        var b = Builders<Provider>.Filter;

        // Exclude real Draft rows but include legacy rows (empty VersionId).
        var notRealDraft = b.Or(
            b.Ne(p => p.VersionState, ProviderVersionState.Draft),
            b.Eq(p => p.VersionId, string.Empty),
            b.Exists(p => p.VersionId, false));

        var filter = b.And(
            b.Eq(p => p.NPI, npi),
            b.Eq(p => p.TenantId, tenantId),
            notRealDraft);

        var doc = await _collection.Find(filter)
            .SortByDescending(p => p.VersionNumber)
            .FirstOrDefaultAsync();
        return doc == null ? null : Hydrate(doc);
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
        int pageSize,
        string? firstName = null,
        string? lastName = null,
        string? city = null)
    {
        var tenantId = GetTenantId();
        var builder = Builders<Provider>.Filter;

        // Base filters
        var filter = builder.And(
            builder.Eq(p => p.TenantId, tenantId),
            builder.Eq(p => p.Status, ProviderStatus.Active) // Enum mapping assumes exact match
        );

        if (!string.IsNullOrEmpty(name))
        {
            // Case-insensitive regex for Name (First, Last, or Org). Escape
            // user input so regex metacharacters / pathological patterns
            // can't be injected into the BSON query — matches the
            // firstName / lastName / city handling below and the roster
            // path in ListNetworkRosterAsync.
            var regex = new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(name), "i");
            var nameFilter = builder.Or(
                builder.Regex(p => p.FirstName, regex),
                builder.Regex(p => p.LastName, regex),
                builder.Regex(p => p.OrganizationName, regex)
            );
            filter = builder.And(filter, nameFilter);
        }

        if (!string.IsNullOrEmpty(firstName))
        {
            var rgx = new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(firstName), "i");
            filter = builder.And(filter, builder.Regex(p => p.FirstName, rgx));
        }

        if (!string.IsNullOrEmpty(lastName))
        {
            var rgx = new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(lastName), "i");
            filter = builder.And(filter, builder.Regex(p => p.LastName, rgx));
        }

        if (!string.IsNullOrEmpty(specialty))
        {
            filter = builder.And(filter, builder.Regex(p => p.PrimarySpecialty,
                new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(specialty), "i")));
        }

        if (!string.IsNullOrEmpty(zipCode))
        {
            filter = builder.And(filter, builder.Eq(p => p.ZipCode, zipCode));
        }

        if (!string.IsNullOrEmpty(state))
        {
            filter = builder.And(filter, builder.Eq(p => p.State, state));
        }

        if (!string.IsNullOrEmpty(city))
        {
            var rgx = new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(city), "i");
            filter = builder.And(filter, builder.Regex(p => p.City, rgx));
        }

        if (providerType.HasValue)
        {
            filter = builder.And(filter, builder.Eq(p => p.ProviderType, providerType.Value));
        }

        if (acceptingNewPatients.HasValue)
        {
             filter = builder.And(filter, builder.Eq(p => p.AcceptingNewPatients, acceptingNewPatients.Value));
        }

        // Network Participation (Array ElemMatch)
        if (!string.IsNullOrEmpty(planId) || lineOfBusiness.HasValue)
        {
            var netBuilder = Builders<NetworkParticipation>.Filter;
            FilterDefinition<NetworkParticipation> netFilter = FilterDefinition<NetworkParticipation>.Empty;

            if (!string.IsNullOrEmpty(planId))
                netFilter = netBuilder.Eq(n => n.PlanId, planId);

            if (lineOfBusiness.HasValue)
            {
                var lobFilter = netBuilder.Eq(n => n.LineOfBusiness, lineOfBusiness.Value);
                netFilter = netFilter == FilterDefinition<NetworkParticipation>.Empty
                    ? lobFilter
                    : netBuilder.And(netFilter, lobFilter);
            }

            filter = builder.And(filter, builder.ElemMatch(p => p.NetworkParticipations, netFilter));
        }

        // Sort by LastName then OrgName
        var sort = Builders<Provider>.Sort.Ascending(p => p.LastName).Ascending(p => p.OrganizationName);

        var docs = await _collection.Find(filter)
            .Sort(sort)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
        return docs.Select(Hydrate).ToList();
    }

    public async Task<Provider> CreateAsync(Provider provider)
    {
        var tenantId = GetTenantId();
        if (string.IsNullOrEmpty(provider.TenantId))
        {
            provider.TenantId = tenantId;
        }

        if (string.IsNullOrEmpty(provider.Id))
        {
            provider.Id = Guid.NewGuid().ToString();
        }

        await _collection.InsertOneAsync(provider);
        return provider;
    }

    public async Task<Provider> UpdateAsync(Provider provider)
    {
        var tenantId = GetTenantId();
        if (!string.IsNullOrEmpty(tenantId) && provider.TenantId != tenantId)
        {
            throw new InvalidOperationException("Cross-tenant updates not allowed");
        }

        // Reject mutations on non-Draft rows so callers fall back to the
        // amend → activate flow. Hydration normalizes legacy rows so that
        // pre-feature data is treated as Active and is also read-only.
        var existing = await _collection.Find(Builders<Provider>.Filter.And(
                Builders<Provider>.Filter.Eq(p => p.Id, provider.Id),
                Builders<Provider>.Filter.Eq(p => p.TenantId, provider.TenantId)))
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            var hydrated = Hydrate(existing);
            if (hydrated.VersionState != ProviderVersionState.Draft)
            {
                throw new ProviderVersionStateException(
                    hydrated.ProviderId, hydrated.VersionId, hydrated.VersionState,
                    $"Provider version {hydrated.VersionId} is {hydrated.VersionState} and cannot be updated. Create an amendment via POST /amend.");
            }
        }

        var filter = Builders<Provider>.Filter.And(
            Builders<Provider>.Filter.Eq(p => p.Id, provider.Id),
            Builders<Provider>.Filter.Eq(p => p.TenantId, provider.TenantId)
        );

        var result = await _collection.ReplaceOneAsync(filter, provider);
        if (result.MatchedCount == 0)
        {
             throw new Exception($"Provider {provider.Id} not found");
        }

        return provider;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Provider>.Filter.And(
            Builders<Provider>.Filter.Eq(p => p.Id, id),
            Builders<Provider>.Filter.Eq(p => p.TenantId, tenantId)
        );
        await _collection.DeleteOneAsync(filter);
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

        var pageSize = Math.Clamp(query.PageSize, 1, NetworkRosterDefaults.MaxPageSize);
        var safeSkip = Math.Max(skip, 0);
        var asOf = (query.AsOfDate ?? DateTime.UtcNow).ToUniversalTime();

        var b = Builders<Provider>.Filter;
        var nb = Builders<NetworkParticipation>.Filter;

        // Build the per-participation filter (ElemMatch). NetworkId is the
        // primary key — without it the row is invisible to this endpoint
        // by design (see network-roster-api.md migration note).
        var participationFilter = nb.And(
            nb.Eq(n => n.NetworkId, query.NetworkId),
            nb.Or(nb.Lte(n => n.EffectiveDate, asOf), nb.Exists(n => n.EffectiveDate, false)),
            nb.Or(nb.Eq(n => n.TerminationDate, null), nb.Gte(n => n.TerminationDate, asOf)));

        if (query.LineOfBusiness.HasValue)
            participationFilter = nb.And(participationFilter, nb.Eq(n => n.LineOfBusiness, query.LineOfBusiness.Value));
        if (!string.IsNullOrEmpty(query.Tier))
            participationFilter = nb.And(participationFilter, nb.Eq(n => n.NetworkTier, query.Tier));
        if (query.AcceptingNewPatients.HasValue)
            participationFilter = nb.And(participationFilter, nb.Eq(n => n.AcceptingNewPatients, query.AcceptingNewPatients.Value));

        // Provider chain must be Active (or legacy/unset) and not
        // terminated before AsOfDate. Three shapes count as "active":
        //   1. VersionState == Active (current versioned shape).
        //   2. VersionState absent (legacy row pre-versioning).
        //   3. VersionId empty/missing AND Status == Active (legacy row
        //      where VersionState defaulted to the C# enum zero value
        //      Draft on read — Hydrate() derives Active from Status).
        // Without (3) those legacy rows would be excluded, which doesn't
        // match Hydrate()'s read-side normalization elsewhere in the
        // repo.
        var stateFilter = b.Or(
            b.Eq(p => p.VersionState, ProviderVersionState.Active),
            b.Exists(p => p.VersionState, false),
            b.And(
                b.Or(
                    b.Exists(p => p.VersionId, false),
                    b.Eq(p => p.VersionId, null),
                    b.Eq(p => p.VersionId, string.Empty)),
                b.Eq(p => p.Status, ProviderStatus.Active)));

        var providerFilter = b.And(
            b.Eq(p => p.TenantId, query.TenantId),
            stateFilter,
            b.Or(b.Eq(p => p.TerminationDate, null), b.Gte(p => p.TerminationDate, asOf)),
            b.ElemMatch(p => p.NetworkParticipations, participationFilter));

        if (!string.IsNullOrEmpty(query.Specialty))
        {
            var rgx = new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(query.Specialty), "i");
            providerFilter = b.And(providerFilter,
                b.Or(b.Regex(p => p.PrimarySpecialty, rgx), b.Regex(p => p.TaxonomyCode, rgx)));
        }

        if (query.AcceptingNewPatients.HasValue)
        {
            providerFilter = b.And(providerFilter,
                b.Eq(p => p.AcceptingNewPatients, query.AcceptingNewPatients.Value));
        }

        // IntegrityScoreDesc needs nulls-last across the whole result
        // set, not just within a page. Find().Sort() places BSON null
        // first on Descending, which would put unverified providers at
        // the head of page 1 and push high-score rows into later pages.
        // Solution: aggregate with a computed has-score key and sort by
        // (hasScore desc, IntegrityScore desc, _id asc) BEFORE skip/limit.
        // The other sorts have well-defined Mongo semantics on non-null
        // string fields (LastName / OrganizationName), so they keep the
        // simpler Find path.
        if (sort == NetworkRosterSort.IntegrityScoreDesc)
        {
            var addFields = new MongoDB.Bson.BsonDocument("$addFields",
                new MongoDB.Bson.BsonDocument("hasScore",
                    new MongoDB.Bson.BsonDocument("$cond", new MongoDB.Bson.BsonArray
                    {
                        new MongoDB.Bson.BsonDocument("$gt", new MongoDB.Bson.BsonArray
                        {
                            "$IntegrityScore", MongoDB.Bson.BsonNull.Value
                        }),
                        1, 0
                    })));
            var sortStage = new MongoDB.Bson.BsonDocument("$sort", new MongoDB.Bson.BsonDocument
            {
                { "hasScore", -1 },
                { "IntegrityScore", -1 },
                { "_id", 1 },
            });

            var fluent = _collection.Aggregate()
                .Match(providerFilter)
                .AppendStage<Provider>(addFields)
                .AppendStage<Provider>(sortStage)
                .Skip(safeSkip)
                .Limit(pageSize);

            var aggDocs = await fluent.ToListAsync(ct);
            return aggDocs.Select(Hydrate).ToList();
        }

        var sortDef = sort switch
        {
            NetworkRosterSort.NameDesc =>
                Builders<Provider>.Sort
                    .Descending(p => p.LastName)
                    .Descending(p => p.OrganizationName)
                    .Descending(p => p.Id),
            _ =>
                Builders<Provider>.Sort
                    .Ascending(p => p.LastName)
                    .Ascending(p => p.OrganizationName)
                    .Ascending(p => p.Id),
        };

        var docs = await _collection.Find(providerFilter)
            .Sort(sortDef)
            .Skip(safeSkip)
            .Limit(pageSize)
            .ToListAsync(ct);

        return docs.Select(Hydrate).ToList();
    }

    private static FilterDefinition<Provider> ChainKeyFilter(string providerId)
    {
        var b = Builders<Provider>.Filter;
        return b.Or(
            b.Eq(p => p.ProviderId, providerId),
            b.And(
                b.Or(b.Eq(p => p.ProviderId, string.Empty), b.Exists(p => p.ProviderId, false)),
                b.Eq(p => p.Id, providerId)));
    }

    public async Task<Provider?> GetLatestActiveAsync(string providerId, DateTime asOf)
    {
        var tenantId = GetTenantId();
        var b = Builders<Provider>.Filter;

        // Legacy rows lack versionState entirely. Match either Active
        // explicitly or rows where the field is absent.
        var stateFilter = b.Or(
            b.Eq(x => x.VersionState, ProviderVersionState.Active),
            b.Exists(x => x.VersionState, false));

        var filter = b.And(
            ChainKeyFilter(providerId),
            b.Eq(x => x.TenantId, tenantId),
            stateFilter,
            b.Or(
                b.Eq(x => x.TerminationDate, null),
                b.Gte(x => x.TerminationDate, asOf)));

        var doc = await _collection.Find(filter)
            .SortByDescending(x => x.VersionNumber)
            .FirstOrDefaultAsync();
        return doc == null ? null : Hydrate(doc);
    }

    public async Task<Provider?> GetVersionAsync(string providerId, string versionId)
    {
        var tenantId = GetTenantId();
        var b = Builders<Provider>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            ChainKeyFilter(providerId),
            b.Eq(x => x.VersionId, versionId));
        var doc = await _collection.Find(filter).FirstOrDefaultAsync();
        return doc == null ? null : Hydrate(doc);
    }

    public async Task<(IReadOnlyList<Provider> Items, string? ContinuationToken)> ListVersionsAsync(
        string providerId, int pageSize, string? continuationToken)
    {
        var tenantId = GetTenantId();
        var skip = 0;
        if (!string.IsNullOrEmpty(continuationToken) &&
            int.TryParse(continuationToken, out var parsed) && parsed > 0)
        {
            skip = parsed;
        }

        var b = Builders<Provider>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            ChainKeyFilter(providerId));

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

    public async Task<Provider> CreateDraftAsync(Provider draft)
    {
        var tenantId = GetTenantId();
        if (string.IsNullOrEmpty(draft.TenantId)) draft.TenantId = tenantId;
        if (string.IsNullOrEmpty(draft.Id)) draft.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(draft.ProviderId)) draft.ProviderId = draft.Id;
        draft.VersionState = ProviderVersionState.Draft;
        draft.LastUpdatedDate = DateTime.UtcNow;
        await _collection.InsertOneAsync(draft);
        return draft;
    }

    public async Task<Provider> UpdateDraftAsync(Provider draft)
    {
        var b = Builders<Provider>.Filter;
        var filter = b.And(
            b.Eq(x => x.Id, draft.Id),
            b.Eq(x => x.TenantId, draft.TenantId));

        var existing = await _collection.Find(filter).FirstOrDefaultAsync()
            ?? throw new ProviderVersionStateException(draft.ProviderId, draft.VersionId, ProviderVersionState.Draft,
                $"Draft {draft.VersionId} not found") { IsNotFound = true };

        if (existing.VersionState != ProviderVersionState.Draft)
        {
            throw new ProviderVersionStateException(
                existing.ProviderId, existing.VersionId, existing.VersionState,
                $"Provider version {existing.VersionId} is {existing.VersionState} and cannot be edited.");
        }

        draft.LastUpdatedDate = DateTime.UtcNow;
        draft.VersionState = ProviderVersionState.Draft;

        await _collection.ReplaceOneAsync(filter, draft);
        return draft;
    }

    public async Task<Provider> ActivateAndSupersedeAsync(Provider draftToActivate, Provider? predecessor)
    {
        if (draftToActivate.VersionState != ProviderVersionState.Active)
        {
            throw new InvalidOperationException(
                "ActivateAndSupersedeAsync expects draftToActivate to already have VersionState=Active applied by the service layer.");
        }

        draftToActivate.LastUpdatedDate = DateTime.UtcNow;
        if (predecessor != null) predecessor.LastUpdatedDate = DateTime.UtcNow;

        var activateFilter = Builders<Provider>.Filter.And(
            Builders<Provider>.Filter.Eq(x => x.Id, draftToActivate.Id),
            Builders<Provider>.Filter.Eq(x => x.TenantId, draftToActivate.TenantId));

        // Try a session transaction (replica set required); fall back to
        // sequential writes when the deployment is a single-node Mongo
        // instance — log a compensating-action warning so ops can spot it.
        try
        {
            using var session = await _database.Client.StartSessionAsync();
            session.StartTransaction();
            try
            {
                await _collection.ReplaceOneAsync(session, activateFilter, draftToActivate);

                if (predecessor != null)
                {
                    var predFilter = Builders<Provider>.Filter.And(
                        Builders<Provider>.Filter.Eq(x => x.Id, predecessor.Id),
                        Builders<Provider>.Filter.Eq(x => x.TenantId, predecessor.TenantId));
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
            // Mongo errors when transactions aren't supported on the deployment.
            return await ActivateAndSupersedeWithoutTransactionAsync(draftToActivate, predecessor, activateFilter);
        }
    }

    private async Task<Provider> ActivateAndSupersedeWithoutTransactionAsync(
        Provider draftToActivate,
        Provider? predecessor,
        FilterDefinition<Provider> activateFilter)
    {
        _logger.LogWarning(
            "Mongo deployment does not support transactions; activating provider {ProviderId} version {VersionId} non-atomically. " +
            "Operators must verify the predecessor was superseded after the call.",
            draftToActivate.Id, draftToActivate.VersionId);

        await _collection.ReplaceOneAsync(activateFilter, draftToActivate);

        if (predecessor != null)
        {
            var predFilter = Builders<Provider>.Filter.And(
                Builders<Provider>.Filter.Eq(x => x.Id, predecessor.Id),
                Builders<Provider>.Filter.Eq(x => x.TenantId, predecessor.TenantId));
            await _collection.ReplaceOneAsync(predFilter, predecessor);
        }

        return draftToActivate;
    }

    public async Task<Provider> ReplaceVersionRowAsync(Provider version)
    {
        version.LastUpdatedDate = DateTime.UtcNow;
        var filter = Builders<Provider>.Filter.And(
            Builders<Provider>.Filter.Eq(x => x.Id, version.Id),
            Builders<Provider>.Filter.Eq(x => x.TenantId, version.TenantId));
        await _collection.ReplaceOneAsync(filter, version);
        return version;
    }

    public async Task<bool> UpdateIntegrityProjectionAsync(
        string tenantId,
        string providerId,
        int? integrityScore,
        string? integrityRating,
        DateTimeOffset? lastVerifiedAt,
        DateTimeOffset? nextVerificationDue,
        CancellationToken ct = default)
    {
        // $set on the four projection fields only — bypasses the
        // version-state guard on UpdateAsync. Targets the head Active
        // version of the chain (matching ChainKeyFilter + Active state).
        //
        // Legacy-row hydration rule (mirrors Hydrate()): a row counts as
        // Active when ANY of the following hold:
        //   1. VersionState == Active (current versioned shape).
        //   2. VersionState missing AND Status == Active (rows that
        //      pre-date capability 5.1 — never had VersionState
        //      persisted).
        //   3. VersionId missing/empty AND Status == Active (rows that
        //      defaulted VersionState to enum-zero Draft on read; the
        //      Hydrate() fallback derives Active from Status when
        //      VersionId is unset).
        // The Status guard on branches 2 and 3 is non-negotiable — without
        // it, legacy Terminated/Suspended rows would be patched, violating
        // the method contract ("returns false when no Active head exists").
        // See docs/architecture/provider-versioning.md "Legacy hydration
        // query pattern".
        var b = Builders<Provider>.Filter;
        var stateFilter = b.Or(
            b.Eq(p => p.VersionState, ProviderVersionState.Active),
            b.And(
                b.Exists(p => p.VersionState, false),
                b.Eq(p => p.Status, ProviderStatus.Active)),
            b.And(
                b.Or(
                    b.Exists(p => p.VersionId, false),
                    b.Eq(p => p.VersionId, null),
                    b.Eq(p => p.VersionId, string.Empty)),
                b.Eq(p => p.Status, ProviderStatus.Active)));

        var filter = b.And(
            b.Eq(p => p.TenantId, tenantId),
            ChainKeyFilter(providerId),
            stateFilter);

        var update = Builders<Provider>.Update
            .Set(p => p.IntegrityScore, integrityScore)
            .Set(p => p.IntegrityRating, integrityRating)
            .Set(p => p.LastVerifiedAt, lastVerifiedAt)
            .Set(p => p.NextVerificationDue, nextVerificationDue)
            .Set(p => p.LastUpdatedDate, DateTime.UtcNow);

        // Sort by VersionNumber desc so amendments hit the latest head when
        // there are historical Superseded rows. Mongo's UpdateOneAsync with
        // a Sort option requires FindOneAndUpdate semantics; use that.
        var options = new FindOneAndUpdateOptions<Provider>
        {
            Sort = Builders<Provider>.Sort.Descending(p => p.VersionNumber),
            ReturnDocument = ReturnDocument.After,
        };
        var updated = await _collection.FindOneAndUpdateAsync(filter, update, options, ct);
        return updated != null;
    }

    public async Task<bool> UpdateCredentialingProjectionAsync(
        string tenantId,
        string providerId,
        CredentialingStatus status,
        DateTime? credentialingDate,
        DateTime? recredentialingDueDate,
        CancellationToken ct = default)
    {
        // Mirror UpdateIntegrityProjectionAsync: $set on the three
        // credentialing projection fields only — bypasses the
        // version-state guard on UpdateAsync. Targets the head Active
        // version of the chain (matching ChainKeyFilter + Active state).
        // Hydration rule (three "Active" shapes, each Status-gated) is
        // identical.
        var b = Builders<Provider>.Filter;
        var stateFilter = b.Or(
            b.Eq(p => p.VersionState, ProviderVersionState.Active),
            b.And(
                b.Exists(p => p.VersionState, false),
                b.Eq(p => p.Status, ProviderStatus.Active)),
            b.And(
                b.Or(
                    b.Exists(p => p.VersionId, false),
                    b.Eq(p => p.VersionId, null),
                    b.Eq(p => p.VersionId, string.Empty)),
                b.Eq(p => p.Status, ProviderStatus.Active)));

        var filter = b.And(
            b.Eq(p => p.TenantId, tenantId),
            ChainKeyFilter(providerId),
            stateFilter);

        var update = Builders<Provider>.Update
            .Set(p => p.CredentialingStatus, status)
            .Set(p => p.CredentialingDate, credentialingDate)
            .Set(p => p.RecredentialingDueDate, recredentialingDueDate)
            .Set(p => p.LastUpdatedDate, DateTime.UtcNow);

        var options = new FindOneAndUpdateOptions<Provider>
        {
            Sort = Builders<Provider>.Sort.Descending(p => p.VersionNumber),
            ReturnDocument = ReturnDocument.After,
        };
        var updated = await _collection.FindOneAndUpdateAsync(filter, update, options, ct);
        return updated != null;
    }

    public async Task<IReadOnlyList<Provider>> ListProvidersForIntegrityRefreshAsync(
        string tenantId,
        DateTimeOffset dueBefore,
        bool includeNeverVerified,
        int skip,
        int pageSize,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId))
            throw new ArgumentException("tenantId is required.", nameof(tenantId));
        var safeSkip = Math.Max(skip, 0);
        var safePageSize = Math.Clamp(pageSize, 1, 1000);

        // Hydration rule (mirrors Hydrate()) — three "Active" shapes,
        // each Status-gated to keep legacy Terminated/Suspended rows
        // out of refresh batches:
        //   1. VersionState == Active.
        //   2. VersionState missing AND Status == Active.
        //   3. VersionId missing/empty AND Status == Active.
        // See docs/architecture/provider-versioning.md "Legacy
        // hydration query pattern".
        var b = Builders<Provider>.Filter;
        var stateFilter = b.Or(
            b.Eq(p => p.VersionState, ProviderVersionState.Active),
            b.And(
                b.Exists(p => p.VersionState, false),
                b.Eq(p => p.Status, ProviderStatus.Active)),
            b.And(
                b.Or(
                    b.Exists(p => p.VersionId, false),
                    b.Eq(p => p.VersionId, null),
                    b.Eq(p => p.VersionId, string.Empty)),
                b.Eq(p => p.Status, ProviderStatus.Active)));

        var dueFilter = includeNeverVerified
            ? b.Or(
                b.Exists(p => p.NextVerificationDue, false),
                b.Eq(p => p.NextVerificationDue, null),
                b.Lte(p => p.NextVerificationDue, dueBefore))
            : b.And(
                b.Exists(p => p.NextVerificationDue, true),
                b.Ne(p => p.NextVerificationDue, null),
                b.Lte(p => p.NextVerificationDue, dueBefore));

        var filter = b.And(
            b.Eq(p => p.TenantId, tenantId),
            stateFilter,
            dueFilter);

        var docs = await _collection.Find(filter)
            .Sort(Builders<Provider>.Sort.Ascending(p => p.ProviderId).Ascending(p => p.Id))
            .Skip(safeSkip)
            .Limit(safePageSize)
            .ToListAsync(ct);
        return docs.Select(Hydrate).ToList();
    }

    public async Task<IReadOnlyList<string>> ListProviderTenantIdsAsync(CancellationToken ct = default)
    {
        var distinct = await _collection.DistinctAsync<string>(
            "TenantId", FilterDefinition<Provider>.Empty, cancellationToken: ct);
        var list = await distinct.ToListAsync(ct);
        return list.Where(x => !string.IsNullOrEmpty(x))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<long> CountStaleProvidersAsync(
        string tenantId,
        DateTimeOffset staleBefore,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId))
            throw new ArgumentException("tenantId is required.", nameof(tenantId));

        // Hydration rule (mirrors ListProvidersForIntegrityRefreshAsync) —
        // three "Active" shapes, each Status-gated. A provider is stale
        // when LastVerifiedAt is missing/null or older than staleBefore.
        var b = Builders<Provider>.Filter;
        var stateFilter = b.Or(
            b.Eq(p => p.VersionState, ProviderVersionState.Active),
            b.And(
                b.Exists(p => p.VersionState, false),
                b.Eq(p => p.Status, ProviderStatus.Active)),
            b.And(
                b.Or(
                    b.Exists(p => p.VersionId, false),
                    b.Eq(p => p.VersionId, null),
                    b.Eq(p => p.VersionId, string.Empty)),
                b.Eq(p => p.Status, ProviderStatus.Active)));

        var stalenessFilter = b.Or(
            b.Exists(p => p.LastVerifiedAt, false),
            b.Eq(p => p.LastVerifiedAt, null),
            b.Lt(p => p.LastVerifiedAt, staleBefore));

        var filter = b.And(
            b.Eq(p => p.TenantId, tenantId),
            stateFilter,
            stalenessFilter);

        return await _collection.CountDocumentsAsync(filter, cancellationToken: ct);
    }

    public async Task<bool> UpdatePanelGatingDefaultsAsync(
        string tenantId,
        string providerId,
        int participationIndex,
        PanelGatingFields fields,
        CancellationToken ct = default)
    {
        if (participationIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(participationIndex));
        if (fields == null) throw new ArgumentNullException(nameof(fields));

        // Match the same three Active shapes as
        // UpdateIntegrityProjectionAsync. The patch is positional within
        // the networkParticipations array — addressed by index — and
        // bypasses the version-state guard on UpdateAsync.
        var b = Builders<Provider>.Filter;
        var stateFilter = b.Or(
            b.Eq(p => p.VersionState, ProviderVersionState.Active),
            b.And(
                b.Exists(p => p.VersionState, false),
                b.Eq(p => p.Status, ProviderStatus.Active)),
            b.And(
                b.Or(
                    b.Exists(p => p.VersionId, false),
                    b.Eq(p => p.VersionId, null),
                    b.Eq(p => p.VersionId, string.Empty)),
                b.Eq(p => p.Status, ProviderStatus.Active)));

        // Bounds-check filter at the storage layer: the patch is
        // positional, so a $set against an out-of-range array slot
        // would extend the array with nulls. Filter on the existence
        // of the specific path (NetworkParticipations.{idx}) so the
        // FindOneAndUpdate returns null when the row has fewer
        // participations than expected.
        var sizeFilter = b.Exists($"NetworkParticipations.{participationIndex}", true);

        var filter = b.And(
            b.Eq(p => p.TenantId, tenantId),
            ChainKeyFilter(providerId),
            stateFilter,
            sizeFilter);

        // Positional update via array index. Mongo uses dot-notation
        // for nested array element fields:
        // networkParticipations.{idx}.panelLimit etc. Each $set hits
        // exactly one slot.
        var prefix = $"NetworkParticipations.{participationIndex}";
        var update = Builders<Provider>.Update
            .Set($"{prefix}.PanelLimit", fields.PanelLimit)
            .Set($"{prefix}.PanelAccepted", fields.PanelAccepted)
            .Set($"{prefix}.AcceptedLobs", fields.AcceptedLobs.ToList())
            .Set($"{prefix}.MinAcceptedAgeYears", fields.MinAcceptedAgeYears)
            .Set($"{prefix}.MaxAcceptedAgeYears", fields.MaxAcceptedAgeYears)
            .Set(p => p.LastUpdatedDate, DateTime.UtcNow);

        // Sort by VersionNumber desc so amendments hit the latest head
        // when there are historical Superseded rows. FindOneAndUpdate
        // is the only Mongo write that supports Sort.
        var options = new FindOneAndUpdateOptions<Provider>
        {
            Sort = Builders<Provider>.Sort.Descending(p => p.VersionNumber),
            ReturnDocument = ReturnDocument.After,
        };
        var updated = await _collection.FindOneAndUpdateAsync(filter, update, options, ct);
        return updated != null;
    }

    public async Task<IReadOnlyList<Provider>> ListProvidersForPanelGatingBackfillAsync(
        string tenantId,
        int skip,
        int pageSize,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId))
            throw new ArgumentException("tenantId is required.", nameof(tenantId));
        var safeSkip = Math.Max(skip, 0);
        var safePageSize = Math.Clamp(pageSize, 1, 1000);

        var b = Builders<Provider>.Filter;
        var stateFilter = b.Or(
            b.Eq(p => p.VersionState, ProviderVersionState.Active),
            b.And(
                b.Exists(p => p.VersionState, false),
                b.Eq(p => p.Status, ProviderStatus.Active)),
            b.And(
                b.Or(
                    b.Exists(p => p.VersionId, false),
                    b.Eq(p => p.VersionId, null),
                    b.Eq(p => p.VersionId, string.Empty)),
                b.Eq(p => p.Status, ProviderStatus.Active)));

        // Storage-layer superset filter: any row with at least one
        // participation that has PanelLimit unset is a candidate. The
        // service-layer eligibility check (all five fields at type
        // defaults) is authoritative; a false-positive page entry just
        // results in a no-op skip.
        var eligibleParticipationFilter = b.ElemMatch(p => p.NetworkParticipations,
            Builders<NetworkParticipation>.Filter.And(
                Builders<NetworkParticipation>.Filter.Or(
                    Builders<NetworkParticipation>.Filter.Exists(np => np.PanelLimit, false),
                    Builders<NetworkParticipation>.Filter.Eq(np => np.PanelLimit, null)),
                Builders<NetworkParticipation>.Filter.Or(
                    Builders<NetworkParticipation>.Filter.Exists(np => np.PanelAccepted, false),
                    Builders<NetworkParticipation>.Filter.Eq(np => np.PanelAccepted, null)),
                Builders<NetworkParticipation>.Filter.Or(
                    Builders<NetworkParticipation>.Filter.Exists(np => np.MinAcceptedAgeYears, false),
                    Builders<NetworkParticipation>.Filter.Eq(np => np.MinAcceptedAgeYears, null)),
                Builders<NetworkParticipation>.Filter.Or(
                    Builders<NetworkParticipation>.Filter.Exists(np => np.MaxAcceptedAgeYears, false),
                    Builders<NetworkParticipation>.Filter.Eq(np => np.MaxAcceptedAgeYears, null))));

        var filter = b.And(
            b.Eq(p => p.TenantId, tenantId),
            stateFilter,
            eligibleParticipationFilter);

        var docs = await _collection.Find(filter)
            .Sort(Builders<Provider>.Sort.Ascending(p => p.ProviderId).Ascending(p => p.Id))
            .Skip(safeSkip)
            .Limit(safePageSize)
            .ToListAsync(ct);
        return docs.Select(Hydrate).ToList();
    }

    private static Provider Hydrate(Provider provider)
    {
        if (string.IsNullOrEmpty(provider.ProviderId))
        {
            provider.ProviderId = provider.Id;
        }

        if (string.IsNullOrEmpty(provider.VersionId))
        {
            provider.VersionId = provider.Id;
            provider.VersionNumber = provider.VersionNumber <= 0 ? 1 : provider.VersionNumber;
            provider.VersionState = provider.Status switch
            {
                ProviderStatus.Terminated => ProviderVersionState.Terminated,
                ProviderStatus.Inactive => ProviderVersionState.Suspended,
                ProviderStatus.Pending => ProviderVersionState.Draft,
                _ => ProviderVersionState.Active
            };
        }

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
