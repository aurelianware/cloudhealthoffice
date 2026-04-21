using ClaimsService.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Linq;
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
            new CreateIndexModel<Claim>(indexKeys.Ascending(c => c.TenantId).Ascending(c => c.ServiceDateFrom))
        };
        
        _collection.Indexes.CreateMany(indexModels);
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
        {
            // Fallback for background services or testing if no context
            // In a real scenario, we might default to a specific behavior or throw
             // throw new InvalidOperationException("TenantId not found in request context -- Mongo repo");
             return "unknown"; 
        }
        return tenantId;
    }

    public async Task<Claim?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Claim>.Filter.And(
            Builders<Claim>.Filter.Eq(c => c.Id, id),
            Builders<Claim>.Filter.Eq(c => c.TenantId, tenantId)
        );

        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<Claim?> GetByClaimNumberAsync(string claimNumber)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Claim>.Filter.And(
            Builders<Claim>.Filter.Eq(c => c.ClaimNumber, claimNumber),
            Builders<Claim>.Filter.Eq(c => c.TenantId, tenantId)
        );

        return await _collection.Find(filter).FirstOrDefaultAsync();
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

        return await _collection.Find(filter)
            .SortByDescending(c => c.SubmittedDate)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
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

        return (items, totalCount);
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

        var filter = Builders<Claim>.Filter.And(
            Builders<Claim>.Filter.Eq(c => c.Id, claim.Id),
            Builders<Claim>.Filter.Eq(c => c.TenantId, tenantId)
        );

        var result = await _collection.ReplaceOneAsync(filter, claim);
        
        if (result.MatchedCount == 0)
        {
             throw new Exception($"Claim with ID {claim.Id} not found for update.");
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

        var builder = Builders<Claim>.Filter;
        var filter = builder.And(
            builder.Eq(c => c.TenantId, tenantId),
            builder.Eq(c => c.BenefitPlanId, benefitPlanId),
            builder.Gte(c => c.ServiceDateFrom, yearStart),
            builder.Lte(c => c.ServiceDateFrom, yearEnd),
            builder.In(c => c.Status, finalizedStatuses),
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
