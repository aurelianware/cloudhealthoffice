using AuthorizationService.Exceptions;
using AuthorizationService.Models;
using AuthorizationService.Services.Retention;
using MongoDB.Driver;
using MongoDB.Bson;

namespace AuthorizationService.Repositories;

public class AuthorizationRepositoryMongo : IAuthorizationRepository
{
    private readonly IMongoCollection<Authorization> _collection;
    private readonly AuthorizationIndexes _indexes;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuthorizationRepositoryMongo> _logger;

    public AuthorizationRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuthorizationRepositoryMongo> logger,
        AuthorizationIndexes? indexes = null)
    {
        var collectionName = configuration["CosmosDb:ContainerName"] ?? "Authorizations";
        _collection = database.GetCollection<Authorization>(collectionName);
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _indexes = indexes ?? new AuthorizationIndexes();
    }

    public Task EnsureIndexesAsync(CancellationToken ct = default) => _indexes.EnsureAsync(_collection, ct);

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new InvalidOperationException("TenantId not found in request context");
        }
        return tenantId;
    }

    public async Task<Authorization?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Authorization>.Filter.And(
            Builders<Authorization>.Filter.Eq(x => x.Id, id),
            Builders<Authorization>.Filter.Eq(x => x.TenantId, tenantId)
        );
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<Authorization?> GetByAuthorizationNumberAsync(string authorizationNumber)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Authorization>.Filter.And(
            Builders<Authorization>.Filter.Eq(x => x.AuthorizationNumber, authorizationNumber),
            Builders<Authorization>.Filter.Eq(x => x.TenantId, tenantId)
        );
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<Authorization>> SearchAsync(
        string? memberId,
        string? providerNPI,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        AuthorizationStatus? status,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize)
    {
        var tenantId = GetTenantId();
        var builder = Builders<Authorization>.Filter;
        var filter = builder.Eq(x => x.TenantId, tenantId);

        if (!string.IsNullOrEmpty(memberId))
        {
            filter &= builder.Eq(x => x.MemberId, memberId);
        }

        if (!string.IsNullOrEmpty(providerNPI))
        {
            filter &= builder.Or(
                builder.Eq(x => x.RequestingProviderNPI, providerNPI),
                builder.Eq(x => x.ServicingProviderNPI, providerNPI)
            );
        }

        if (serviceDateFrom.HasValue)
        {
            filter &= builder.Gte(x => x.RequestedServiceDateFrom, serviceDateFrom.Value);
        }

        if (serviceDateTo.HasValue)
        {
            filter &= builder.Or(
                builder.Lte(x => x.RequestedServiceDateTo, serviceDateTo.Value),
                builder.Eq(x => x.RequestedServiceDateTo, null)
            );
        }

        if (status.HasValue)
        {
            filter &= builder.Eq(x => x.Status, status.Value);
        }

        if (lineOfBusiness.HasValue)
        {
            filter &= builder.Eq(x => x.LineOfBusiness, lineOfBusiness.Value);
        }

        return await _collection.Find(filter)
            .SortByDescending(x => x.SubmittedDate)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<AuthorizationsSummary> GetAuthorizationsSummaryAsync(DateTime from, DateTime to, LineOfBusiness? lineOfBusiness)
    {
        var tenantId = GetTenantId();
        var builder = Builders<Authorization>.Filter;
        var filter = builder.And(
            builder.Eq(x => x.TenantId, tenantId),
            builder.Gte(x => x.SubmittedDate, from),
            builder.Lte(x => x.SubmittedDate, to)
        );

        if (lineOfBusiness.HasValue)
        {
            filter &= builder.Eq(x => x.LineOfBusiness, lineOfBusiness.Value);
        }

        var results = await _collection.Find(filter).ToListAsync();

        var summary = new AuthorizationsSummary
        {
            TotalAuthorizations = results.Count,
            ApprovedAuthorizations = results.Count(x => x.Status == AuthorizationStatus.Approved),
            DeniedAuthorizations = results.Count(x => x.Status == AuthorizationStatus.Denied),
            PendedAuthorizations = results.Count(x => x.Status == AuthorizationStatus.Pended),
            ModifiedAuthorizations = results.Count(x => x.Status == AuthorizationStatus.Modified),
            ExpiredAuthorizations = results.Count(x => x.Status == AuthorizationStatus.Expired)
        };

        if (summary.TotalAuthorizations > 0)
        {
            summary.ApprovalRate = (decimal)(summary.ApprovedAuthorizations + summary.ModifiedAuthorizations) / summary.TotalAuthorizations * 100;
        }

        var reviewedAuths = results.Where(x => x.ReviewedDate.HasValue).ToList();
        if (reviewedAuths.Any())
        {
            summary.AverageReviewDays = (decimal)reviewedAuths.Average(x => (x.ReviewedDate!.Value - x.SubmittedDate).TotalDays);
        }

        return summary;
    }

    // ── Retention (PAT-03) ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> ListTenantIdsAsync(CancellationToken ct = default)
    {
        var tenants = await _collection
            .Distinct(new StringFieldDefinition<Authorization, string>("TenantId"),
                Builders<Authorization>.Filter.Empty,
                cancellationToken: ct)
            .ToListAsync(ct);

        return tenants.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
    }

    public async Task<IReadOnlyList<Authorization>> FindRetentionCandidatesAsync(
        string tenantId, DateTime anchorCutoffUtc, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant is required for a retention sweep.", nameof(tenantId));

        var builder = Builders<Authorization>.Filter;

        // Terminal statuses only, bounded, and tenant-scoped. SubmittedDate is a
        // COARSE floor — a record submitted after the cutoff cannot have a last
        // status change before it — so this over-selects safely and the policy
        // makes the real per-record decision.
        var filter = builder.Eq(x => x.TenantId, tenantId)
                     & builder.In(x => x.Status, new[]
                       {
                           AuthorizationStatus.Approved,
                           AuthorizationStatus.Modified,
                           AuthorizationStatus.Denied,
                           AuthorizationStatus.Expired,
                           AuthorizationStatus.Cancelled,
                       })
                     & builder.Lte(x => x.SubmittedDate, anchorCutoffUtc);

        // Ordered oldest-first: a bounded
        // query without a sort can re-scan one subset forever and never reach the
        // oldest records. Matches SearchAsync's existing SubmittedDate ordering.
        return await _collection
            .Find(filter)
            .SortBy(x => x.SubmittedDate)
            .Limit(limit)
            .ToListAsync(ct);
    }

    public async Task<bool> PurgeIfStillEligibleAsync(
        string tenantId, string id, AuthorizationStatus expectedStatus, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant is required for a retention purge.", nameof(tenantId));

        if (expectedStatus.IsOpen())
            return false;

        var builder = Builders<Authorization>.Filter;

        // The status predicate is part of the DELETE, so the check and the
        // delete are one atomic operation: a record that moved back to an open
        // state between being listed and being purged no longer matches, and
        // survives. No read-then-blind-delete window.
        var filter = builder.Eq(x => x.Id, id)
                     & builder.Eq(x => x.TenantId, tenantId)
                     & builder.Eq(x => x.Status, expectedStatus);

        var result = await _collection.DeleteOneAsync(filter, ct);
        return result.DeletedCount == 1;
    }

    public async Task<IEnumerable<Authorization>> GetOpenAuthorizationsAsync(string? tenantId = null)
    {
        var effectiveTenantId = tenantId ?? GetTenantId();
        var builder = Builders<Authorization>.Filter;
        var filter = builder.And(
            builder.Eq(x => x.TenantId, effectiveTenantId),
            builder.In(x => x.Status, new[]
            {
                AuthorizationStatus.Submitted,
                AuthorizationStatus.InReview,
                AuthorizationStatus.Pended
            })
        );

        return await _collection.Find(filter).ToListAsync();
    }

    public async Task<IReadOnlyList<Authorization>> ListApprovedEndingBetweenAsync(DateTime from, DateTime to)
    {
        var b = Builders<Authorization>.Filter;
        var filter = b.Eq(x => x.TenantId, GetTenantId())
                     & b.In(x => x.Status, new[] { AuthorizationStatus.Approved, AuthorizationStatus.Modified })
                     & b.Or(
                         b.And(b.Gte(x => x.ApprovedServiceDateTo, from), b.Lte(x => x.ApprovedServiceDateTo, to)),
                         b.And(b.Eq(x => x.ApprovedServiceDateTo, null), b.Gte(x => x.ExpirationDate, from), b.Lte(x => x.ExpirationDate, to)));
        return await _collection.Find(filter).ToListAsync();
    }

    public async Task<IEnumerable<Authorization>> ListActivityBetweenAsync(DateTime from, DateTime to)
    {
        var tenantId = GetTenantId();
        var builder = Builders<Authorization>.Filter;
        var filter = builder.And(
            builder.Eq(x => x.TenantId, tenantId),
            builder.Or(
                builder.And(builder.Gte(x => x.SubmittedDate, from), builder.Lte(x => x.SubmittedDate, to)),
                builder.And(builder.Gte(x => x.ReviewedDate, from), builder.Lte(x => x.ReviewedDate, to))));
        return await _collection.Find(filter).ToListAsync();
    }

    public async Task<bool> RaiseSlaEscalationAsync(
        string tenantId, string id, SlaEscalationLevel level, DateTime escalatedAtUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant is required for an SLA escalation.", nameof(tenantId));

        var builder = Builders<Authorization>.Filter;

        // One conditional update: tenant, still open and still below the level
        // are part of the filter, and only the two escalation fields are set,
        // so a reviewer's concurrent change is never overwritten.
        var filter = builder.Eq(x => x.Id, id)
                     & builder.Eq(x => x.TenantId, tenantId)
                     & builder.In(x => x.Status, new[]
                       {
                           AuthorizationStatus.Submitted,
                           AuthorizationStatus.InReview,
                           AuthorizationStatus.Pended,
                       })
                     & builder.Or(
                           builder.Lt(x => x.SlaEscalation, level),
                           builder.Exists(x => x.SlaEscalation, false));

        var update = Builders<Authorization>.Update
            .Set(x => x.SlaEscalation, level)
            .Set(x => x.SlaEscalatedAt, escalatedAtUtc);

        // Bumped: a replace based on a read from before the escalation then
        // conflicts instead of erasing it.
        var result = await _collection.UpdateOneAsync(filter, Bumped(update), cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task<Authorization> CreateAsync(Authorization authorization)
    {
        var tenantId = GetTenantId();
        authorization.TenantId = tenantId;
        authorization.Id ??= Guid.NewGuid().ToString();
        authorization.Revision = 0;
        AuthorizationService.Services.AuthorizationDeadlinePolicy.Initialize(authorization);

        await EnsureIndexesAsync(_httpContextAccessor.HttpContext?.RequestAborted ?? default);
        try
        {
            await _collection.InsertOneAsync(authorization);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            if (await GetByAuthorizationNumberAsync(authorization.AuthorizationNumber) is not null)
                throw new DuplicateAuthorizationNumberException(authorization.AuthorizationNumber, ex);
            throw;
        }
        return authorization;
    }

    /// <summary>Every write to an authorization document bumps its <see cref="Authorization.Revision"/>.</summary>
    private static UpdateDefinition<Authorization> Bumped(UpdateDefinition<Authorization> update) =>
        update.Inc(x => x.Revision, 1L);

    /// <summary>The document still has the revision the write was based on (0 matches documents written before the field existed).</summary>
    private static FilterDefinition<Authorization> RevisionIs(long expected)
    {
        var b = Builders<Authorization>.Filter;
        return expected == 0
            ? b.Or(b.Eq(x => x.Revision, 0L), b.Exists(x => x.Revision, false))
            : b.Eq(x => x.Revision, expected);
    }

    public async Task<bool> EnsureDecisionDeadlineAsync(
        string tenantId, Authorization authorization, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || authorization.TenantId != tenantId)
            throw new ArgumentException("A matching tenant is required for deadline initialization.");
        var b = Builders<Authorization>.Filter;
        var filter = b.Eq(x => x.Id, authorization.Id) & b.Eq(x => x.TenantId, tenantId)
            & RevisionIs(authorization.Revision)
            & b.In(x => x.Status, new[] { AuthorizationStatus.Submitted, AuthorizationStatus.InReview, AuthorizationStatus.Pended })
            & b.Or(b.Eq(x => x.DecisionDueAt, null), b.Eq(x => x.DecisionReceivedAt, null));
        // A legacy record receives the original receipt target, never a new clock from now.
        var received = authorization.DecisionReceivedAt ?? AuthorizationService.Services.AuthorizationDeadlinePolicy.Utc(authorization.SubmittedDate);
        var due = authorization.DecisionDueAt ?? AuthorizationService.Services.AuthorizationDeadlinePolicy.DueAt(received, authorization.LevelOfService);
        var result = await _collection.UpdateOneAsync(filter, Bumped(Builders<Authorization>.Update
            .Set(x => x.DecisionReceivedAt, received).Set(x => x.DecisionDueAt, due)
            .Set(x => x.DecisionDeadlinePolicy, AuthorizationService.Services.AuthorizationDeadlinePolicy.Version)), cancellationToken: ct);
        if (result.ModifiedCount != 1) return false;
        authorization.DecisionReceivedAt = received;
        authorization.DecisionDueAt = due;
        authorization.DecisionDeadlinePolicy = AuthorizationService.Services.AuthorizationDeadlinePolicy.Version;
        authorization.Revision++;
        return true;
    }

    public async Task<Authorization> UpdateAsync(Authorization authorization)
    {
        AuthorizationService.Services.AuthorizationDeadlinePolicy.InitializeMissing(authorization);
        await EnsureIndexesAsync(_httpContextAccessor.HttpContext?.RequestAborted ?? default);
        var tenantId = GetTenantId();
        authorization.TenantId = tenantId;

        var filter = Builders<Authorization>.Filter.And(
            Builders<Authorization>.Filter.Eq(x => x.Id, authorization.Id),
            Builders<Authorization>.Filter.Eq(x => x.TenantId, tenantId)
        );

        // Conditional on the revision the caller read: a concurrent write in
        // between (a reviewer decision, an SLA escalation, the RFAI consumer)
        // makes this one fail instead of being silently overwritten.
        var expected = authorization.Revision;
        authorization.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(filter & RevisionIs(expected), authorization);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            authorization.Revision = expected;
            throw new DuplicateAuthorizationNumberException(authorization.AuthorizationNumber, ex);
        }
        catch
        {
            authorization.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            authorization.Revision = expected;
            if (await _collection.Find(filter).AnyAsync())
                throw new AuthorizationConcurrencyException(authorization.Id, expected);
            // Missing: unchanged behaviour (nothing written, no error).
        }

        return authorization;
    }

    private static FilterDefinition<Authorization> ExpiryFilter(string tenantId, DateTime cutoffDateUtc)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant is required for expiry.");
        var b = Builders<Authorization>.Filter;
        return b.Eq(a => a.TenantId, tenantId)
            & b.In(a => a.Status, new[] { AuthorizationStatus.Approved, AuthorizationStatus.Modified })
            & b.Or(b.Ne(a => a.ExpirationDate, null) & b.Lt(a => a.ExpirationDate, cutoffDateUtc.Date),
                b.Ne(a => a.ApprovedServiceDateTo, null) & b.Lt(a => a.ApprovedServiceDateTo, cutoffDateUtc.Date));
    }

    public async Task<IReadOnlyList<Authorization>> FindExpiryCandidatesAsync(
        string tenantId, DateTime cutoffDateUtc, int limit, CancellationToken ct = default) =>
        await _collection.Find(ExpiryFilter(tenantId, cutoffDateUtc)).SortBy(a => a.Id)
            .Limit(Math.Clamp(limit, 1, 1000)).ToListAsync(ct);

    public async Task<bool> ExpireIfStillEligibleAsync(
        string tenantId, Authorization authorization, DateTime cutoffDateUtc, DateTime changedAtUtc, CancellationToken ct = default)
    {
        if (authorization.TenantId != tenantId) throw new ArgumentException("A matching tenant is required for expiry.");
        var b = Builders<Authorization>.Filter;
        var filter = ExpiryFilter(tenantId, cutoffDateUtc) & b.Eq(a => a.Id, authorization.Id) & RevisionIs(authorization.Revision);
        var history = new AuthorizationStatusChange
        {
            Status = AuthorizationStatus.Expired, ChangedAt = changedAtUtc,
            ChangedBy = "authorization-expiry", Source = "authorization-expiry",
            Reason = "Approved authorization period ended.",
            LineDecisions = AuthorizationService.Backends.AuthorizationDecisionPolicy.Snapshot(authorization),
        };
        var update = Builders<Authorization>.Update.Set(a => a.Status, AuthorizationStatus.Expired)
            .Set(a => a.ReviewDecision, null).Set(a => a.LastUpdatedDate, changedAtUtc)
            .Set(a => a.LastUpdatedBy, "authorization-expiry");
        update = authorization.StatusHistory is null
            ? update.Set(a => a.StatusHistory, new List<AuthorizationStatusChange> { history })
            : update.Push(a => a.StatusHistory, history);
        // No full-document replacement: stale review/extension/cancellation writes cannot be overwritten.
        var result = await _collection.UpdateOneAsync(filter, Bumped(update), cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Authorization>.Filter.And(
            Builders<Authorization>.Filter.Eq(x => x.Id, id),
            Builders<Authorization>.Filter.Eq(x => x.TenantId, tenantId)
        );
        await _collection.DeleteOneAsync(filter);
    }
}
