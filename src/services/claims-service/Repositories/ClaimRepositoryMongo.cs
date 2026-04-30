using ClaimsService.Exceptions;
using ClaimsService.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClaimsService.Repositories;

public class ClaimRepositoryMongo : IClaimRepository
{
    private readonly IMongoCollection<Claim> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ClaimRepositoryMongo> _logger;

    public ClaimRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ClaimRepositoryMongo> logger)
    {
        _collection = database.GetCollection<Claim>("Claims");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;

        // Ensure indexes (best effort on startup)
        var indexKeys = Builders<Claim>.IndexKeys;
        var indexModels = new List<CreateIndexModel<Claim>>
        {
            new CreateIndexModel<Claim>(indexKeys.Ascending(c => c.TenantId).Ascending(c => c.ClaimNumber)),
            new CreateIndexModel<Claim>(indexKeys.Ascending(c => c.TenantId).Ascending(c => c.MemberId)),
            new CreateIndexModel<Claim>(indexKeys.Ascending(c => c.TenantId).Ascending(c => c.SubmittedDate)),
            // Compound index for search
            new CreateIndexModel<Claim>(indexKeys.Ascending(c => c.TenantId).Ascending(c => c.ServiceDateFrom)),
            // Versioning chain key index — supports GetLatestVersion / ListVersions.
            new CreateIndexModel<Claim>(indexKeys.Ascending(c => c.TenantId).Ascending(c => c.ClaimVersionId).Descending(c => c.VersionNumber))
        };

        _collection.Indexes.CreateMany(indexModels);
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
        {
            // Fallback for background services or testing if no context.
            // In a real scenario, we might default to a specific behavior or throw
            // throw new InvalidOperationException("TenantId not found in request context -- Mongo repo");
            return "unknown";
        }
        return tenantId;
    }

    /// <summary>
    /// Hydrates legacy claim documents (predating versioning fields) with
    /// sensible defaults. Idempotent.
    /// </summary>
    private static Claim Hydrate(Claim claim)
    {
        if (string.IsNullOrEmpty(claim.ClaimVersionId))
        {
            claim.ClaimVersionId = claim.Id;
        }
        if (claim.VersionNumber == 0)
        {
            claim.VersionNumber = 1;
        }
        if (claim.VersionState == ClaimVersionState.Unknown)
        {
            claim.VersionState = ClaimRepository.MapStatusToVersionState(claim.Status);
        }
        return claim;
    }

    private static bool IsTerminal(ClaimVersionState state) => state switch
    {
        ClaimVersionState.Paid or
        ClaimVersionState.Denied or
        ClaimVersionState.Voided or
        ClaimVersionState.Adjusted => true,
        _ => false
    };

    public async Task<Claim?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Claim>.Filter.And(
            Builders<Claim>.Filter.Eq(c => c.Id, id),
            Builders<Claim>.Filter.Eq(c => c.TenantId, tenantId)
        );

        var doc = await _collection.Find(filter).FirstOrDefaultAsync();
        return doc != null ? Hydrate(doc) : null;
    }

    public async Task<Claim?> GetByClaimNumberAsync(string claimNumber)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Claim>.Filter.And(
            Builders<Claim>.Filter.Eq(c => c.ClaimNumber, claimNumber),
            Builders<Claim>.Filter.Eq(c => c.TenantId, tenantId)
        );

        var doc = await _collection.Find(filter).SortByDescending(c => c.VersionNumber).FirstOrDefaultAsync();
        return doc != null ? Hydrate(doc) : null;
    }

    public async Task<IEnumerable<Claim>> SearchAsync(
        string? memberId,
        string? providerNPI,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        ClaimStatus? status,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize)
    {
        var tenantId = GetTenantId();
        var builder = Builders<Claim>.Filter;
        var filter = builder.Eq(c => c.TenantId, tenantId);

        if (!string.IsNullOrEmpty(memberId))
        {
            filter = builder.And(filter, builder.Eq(c => c.MemberId, memberId));
        }

        if (!string.IsNullOrEmpty(providerNPI))
        {
            var providerFilter = builder.Or(
                builder.Eq(c => c.BillingProviderNPI, providerNPI),
                builder.Eq(c => c.RenderingProviderNPI, providerNPI)
            );
            filter = builder.And(filter, providerFilter);
        }

        if (serviceDateFrom.HasValue)
        {
            filter = builder.And(filter, builder.Gte(c => c.ServiceDateFrom, serviceDateFrom.Value));
        }

        if (serviceDateTo.HasValue)
        {
            filter = builder.And(filter, builder.Lte(c => c.ServiceDateTo, serviceDateTo.Value));
        }

        if (status.HasValue)
        {
            filter = builder.And(filter, builder.Eq(c => c.Status, status.Value));
        }

        if (lineOfBusiness.HasValue)
        {
            filter = builder.And(filter, builder.Eq(c => c.LineOfBusiness, lineOfBusiness.Value));
        }

        var docs = await _collection.Find(filter)
            .SortByDescending(c => c.SubmittedDate)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
        return docs.Select(Hydrate);
    }

    public async Task<(IReadOnlyList<Claim> Page, int TotalCount)> SearchForMemberAsync(
        string memberId,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        ClaimStatus? status,
        string? providerNPI,
        ClaimType? claimType,
        decimal? amountMin,
        decimal? amountMax,
        int page,
        int pageSize)
    {
        var tenantId = GetTenantId();
        var b = Builders<Claim>.Filter;
        var filter = b.And(
            b.Eq(c => c.TenantId, tenantId),
            b.Eq(c => c.MemberId, memberId));

        if (serviceDateFrom.HasValue)
            filter &= b.Gte(c => c.ServiceDateFrom, serviceDateFrom.Value);
        if (serviceDateTo.HasValue)
            filter &= b.Lte(c => c.ServiceDateTo, serviceDateTo.Value);
        if (status.HasValue)
            filter &= b.Eq(c => c.Status, status.Value);
        if (!string.IsNullOrEmpty(providerNPI))
            filter &= b.Or(
                b.Eq(c => c.BillingProviderNPI, providerNPI),
                b.Eq(c => c.RenderingProviderNPI, providerNPI));
        if (claimType.HasValue)
            filter &= b.Eq(c => c.ClaimType, claimType.Value);
        if (amountMin.HasValue)
            filter &= b.Gte(c => c.TotalChargeAmount, amountMin.Value);
        if (amountMax.HasValue)
            filter &= b.Lte(c => c.TotalChargeAmount, amountMax.Value);

        var totalCount = (int)await _collection.CountDocumentsAsync(filter);
        var items = await _collection.Find(filter)
            .SortByDescending(c => c.SubmittedDate)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();

        return (items.Select(Hydrate).ToList(), totalCount);
    }

    public async Task<ClaimsSummary> GetClaimsSummaryAsync(
        DateTime from,
        DateTime to,
        LineOfBusiness? lineOfBusiness)
    {
        var tenantId = GetTenantId();

        var builder = Builders<Claim>.Filter;
        var filter = builder.And(
            builder.Eq(c => c.TenantId, tenantId),
            builder.Gte(c => c.SubmittedDate, from),
            builder.Lte(c => c.SubmittedDate, to)
        );

        if (lineOfBusiness.HasValue)
        {
            filter = builder.And(filter, builder.Eq(c => c.LineOfBusiness, lineOfBusiness.Value));
        }

        var claims = await _collection.Find(filter).ToListAsync();

        // Perform aggregation in-memory as a simplified approach for Mongo migration
        // In a high-scale production, we would use the Mongo Aggregation Pipeline (Inject IMongoCollection and use Aggregate)

        var summary = new ClaimsSummary
        {
            TotalClaims = claims.Count,
            ApprovedClaims = claims.Count(c => c.Status.ToString() == "Approved"),
            DeniedClaims = claims.Count(c => c.Status.ToString() == "Denied"),
            PendedClaims = claims.Count(c => c.Status.ToString() == "Pended"),
            PaidClaims = claims.Count(c => c.Status.ToString() == "Paid"),
            TotalChargeAmount = claims.Sum(c => c.TotalChargeAmount),
            TotalAllowedAmount = claims.Sum(c => c.AdjudicationResult?.AllowedAmount ?? 0),
            TotalPaidAmount = claims.Sum(c => c.AdjudicationResult?.PayerPayment ?? 0)
        };

        return summary;
    }

    public async Task<Claim> CreateAsync(Claim claim)
    {
        var tenantId = GetTenantId();
        if (string.IsNullOrEmpty(claim.TenantId))
        {
            claim.TenantId = tenantId;
        }

        if (string.IsNullOrEmpty(claim.Id))
        {
            claim.Id = Guid.NewGuid().ToString();
        }

        // Initialize the version chain on first write. See ClaimRepository.CreateAsync
        // for the rationale; same defaults apply on the Mongo backend.
        if (string.IsNullOrEmpty(claim.ClaimVersionId))
        {
            claim.ClaimVersionId = claim.Id;
        }
        if (claim.VersionNumber == 0)
        {
            claim.VersionNumber = 1;
        }
        if (claim.VersionState == ClaimVersionState.Unknown)
        {
            claim.VersionState = ClaimRepository.MapStatusToVersionState(claim.Status);
        }

        await _collection.InsertOneAsync(claim);
        return claim;
    }

    public async Task<Claim> UpdateAsync(Claim claim)
    {
        var tenantId = GetTenantId();
        // Ensure tenant isolation
        if (claim.TenantId != tenantId)
        {
            throw new InvalidOperationException("Cross-tenant updates are not allowed.");
        }

        // Reject mutations on terminal versions. Mirrors ClaimRepository
        // (Cosmos): adjustments must go through the explicit "create new
        // version with PredecessorVersionId" path (capability 5.12).
        var filter = Builders<Claim>.Filter.And(
            Builders<Claim>.Filter.Eq(c => c.Id, claim.Id),
            Builders<Claim>.Filter.Eq(c => c.TenantId, tenantId)
        );
        var existing = await _collection.Find(filter).FirstOrDefaultAsync();
        if (existing != null)
        {
            var hydrated = Hydrate(existing);
            if (IsTerminal(hydrated.VersionState))
            {
                throw new ClaimVersionStateException(
                    hydrated.ClaimVersionId, hydrated.Id, hydrated.VersionState,
                    $"Claim version {hydrated.Id} is in terminal state {hydrated.VersionState} and cannot be updated. " +
                    "Create an adjustment version via the adjustment workflow.");
            }
        }

        var result = await _collection.ReplaceOneAsync(filter, claim);

        if (result.MatchedCount == 0)
        {
            // Surface a domain-specific not-found rather than a generic
            // Exception so the controller boundary can map to 404 (via
            // IsNotFound) instead of falling through ExceptionHandlingMiddleware
            // as a 500. Mirrors ProviderVersionStateException usage.
            throw new ClaimVersionStateException(
                claim.ClaimVersionId,
                claim.Id,
                claim.VersionState,
                $"Claim version {claim.Id} not found for update.")
            {
                IsNotFound = true
            };
        }

        return claim;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Claim>.Filter.And(
            Builders<Claim>.Filter.Eq(c => c.Id, id),
            Builders<Claim>.Filter.Eq(c => c.TenantId, tenantId)
        );

        await _collection.DeleteOneAsync(filter);
    }

    // ── Versioning surface (5.1) ─────────────────────────────────────────

    public async Task<Claim?> GetLatestVersionAsync(string claimVersionId, DateTime asOf)
    {
        var tenantId = GetTenantId();
        var b = Builders<Claim>.Filter;

        // Match either the new chain key or the legacy fallback (Id == chain key).
        var chainFilter = b.Or(
            b.Eq(c => c.ClaimVersionId, claimVersionId),
            b.And(
                b.Or(b.Eq(c => c.ClaimVersionId, string.Empty), b.Eq(c => c.ClaimVersionId, (string?)null)),
                b.Eq(c => c.Id, claimVersionId)));

        // "In effect at asOf" — PublishedAt <= asOf and either SupersededAt is
        // null or > asOf. Legacy rows missing PublishedAt/SupersededAt pass
        // through (the null cases match).
        var publishedFilter = b.Or(
            b.Eq(c => c.PublishedAt, (DateTime?)null),
            b.Lte(c => c.PublishedAt, asOf));
        var supersededFilter = b.Or(
            b.Eq(c => c.SupersededAt, (DateTime?)null),
            b.Gt(c => c.SupersededAt, asOf));

        var filter = b.And(
            b.Eq(c => c.TenantId, tenantId),
            chainFilter,
            b.Ne(c => c.VersionState, ClaimVersionState.Draft),
            publishedFilter,
            supersededFilter);

        var head = await _collection.Find(filter).SortByDescending(c => c.VersionNumber).FirstOrDefaultAsync();
        return head != null ? Hydrate(head) : null;
    }

    public async Task<Claim?> GetVersionAsync(string claimVersionId, string versionId)
    {
        var tenantId = GetTenantId();
        var b = Builders<Claim>.Filter;

        var chainFilter = b.Or(
            b.Eq(c => c.ClaimVersionId, claimVersionId),
            b.And(
                b.Or(b.Eq(c => c.ClaimVersionId, string.Empty), b.Eq(c => c.ClaimVersionId, (string?)null)),
                b.Eq(c => c.Id, claimVersionId)));

        var filter = b.And(
            b.Eq(c => c.TenantId, tenantId),
            b.Eq(c => c.Id, versionId),
            chainFilter);

        var match = await _collection.Find(filter).FirstOrDefaultAsync();
        return match != null ? Hydrate(match) : null;
    }

    public async Task<(IReadOnlyList<Claim> Items, string? ContinuationToken)> ListVersionsAsync(
        string claimVersionId, int pageSize, string? continuationToken)
    {
        var tenantId = GetTenantId();
        var b = Builders<Claim>.Filter;

        var chainFilter = b.Or(
            b.Eq(c => c.ClaimVersionId, claimVersionId),
            b.And(
                b.Or(b.Eq(c => c.ClaimVersionId, string.Empty), b.Eq(c => c.ClaimVersionId, (string?)null)),
                b.Eq(c => c.Id, claimVersionId)));

        var filter = b.And(b.Eq(c => c.TenantId, tenantId), chainFilter);

        // Mongo doesn't have first-class continuation tokens; we encode the
        // skip offset in the token. Callers that started on Cosmos with
        // server tokens won't pass them to Mongo (the dual-backend is
        // selected at startup), so this simple offset scheme is sufficient.
        var skip = 0;
        if (!string.IsNullOrEmpty(continuationToken) && int.TryParse(continuationToken, out var parsed))
        {
            skip = parsed;
        }

        var items = await _collection.Find(filter)
            .SortByDescending(c => c.VersionNumber)
            .Skip(skip)
            .Limit(pageSize)
            .ToListAsync();

        var hydrated = items.Select(Hydrate).ToList();
        var nextToken = hydrated.Count == pageSize ? (skip + pageSize).ToString() : null;
        return (hydrated, nextToken);
    }

    public async Task<bool> UpdateAdjudicationProjectionAsync(
        string tenantId,
        string claimVersionId,
        AdjudicationResult adjudicationResult,
        IReadOnlyList<LineAdjudicationResult> lineResults,
        CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;

        var chainFilter = b.Or(
            b.Eq(c => c.ClaimVersionId, claimVersionId),
            b.And(
                b.Or(b.Eq(c => c.ClaimVersionId, string.Empty), b.Eq(c => c.ClaimVersionId, (string?)null)),
                b.Eq(c => c.Id, claimVersionId)));

        // Adjudication runs against Submitted / Adjudicated rows (re-adjudication
        // allowed). Legacy rows may have VersionState=Unknown and only ClaimStatus
        // populated, so accept those too.
        var stateFilter = b.Or(
            b.Eq(c => c.VersionState, ClaimVersionState.Submitted),
            b.Eq(c => c.VersionState, ClaimVersionState.Adjudicated),
            b.Eq(c => c.VersionState, ClaimVersionState.Unknown));

        var filter = b.And(b.Eq(c => c.TenantId, tenantId), chainFilter, stateFilter);

        var head = await _collection.Find(filter)
            .SortByDescending(c => c.VersionNumber)
            .FirstOrDefaultAsync(ct);

        if (head == null) return false;

        // The filter accepts VersionState=Unknown so legacy rows (no
        // version fields) can still receive adjudication writes. But a
        // legacy row with Status=Paid hydrates to VersionState=Paid —
        // terminal, and must NOT be patched. Hydrate then enforce the
        // terminal-state guard before mutating.
        head = Hydrate(head);
        if (IsTerminal(head.VersionState)) return false;

        // Apply line adjudication results in claim-line order when shapes
        // agree. Mismatched counts → leave existing line results untouched.
        if (head.ClaimLines.Count == lineResults.Count)
        {
            for (var i = 0; i < head.ClaimLines.Count; i++)
            {
                head.ClaimLines[i].AdjudicationResult = lineResults[i];
            }
        }

        var update = Builders<Claim>.Update
            .Set(c => c.AdjudicationResult, adjudicationResult)
            .Set(c => c.ClaimLines, head.ClaimLines)
            .Set(c => c.LastUpdatedDate, DateTime.UtcNow);

        var rowFilter = b.And(b.Eq(c => c.TenantId, tenantId), b.Eq(c => c.Id, head.Id));
        var result = await _collection.UpdateOneAsync(rowFilter, update, cancellationToken: ct);
        return result.MatchedCount > 0;
    }

    public async Task<AccumulatorTotalsResponse> GetAccumulatorTotalsAsync(
        string ownerId,
        string scope,
        string benefitPlanId,
        string planYear,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();

        var yearStart = new DateTime(int.Parse(planYear), 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = new DateTime(int.Parse(planYear), 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var finalizedStatuses = new[] { ClaimStatus.Approved, ClaimStatus.PartiallyPaid, ClaimStatus.Paid };
        var finalizedVersionStates = new[] { ClaimVersionState.Adjudicated, ClaimVersionState.Paid };

        var builder = Builders<Claim>.Filter;
        var filter = builder.And(
            builder.Eq(c => c.TenantId, tenantId),
            builder.Eq(c => c.BenefitPlanId, benefitPlanId),
            builder.Gte(c => c.ServiceDateFrom, yearStart),
            builder.Lte(c => c.ServiceDateFrom, yearEnd),
            // Accept either the legacy ClaimStatus filter (which has always
            // worked correctly on Mongo) OR the new ClaimVersionState filter.
            // Either match keeps the row; this lets unhydrated legacy rows
            // continue to count while new versioned rows count once they
            // reach a finalized state.
            builder.Or(
                builder.In(c => c.Status, finalizedStatuses),
                builder.In(c => c.VersionState, finalizedVersionStates)),
            builder.Ne(c => c.AdjudicationResult, null)
        );

        // Add scope-specific owner filter
        if (scope == "Family")
            filter = builder.And(filter, builder.Eq(c => c.SubscriberId, ownerId));
        else
            filter = builder.And(filter, builder.Eq(c => c.MemberId, ownerId));

        var claims = await _collection
            .Find(filter)
            .Project(c => new
            {
                c.AdjudicationResult!.DeductibleAmount,
                c.AdjudicationResult.CoinsuranceAmount,
                c.AdjudicationResult.CopayAmount,
                c.AdjudicationResult.PatientResponsibility,
                c.AdjudicationResult.NetworkTier
            })
            .ToListAsync(ct);

        var deductibleType = scope == "Family" ? "FamilyDeductible"     : "IndividualDeductible";
        var oopType        = scope == "Family" ? "FamilyOutOfPocketMax"  : "IndividualOutOfPocketMax";

        var deductible  = new Dictionary<string, decimal>();
        var oop         = new Dictionary<string, decimal>();
        var coinsurance = new Dictionary<string, decimal>();
        var copay       = new Dictionary<string, decimal>();

        foreach (var row in claims)
        {
            var tier = row.NetworkTier ?? "InNetwork";
            deductible[tier]  = deductible.GetValueOrDefault(tier)  + row.DeductibleAmount;
            oop[tier]         = oop.GetValueOrDefault(tier)         + row.PatientResponsibility;
            coinsurance[tier] = coinsurance.GetValueOrDefault(tier) + row.CoinsuranceAmount;
            copay[tier]       = copay.GetValueOrDefault(tier)       + row.CopayAmount;
        }

        var totals = new List<AccumulatorTotalEntry>();
        foreach (var (tier, amount) in deductible)
            if (amount > 0) totals.Add(new AccumulatorTotalEntry { AccumulatorType = deductibleType, NetworkTier = tier, AccumulatedAmount = amount });
        foreach (var (tier, amount) in oop)
            if (amount > 0) totals.Add(new AccumulatorTotalEntry { AccumulatorType = oopType, NetworkTier = tier, AccumulatedAmount = amount });
        foreach (var (tier, amount) in coinsurance)
            if (amount > 0) totals.Add(new AccumulatorTotalEntry { AccumulatorType = "Coinsurance", NetworkTier = tier, AccumulatedAmount = amount });
        foreach (var (tier, amount) in copay)
            if (amount > 0) totals.Add(new AccumulatorTotalEntry { AccumulatorType = "Copay", NetworkTier = tier, AccumulatedAmount = amount });

        return new AccumulatorTotalsResponse { Totals = totals };
    }
}
