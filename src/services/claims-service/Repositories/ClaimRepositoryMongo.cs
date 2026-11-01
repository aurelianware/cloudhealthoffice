using CloudHealthOffice.Infrastructure.Data;
using ClaimsService.Exceptions;
using ClaimsService.Models;
using ClaimsService.Services.Adjudication;
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
    private readonly IAdjudicationTenantContext? _adjudicationTenantContext;

    public ClaimRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ClaimRepositoryMongo> logger,
        IAdjudicationTenantContext? adjudicationTenantContext = null)
    {
        _collection = database.GetCollection<Claim>("Claims");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _adjudicationTenantContext = adjudicationTenantContext;
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString()
            ?? _adjudicationTenantContext?.TenantId;
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new InvalidOperationException("TenantId not found in request or adjudication context");
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
        ClaimRepository.NormalizeAdjudicationProjection(claim);
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

    public async Task<IReadOnlyList<Claim>> FindByAttachmentControlNumberAsync(string controlNumber, int limit, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Claim>.Filter.And(
            Builders<Claim>.Filter.Eq(c => c.TenantId, tenantId),
            Builders<Claim>.Filter.ElemMatch(c => c.AttachmentReports, r => r.ControlNumber == controlNumber));

        // Every version of a claim keeps its PWK data; answer one per claim, its latest.
        var docs = await _collection.Find(filter)
            .SortByDescending(c => c.VersionNumber)
            .Limit(Math.Max(1, limit) * 10)
            .ToListAsync(ct);
        return docs
            .Select(Hydrate)
            .GroupBy(c => c.ClaimVersionId)
            .Select(g => g.OrderByDescending(c => c.VersionNumber).First())
            .OrderByDescending(c => c.SubmittedDate)
            .Take(Math.Max(1, limit))
            .ToList();
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
        var (claims, _) = await SearchWithCountAsync(
            memberId,
            providerNPI,
            serviceDateFrom,
            serviceDateTo,
            status,
            lineOfBusiness,
            page,
            pageSize);

        return claims;
    }

    public async Task<(IReadOnlyList<Claim> Page, int TotalCount)> SearchWithCountAsync(
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

        var safePage = Math.Max(page, 1);
        var safePageSize = Math.Clamp(pageSize, 1, 1000);
        var totalCount = (int)await _collection.CountDocumentsAsync(filter);
        var docs = await _collection.Find(filter)
            .SortByDescending(c => c.SubmittedDate)
            .Skip((safePage - 1) * safePageSize)
            .Limit(safePageSize)
            .ToListAsync();

        return (docs.Select(Hydrate).ToList(), totalCount);
    }

    public async Task<(IReadOnlyList<Claim> Page, int TotalCount)> SearchByCriteriaAsync(ClaimCriteria criteria, int page, int pageSize)
    {
        var filter = ClaimCriteriaFilter.Build(GetTenantId(), criteria);
        var safePage = Math.Max(page, 1);
        var safePageSize = Math.Clamp(pageSize, 1, 1000);
        var totalCount = (int)await _collection.CountDocumentsAsync(filter);
        var docs = await _collection.Find(filter)
            .SortByDescending(c => c.SubmittedDate)
            .Skip((safePage - 1) * safePageSize)
            .Limit(safePageSize)
            .ToListAsync();

        return (docs.Select(Hydrate).ToList(), totalCount);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetAuthorizationUsageAsync(IReadOnlyCollection<string> authorizationNumbers, CancellationToken ct = default)
    {
        var wanted = authorizationNumbers.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToHashSet(StringComparer.Ordinal);
        var usage = new Dictionary<string, int>(StringComparer.Ordinal);
        if (wanted.Count == 0) return usage;

        var b = Builders<Claim>.Filter;
        var filter = ClaimCriteriaFilter.Build(GetTenantId(), new ClaimCriteria())
                     & b.Or(b.In(c => c.PriorAuthorizationNumber, wanted),
                            b.ElemMatch(c => c.ClaimLines, Builders<ClaimLine>.Filter.In(l => l.PriorAuthorizationNumber, wanted)));
        var rows = await _collection.Find(filter)
            .Project(c => new { Header = c.PriorAuthorizationNumber, Lines = c.ClaimLines.Select(l => l.PriorAuthorizationNumber) })
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            // A claim counts once per number it carries, however many lines carry it.
            var numbers = row.Lines.Prepend(row.Header).Where(n => n is not null).Select(n => n!.Trim()).Where(wanted.Contains).Distinct();
            foreach (var n in numbers) usage[n] = usage.TryGetValue(n, out var count) ? count + 1 : 1;
        }
        return usage;
    }

    public async Task<ClaimInsights> GetInsightsAsync(DateTime from, DateTime to, LineOfBusiness? lineOfBusiness, DateTime asOf, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        FilterDefinition<Claim> Where(ClaimCriteria c) => ClaimCriteriaFilter.Build(tenantId, c);

        // ── Open inventory, by status ──
        var open = Where(new ClaimCriteria { Open = true, LineOfBusiness = lineOfBusiness });
        var byStatus = await _collection.Aggregate().Match(open)
            .Group(c => c.Status, g => new { Status = g.Key, Count = g.Count(), Charge = g.Sum(x => x.TotalChargeAmount) })
            .ToListAsync(ct);

        // ── Open inventory, by full days since receipt ──
        var aging = new List<ClaimAgingBucket>();
        foreach (var (min, max) in ClaimCriteriaFilter.AgingBuckets)
        {
            var after = max is { } m ? asOf.AddDays(-(m + 1)) : (DateTime?)null;
            var onOrBefore = min == 0 ? (DateTime?)null : asOf.AddDays(-min);
            var bucket = Where(new ClaimCriteria
            {
                Open = true, LineOfBusiness = lineOfBusiness, ReceivedAfter = after, ReceivedOnOrBefore = onOrBefore,
            });
            var totals = await _collection.Aggregate().Match(bucket)
                .Group(_ => 1, g => new { Count = g.Count(), Charge = g.Sum(x => x.TotalChargeAmount) })
                .FirstOrDefaultAsync(ct);
            aging.Add(new ClaimAgingBucket
            {
                Label = max is null ? $"{min}+ days" : $"{min}–{max} days",
                MinDays = min, MaxDays = max,
                Count = totals?.Count ?? 0, Charge = totals?.Charge ?? 0m,
                ReceivedAfter = after, ReceivedOnOrBefore = onOrBefore,
            });
        }

        // ── Pended now, by pend code ──
        var pended = Where(new ClaimCriteria { Status = ClaimStatus.Pended, LineOfBusiness = lineOfBusiness });
        var pendDocs = await _collection.Find(pended)
            .Project(c => new { Code = c.PendDetails == null ? null : c.PendDetails.PendCode, At = c.PendDetails == null ? (DateTime?)null : c.PendDetails.PendedAt })
            .ToListAsync(ct);
        var pendReasons = pendDocs
            .GroupBy(p => string.IsNullOrWhiteSpace(p.Code) ? ClaimCriteria.UnspecifiedPendCode : p.Code!)
            .Select(g => new ClaimPendReasonInsight { PendCode = g.Key, Count = g.Count(), OldestPendedAt = g.Min(p => p.At) })
            .OrderByDescending(r => r.Count).ThenBy(r => r.PendCode)
            .ToList();

        // ── Adjudicated in the period ──
        var adjudicated = new ClaimCriteria { Adjudicated = true, LineOfBusiness = lineOfBusiness, AdjudicatedFrom = from, AdjudicatedBefore = to };
        var outcomeRows = await _collection.Aggregate().Match(Where(adjudicated))
            .Group(c => c.Status, g => new
            {
                Status = g.Key, Count = g.Count(), Charge = g.Sum(x => x.TotalChargeAmount),
                Paid = g.Sum(x => x.AdjudicationResult == null ? 0m : x.AdjudicationResult.PayerPayment),
            })
            .ToListAsync(ct);
        var adjudicatedCount = outcomeRows.Sum(r => r.Count);
        var autoCount = (int)await _collection.CountDocumentsAsync(Where(new ClaimCriteria
        {
            Adjudicated = true, LineOfBusiness = lineOfBusiness, AdjudicatedFrom = from, AdjudicatedBefore = to, AutoAdjudicated = true,
        }), cancellationToken: ct);

        // ── Denied in the period, by claim-level reason ──
        var deniedDocs = await _collection.Find(Where(new ClaimCriteria
            {
                Adjudicated = true, LineOfBusiness = lineOfBusiness, AdjudicatedFrom = from, AdjudicatedBefore = to,
            }) & Builders<Claim>.Filter.In(c => c.Status, ClaimStatusSets.Denied))
            .Project(c => new
            {
                Code = c.AdjudicationResult == null ? null : c.AdjudicationResult.DenialReasonCode,
                Reason = c.AdjudicationResult == null ? null : c.AdjudicationResult.DenialReason,
            })
            .ToListAsync(ct);
        var denialReasons = deniedDocs
            .GroupBy(d => string.IsNullOrWhiteSpace(d.Code) ? ClaimCriteria.UnspecifiedDenialCode : d.Code!.Trim())
            .Select(g => new ClaimDenialReasonInsight
            {
                Code = g.Key, Count = g.Count(),
                Description = g.Select(d => d.Reason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)),
            })
            .OrderByDescending(r => r.Count).ThenBy(r => r.Code)
            .Take(10)
            .ToList();

        // ── Receipt to adjudication ──
        var spans = await _collection.Find(Where(adjudicated))
            .Project(c => new { c.SubmittedDate, c.AdjudicatedDate })
            .Limit(ClaimTurnaroundInsight.SampleLimit)
            .ToListAsync(ct);
        var days = spans.Where(s => s.AdjudicatedDate.HasValue && s.AdjudicatedDate.Value >= s.SubmittedDate)
            .Select(s => (decimal)(s.AdjudicatedDate!.Value - s.SubmittedDate).TotalDays)
            .OrderBy(d => d)
            .ToList();

        return new ClaimInsights
        {
            AsOf = asOf, From = from, To = to, LineOfBusiness = lineOfBusiness,
            Inventory = new ClaimInventoryInsight
            {
                Total = byStatus.Sum(r => r.Count),
                TotalCharge = byStatus.Sum(r => r.Charge),
                ByStatus = byStatus.OrderBy(r => r.Status).Select(r => new ClaimStatusCount { Status = r.Status, Count = r.Count, Charge = r.Charge }).ToList(),
                Aging = aging,
            },
            PendReasons = pendReasons,
            Outcomes = new ClaimOutcomeInsight
            {
                Adjudicated = adjudicatedCount,
                AutoAdjudicated = autoCount,
                AutoAdjudicationRate = adjudicatedCount == 0 ? null : Math.Round((decimal)autoCount / adjudicatedCount, 4),
                ByStatus = outcomeRows.OrderBy(r => r.Status).Select(r => new ClaimStatusCount { Status = r.Status, Count = r.Count, Charge = r.Charge }).ToList(),
                TotalCharge = outcomeRows.Sum(r => r.Charge),
                TotalPaid = outcomeRows.Sum(r => r.Paid),
            },
            DenialReasons = denialReasons,
            Turnaround = new ClaimTurnaroundInsight
            {
                Count = days.Count,
                AverageDays = days.Count == 0 ? null : Math.Round(days.Average(), 1),
                MedianDays = days.Count == 0 ? null : Math.Round(Percentile(days, 0.5m), 1),
                P90Days = days.Count == 0 ? null : Math.Round(Percentile(days, 0.9m), 1),
                Sampled = spans.Count >= ClaimTurnaroundInsight.SampleLimit,
            },
        };
    }

    // Nearest-rank percentile of an ascending list.
    private static decimal Percentile(IReadOnlyList<decimal> ascending, decimal p)
    {
        var rank = (int)Math.Ceiling(p * ascending.Count);
        return ascending[Math.Clamp(rank - 1, 0, ascending.Count - 1)];
    }

    public async Task<(IReadOnlyList<Claim> Page, int TotalCount)> SearchByIdsAsync(
        IReadOnlyCollection<string> claimIds,
        string? memberId,
        string? providerNPI,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        ClaimStatus? status,
        int page,
        int pageSize)
    {
        if (claimIds.Count == 0)
        {
            return (Array.Empty<Claim>(), 0);
        }

        var tenantId = GetTenantId();
        var builder = Builders<Claim>.Filter;
        var filter = builder.And(
            builder.Eq(c => c.TenantId, tenantId),
            builder.In(c => c.Id, claimIds.Distinct(StringComparer.OrdinalIgnoreCase)));

        if (!string.IsNullOrEmpty(memberId))
        {
            filter = builder.And(filter, builder.Eq(c => c.MemberId, memberId));
        }

        if (!string.IsNullOrEmpty(providerNPI))
        {
            filter = builder.And(filter, builder.Or(
                builder.Eq(c => c.BillingProviderNPI, providerNPI),
                builder.Eq(c => c.RenderingProviderNPI, providerNPI)));
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

        var totalCount = (int)await _collection.CountDocumentsAsync(filter);
        var docs = await _collection.Find(filter)
            .SortByDescending(c => c.SubmittedDate)
            .Skip((Math.Max(page, 1) - 1) * Math.Clamp(pageSize, 1, 1000))
            .Limit(Math.Clamp(pageSize, 1, 1000))
            .ToListAsync();

        return (docs.Select(Hydrate).ToList(), totalCount);
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
            DeniedClaims = claims.Count(c => ClaimStatusSemantics.IsDenied(c.Status)),
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

    /// <summary>Every write to a claim document bumps its <see cref="Claim.Revision"/>.</summary>
    private static UpdateDefinition<Claim> Bumped(UpdateDefinition<Claim> update) =>
        update.Inc(c => c.Revision, 1L);

    /// <summary>The document still has the revision the write was based on (0 matches documents written before the field existed).</summary>
    private static FilterDefinition<Claim> RevisionIs(long expected)
    {
        var b = Builders<Claim>.Filter;
        return expected == 0
            ? b.Or(b.Eq(c => c.Revision, 0L), b.Exists(c => c.Revision, false))
            : b.Eq(c => c.Revision, expected);
    }

    public async Task<Claim> UpdateAsync(Claim claim)
    {
        var tenantId = GetTenantId();
        // Ensure tenant isolation
        if (claim.TenantId != tenantId)
        {
            throw new InvalidOperationException("Cross-tenant updates are not allowed.");
        }

        // Reject mutations on terminal versions: adjustments must go through
        // the explicit "create new version with PredecessorVersionId" path
        // (capability 5.12).
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

        // Conditional on the revision the caller read: a concurrent write in
        // between (a status patch, an outbox clear, another replace) makes
        // this one fail instead of being silently overwritten.
        var expected = claim.Revision;
        claim.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(filter & RevisionIs(expected), claim);
        }
        catch
        {
            claim.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            claim.Revision = expected;
            if (existing != null || await _collection.Find(filter).AnyAsync())
                throw new ClaimConcurrencyException(claim.Id, expected);

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
        // skip offset in the token.
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
        CancellationToken ct = default,
        PendDetails? pendDetails = null,
        bool isPend = false,
        ClaimStatus? resolvedStatus = null,
        string? resolvedBenefitPlanId = null,
        string? requiredResolutionLockToken = null,
        AccumulatorOutboxItem? pendingAccumulatorCommit = null)
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

        // 5.7 — project deterministic pend reason when the adjudication
        // pipeline (NCCI / MUE / future authorization stages) populated
        // it. Null leaves the head row's existing PendDetails untouched
        // so a clean re-adjudication doesn't drop a prior pend reason
        // unless the new pipeline run replaces it.
        if (pendDetails is not null)
        {
            update = update.Set(c => c.PendDetails, pendDetails);
        }

        // The orchestrator resolves BenefitPlanId in-memory from the
        // member's active coverage for X12 837 claims that arrive without
        // one (see ClaimAdjudicationOrchestrator), but that in-memory claim
        // is never otherwise persisted — this bypass write is the only path
        // back to the row. Only patch when the row doesn't already carry one.
        if (!string.IsNullOrWhiteSpace(resolvedBenefitPlanId) && string.IsNullOrWhiteSpace(head.BenefitPlanId))
        {
            update = update.Set(c => c.BenefitPlanId, resolvedBenefitPlanId);
        }

        var rowFilter = b.And(b.Eq(c => c.TenantId, tenantId), b.Eq(c => c.Id, head.Id));
        if (requiredResolutionLockToken is not null)
        {
            // Follow-up 1: an examiner-approval re-run writes only while its
            // resolution lock is still held — evaluated by the update itself,
            // so a re-run that outlived its lock cannot overwrite the claim
            // another examiner took over (or finalized).
            rowFilter = b.And(rowFilter, b.Eq(c => c.ResolutionLock!.Token, requiredResolutionLockToken));
        }
        var result = await _collection.UpdateOneAsync(rowFilter, Bumped(update), cancellationToken: ct);
        if (result.MatchedCount == 0)
        {
            return false;
        }

        if (isPend)
        {
            // Defect A fix, made atomic — project the orchestrator's Pend outcome
            // onto ClaimStatus in a SEPARATE conditional update, whose FILTER
            // (not a C# if-check against the `head` snapshot above) carries the
            // precedence rule: never downgrade a claim already at a later-stage
            // disposition (see ClaimRepository.IsFinalDisposition). A
            // read-then-decide check only catches a concurrent write that lands
            // before this method's own read; MongoDB evaluates this filter
            // against the row's live state at the moment the update actually
            // executes, so a competing write landing in between is still caught.
            // No match on this second update just means the guard correctly
            // blocked the downgrade — not an error; the AdjudicationResult /
            // ClaimLines / PendDetails write above already succeeded, so this
            // method still returns true either way. Re-pending an already-Pended
            // claim is allowed — Pended is excluded from FinalDispositions.
            var pendFilter = b.And(
                b.Eq(c => c.TenantId, tenantId),
                b.Eq(c => c.Id, head.Id),
                b.Not(b.In(c => c.Status, ClaimRepository.FinalDispositions)));
            if (requiredResolutionLockToken is not null)
                pendFilter = b.And(pendFilter, b.Eq(c => c.ResolutionLock!.Token, requiredResolutionLockToken));
            var pendUpdate = Builders<Claim>.Update.Set(c => c.Status, ClaimStatus.Pended);
            await _collection.UpdateOneAsync(pendFilter, Bumped(pendUpdate), cancellationToken: ct);
            return true;
        }

        if (resolvedStatus is not null)
        {
            await TryPatchStatusAsync(
                    tenantId,
                    head.Id,
                    resolvedStatus.Value,
                    ClaimRepository.MapStatusToVersionState(resolvedStatus.Value),
                    ct,
                    adjudicationResult,
                    head.Status,
                    requiredResolutionLockToken,
                    resolvedStatus == ClaimStatus.Approved ? pendingAccumulatorCommit : null)
                .ConfigureAwait(false);
        }

        return true;
    }

    public Task<StatusWriteResult> TryTransitionStatusAsync(
        string tenantId,
        string claimId,
        ClaimStatus desiredStatus,
        CancellationToken ct = default) =>
        TryPatchStatusAsync(tenantId, claimId, desiredStatus, ClaimRepository.MapStatusToVersionState(desiredStatus), ct);

    /// <summary>
    /// Shared atomic status write behind <see cref="TryTransitionStatusAsync"/>
    /// and the pipeline's resolved-status projection
    /// (<see cref="UpdateAdjudicationProjectionAsync"/>). The precedence guard
    /// (<see cref="ClaimRepository.BlocksSynchronousWriteback"/>) is encoded
    /// directly in the update filter's <c>$nin</c> clause, so MongoDB
    /// evaluates it against the document's live state at the moment the
    /// update actually executes — not a read-then-decide snapshot — meaning
    /// it holds under a true concurrent write. A <c>MatchedCount == 0</c>
    /// result is ambiguous (guard blocked it vs. row doesn't exist), so on
    /// that path we issue one fallback existence+status read to disambiguate
    /// and to report what's actually persisted; this only runs on the
    /// (expected to be rare) suppressed/not-found path, not the hot path.
    /// </summary>
    private async Task<StatusWriteResult> TryPatchStatusAsync(
        string tenantId,
        string rowId,
        ClaimStatus desiredStatus,
        ClaimVersionState desiredVersionState,
        CancellationToken ct,
        AdjudicationResult? incomingAdjudication = null,
        ClaimStatus? preWriteStatus = null,
        string? requiredResolutionLockToken = null,
        AccumulatorOutboxItem? pendingAccumulatorCommit = null)
    {
        var b = Builders<Claim>.Filter;
        // Follow-up 1 (PR #1279 review): an approval re-run's status write is
        // fenced on the same resolution lock as its projection write.
        var lockFence = requiredResolutionLockToken is null
            ? b.Empty
            : b.Eq(c => c.ResolutionLock!.Token, requiredResolutionLockToken);
        var statusFilter = b.And(
            b.Eq(c => c.TenantId, tenantId),
            b.Eq(c => c.Id, rowId),
            b.Not(b.In(c => c.Status, ClaimRepository.SynchronousWritebackBlockedStatuses)),
            lockFence);
        var statusUpdate = Builders<Claim>.Update
            .Set(c => c.Status, desiredStatus)
            .Set(c => c.VersionState, desiredVersionState);
        // The accumulator outbox entry lands with the status that finalizes
        // the claim, or not at all.
        if (pendingAccumulatorCommit is not null)
            statusUpdate = statusUpdate
                .Set(c => c.PendingAccumulatorCommit, pendingAccumulatorCommit)
                .Set(c => c.LatestAccumulatorCommitId, pendingAccumulatorCommit.Id);

        var result = await _collection.UpdateOneAsync(statusFilter, Bumped(statusUpdate), cancellationToken: ct);
        if (result.MatchedCount > 0)
        {
            return new StatusWriteResult(StatusWriteOutcome.Applied, desiredStatus);
        }

        var snapshotAllowsRepair = incomingAdjudication is not null
            && preWriteStatus is not null
            && (ClaimRepository.CanRepairContradictoryDeniedSummary(
                    preWriteStatus.Value,
                    desiredStatus,
                    incomingAdjudication)
                || ClaimRepository.CanRepairContradictoryApprovedSummary(
                    preWriteStatus.Value,
                    desiredStatus,
                    incomingAdjudication));
        var liveEvidenceAllowsRepair = incomingAdjudication is not null
            && ClaimRepository.CanAttemptContradictoryStatusRepair(desiredStatus, incomingAdjudication);

        if (snapshotAllowsRepair || liveEvidenceAllowsRepair)
        {
            var repairEvidenceFilter = desiredStatus == ClaimStatus.Deny
                ? b.And(
                    b.Eq(c => c.Status, ClaimStatus.Approved),
                    b.Eq(c => c.AdjudicationResult!.PayerPayment, 0m),
                    b.Or(
                        b.And(
                            b.Ne(c => c.AdjudicationResult!.DenialReasonCode, null),
                            b.Ne(c => c.AdjudicationResult!.DenialReasonCode, string.Empty)),
                        b.And(
                            b.Ne(c => c.AdjudicationResult!.DenialReason, null),
                            b.Ne(c => c.AdjudicationResult!.DenialReason, string.Empty)),
                        b.SizeGt(c => c.AdjudicationResult!.AdjustmentReasons, 0)))
                : b.And(
                    b.Eq(c => c.Status, ClaimStatus.Deny),
                    b.Gt(c => c.AdjudicationResult!.PayerPayment, 0m),
                    b.Or(
                        b.Eq(c => c.AdjudicationResult!.DenialReasonCode, null),
                        b.Eq(c => c.AdjudicationResult!.DenialReasonCode, string.Empty)));
            var repairFilter = b.And(
                b.Eq(c => c.TenantId, tenantId),
                b.Eq(c => c.Id, rowId),
                repairEvidenceFilter,
                lockFence);

            var repairResult = await _collection.UpdateOneAsync(repairFilter, Bumped(statusUpdate), cancellationToken: ct);
            if (repairResult.MatchedCount > 0)
            {
                return new StatusWriteResult(StatusWriteOutcome.Applied, desiredStatus);
            }
        }

        var existing = await _collection
            .Find(b.And(b.Eq(c => c.TenantId, tenantId), b.Eq(c => c.Id, rowId)))
            .FirstOrDefaultAsync(ct);

        return existing is null
            ? StatusWriteResult.NotFoundResult
            : new StatusWriteResult(StatusWriteOutcome.Suppressed, existing.Status);
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
            // A Deny claim is VersionState Adjudicated (non-final) but writes
            // no accumulators: denials (either phase) and voids never count.
            builder.Nin(c => c.Status, new[] { ClaimStatus.Deny, ClaimStatus.Denied, ClaimStatus.Voided }),
            builder.Ne(c => c.AdjudicationResult, null)
        );

        // Add scope-specific owner filter
        if (scope == "Family")
            filter = builder.And(filter, builder.Eq(c => c.SubscriberId, ownerId));
        else
            filter = builder.And(filter, builder.Eq(c => c.MemberId, ownerId));

        // A version superseded by an adjustment (VersionState Adjusted, or
        // SupersededAt set while Status still says Paid) no longer counts: its
        // replacement does, once it is finalized, and the replacement's commit
        // takes nothing of the predecessor back from a rebuilt cache.
        filter = builder.And(filter,
            builder.Ne(c => c.VersionState, ClaimVersionState.Adjusted),
            builder.Eq(c => c.SupersededAt, null));

        var rows = await _collection.Find(filter)
            .Project(c => new
            {
                c.Id,
                c.AdjudicationResult!.NetworkTier,
                c.AdjudicationResult.DeductibleAmount,
                c.AdjudicationResult.CoinsuranceAmount,
                c.AdjudicationResult.CopayAmount,
                c.AdjudicationResult.PatientResponsibility,
                c.AdjudicationResult.OopAppliedAmount,
            })
            .ToListAsync(ct);

        // OOP-eligible amount; legacy rows without it count full patient responsibility.
        return AccumulatorTotalsRow.Build(rows.Select(r => new AccumulatorTotalsRow(
            r.Id, r.NetworkTier, r.DeductibleAmount, r.CoinsuranceAmount, r.CopayAmount,
            r.OopAppliedAmount ?? r.PatientResponsibility)), scope);
    }

    // ── accumulator outbox ──────────────────────────────────────────────

    private static FilterDefinition<Claim> OutboxSlotIs(AccumulatorOutboxKind kind, string itemId) =>
        kind == AccumulatorOutboxKind.Commit
            ? Builders<Claim>.Filter.Eq(c => c.PendingAccumulatorCommit!.Id, itemId)
            : Builders<Claim>.Filter.Eq(c => c.PendingAccumulatorReversal!.Id, itemId);

    [CrossTenantQuery("AccumulatorOutboxDispatcher (background): finds due entries across tenants; each is processed in its own scope with claim.TenantId.")]
    public async Task<IReadOnlyList<Claim>> FindDueAccumulatorOutboxAsync(long nowMs, int limit, CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        var filter = b.Or(
            b.Lte(c => c.PendingAccumulatorCommit!.DueAtMs, nowMs),
            b.Lte(c => c.PendingAccumulatorReversal!.DueAtMs, nowMs));
        return await _collection.Find(filter).Limit(limit).ToListAsync(ct);
    }

    [CrossTenantQuery("AccumulatorOutboxDispatcher metric: age of the oldest pending entry across tenants; returns no claim data.")]
    public async Task<long?> OldestAccumulatorOutboxAsync(CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        async Task<long?> Oldest(FilterDefinition<Claim> present, SortDefinition<Claim> sort, Func<Claim, long?> pick)
        {
            var claim = await _collection.Find(present).Sort(sort).Limit(1).FirstOrDefaultAsync(ct);
            return claim is null ? null : pick(claim);
        }
        var commit = await Oldest(b.Ne(c => c.PendingAccumulatorCommit, null),
            Builders<Claim>.Sort.Ascending(c => c.PendingAccumulatorCommit!.CreatedAtMs), c => c.PendingAccumulatorCommit?.CreatedAtMs);
        var reversal = await Oldest(b.Ne(c => c.PendingAccumulatorReversal, null),
            Builders<Claim>.Sort.Ascending(c => c.PendingAccumulatorReversal!.CreatedAtMs), c => c.PendingAccumulatorReversal?.CreatedAtMs);
        return (commit, reversal) switch
        {
            (null, null) => null,
            ({ } a, null) => a,
            (null, { } r) => r,
            ({ } a, { } r) => Math.Min(a, r),
        };
    }

    public async Task<Claim?> GetForAccumulatorOutboxAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        var claim = await _collection.Find(b.And(b.Eq(c => c.TenantId, tenantId), b.Eq(c => c.Id, claimId))).FirstOrDefaultAsync(ct);
        return claim is null ? null : Hydrate(claim);
    }

    public async Task<bool> CompleteAccumulatorOutboxAsync(
        string tenantId, string claimId, AccumulatorOutboxKind kind, string itemId,
        AccumulatorClampReview? clampReview = null, CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        var filter = b.And(b.Eq(c => c.TenantId, tenantId), b.Eq(c => c.Id, claimId), OutboxSlotIs(kind, itemId));
        var update = kind == AccumulatorOutboxKind.Commit
            ? Builders<Claim>.Update.Set(c => c.PendingAccumulatorCommit, null)
            : Builders<Claim>.Update.Set(c => c.PendingAccumulatorReversal, null);
        if (clampReview is not null) update = update.Set(c => c.AccumulatorClampReview, clampReview);
        var result = await _collection.UpdateOneAsync(filter, Bumped(update), cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public async Task<bool> RescheduleAccumulatorOutboxAsync(
        string tenantId, string claimId, AccumulatorOutboxKind kind, string itemId,
        int attempts, DateTime nextAttemptAt, string? lastError, CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        var filter = b.And(b.Eq(c => c.TenantId, tenantId), b.Eq(c => c.Id, claimId), OutboxSlotIs(kind, itemId));
        var due = AccumulatorOutboxItem.ToMs(nextAttemptAt);
        var update = kind == AccumulatorOutboxKind.Commit
            ? Builders<Claim>.Update
                .Set(c => c.PendingAccumulatorCommit!.Attempts, attempts)
                .Set(c => c.PendingAccumulatorCommit!.NextAttemptAt, nextAttemptAt)
                .Set(c => c.PendingAccumulatorCommit!.DueAtMs, due)
                .Set(c => c.PendingAccumulatorCommit!.LastError, lastError)
            : Builders<Claim>.Update
                .Set(c => c.PendingAccumulatorReversal!.Attempts, attempts)
                .Set(c => c.PendingAccumulatorReversal!.NextAttemptAt, nextAttemptAt)
                .Set(c => c.PendingAccumulatorReversal!.DueAtMs, due)
                .Set(c => c.PendingAccumulatorReversal!.LastError, lastError);
        var result = await _collection.UpdateOneAsync(filter, Bumped(update), cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    // ── claim-event outbox ──────────────────────────────────────────────

    private static FilterDefinition<Claim> HasClaimEvent(string tenantId, string claimId, string eventId)
    {
        var b = Builders<Claim>.Filter;
        return b.And(
            b.Eq(c => c.TenantId, tenantId),
            b.Eq(c => c.Id, claimId),
            b.ElemMatch(c => c.PendingClaimEvents, e => e.Id == eventId));
    }

    [CrossTenantQuery("ClaimEventOutboxDispatcher (background): finds due events across tenants; completion and rescheduling filter on the claim's TenantId.")]
    public async Task<IReadOnlyList<Claim>> FindDueClaimEventsAsync(long nowMs, int limit, CancellationToken ct = default)
    {
        var filter = Builders<Claim>.Filter.ElemMatch(c => c.PendingClaimEvents, e => e.DueAtMs <= nowMs);
        var claims = await _collection.Find(filter).Limit(limit).ToListAsync(ct);
        return claims.Select(Hydrate).ToList();
    }

    [CrossTenantQuery("ClaimEventOutboxDispatcher metric: age of the oldest pending event across tenants; returns no claim data.")]
    public async Task<long?> OldestClaimEventAsync(CancellationToken ct = default)
    {
        // $exists on the indexed path, so the sparse claim_event_outbox_created
        // index answers the filter (only claims with a pending event are read);
        // a $size / "PendingClaimEvents.0" filter scanned the whole collection.
        var filter = Builders<Claim>.Filter.Exists("PendingClaimEvents.CreatedAtMs");
        var claim = await _collection.Find(filter)
            .Sort(Builders<Claim>.Sort.Ascending("PendingClaimEvents.CreatedAtMs"))
            .Limit(1).FirstOrDefaultAsync(ct);
        return claim?.PendingClaimEvents is { Count: > 0 } pending ? pending.Min(e => e.CreatedAtMs) : null;
    }

    public async Task<bool> CompleteClaimEventAsync(string tenantId, string claimId, string eventId, CancellationToken ct = default)
    {
        var update = Builders<Claim>.Update.PullFilter(c => c.PendingClaimEvents, e => e.Id == eventId);
        var result = await _collection.UpdateOneAsync(HasClaimEvent(tenantId, claimId, eventId), Bumped(update), cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task<bool> RescheduleClaimEventAsync(
        string tenantId, string claimId, string eventId, int attempts, long dueAtMs, string? lastError, CancellationToken ct = default)
    {
        var update = Builders<Claim>.Update
            .Set("PendingClaimEvents.$.Attempts", attempts)
            .Set("PendingClaimEvents.$.DueAtMs", dueAtMs)
            .Set("PendingClaimEvents.$.LastError", lastError);
        var result = await _collection.UpdateOneAsync(HasClaimEvent(tenantId, claimId, eventId), Bumped(update), cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public async Task<IReadOnlyList<Claim>> GetAccumulatorClampReviewsAsync(int limit, CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        var filter = b.And(
            b.Eq(c => c.TenantId, GetTenantId()),
            b.Ne(c => c.AccumulatorClampReview, null),
            b.Eq(c => c.AccumulatorClampReview!.Resolved, false));
        return (await _collection.Find(filter).Limit(limit).ToListAsync(ct)).Select(Hydrate).ToList();
    }

    public async Task<IReadOnlyList<Claim>> FindPendedByPendCodeAsync(string pendCode, int limit, CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        var filter = b.And(
            b.Eq(c => c.TenantId, GetTenantId()),
            b.Eq(c => c.Status, ClaimStatus.Pended),
            b.Eq(c => c.PendDetails!.PendCode, pendCode),
            // Claims with other pend reasons are left for an examiner by the
            // bulk re-runs; returning them would let them fill every batch.
            b.Or(
                b.Exists(c => c.PendDetails!.AdditionalPendReasons, false),
                b.Eq(c => c.PendDetails!.AdditionalPendReasons, null),
                b.Size(c => c.PendDetails!.AdditionalPendReasons, 0)));
        return (await _collection.Find(filter).SortBy(c => c.PendDetails!.PendedAt).Limit(limit).ToListAsync(ct)).Select(Hydrate).ToList();
    }

    /// <summary>The store-side part of <see cref="ClaimRepository.IsLegacyUnremittedDenied"/>.</summary>
    private static FilterDefinition<Claim> LegacyUnremittedDeniedFilter(FilterDefinitionBuilder<Claim> b) => b.And(
        b.Eq(c => c.Status, ClaimStatus.Denied),
        b.Or(b.In(c => c.VersionState, new[] { ClaimVersionState.Denied, ClaimVersionState.Unknown }), b.Exists(c => c.VersionState, false)),
        b.Or(b.Eq(c => c.AdjudicationResult!.CheckNumber, null), b.Eq(c => c.AdjudicationResult!.CheckNumber, string.Empty)),
        b.Eq(c => c.AdjudicationResult!.PaymentDate, null),
        b.Or(b.Eq(c => c.EDI835ControlNumber, null), b.Eq(c => c.EDI835ControlNumber, string.Empty)),
        b.Eq(c => c.SupersededAt, null));

    public async Task<IReadOnlyList<Claim>> FindLegacyUnremittedDeniedAsync(int limit, CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        var filter = b.And(b.Eq(c => c.TenantId, GetTenantId()), LegacyUnremittedDeniedFilter(b));
        return (await _collection.Find(filter).Limit(limit).ToListAsync(ct))
            .Select(Hydrate)
            .Where(ClaimRepository.IsLegacyUnremittedDenied)
            .ToList();
    }

    public async Task<bool> TryReopenLegacyDeniedAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        var filter = b.And(b.Eq(c => c.TenantId, tenantId), b.Eq(c => c.Id, claimId), LegacyUnremittedDeniedFilter(b));
        var update = Builders<Claim>.Update
            .Set(c => c.Status, ClaimStatus.Deny)
            .Set(c => c.VersionState, ClaimVersionState.Adjudicated)
            .Set(c => c.LastUpdatedDate, DateTime.UtcNow);
        var result = await _collection.UpdateOneAsync(filter, Bumped(update), cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task<IReadOnlyList<Claim>> FindFinalizedMedicaidAsync(
        DateTime from, DateTime to, string? memberId, bool payAndChaseOnly, int limit, CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        var filter = b.And(
            b.Eq(c => c.TenantId, GetTenantId()),
            b.Eq(c => c.LineOfBusiness, LineOfBusiness.Medicaid),
            b.In(c => c.Status, new[] { ClaimStatus.Approved, ClaimStatus.PartiallyPaid, ClaimStatus.Paid }),
            b.Gte(c => c.AdjudicatedDate, from),
            b.Lt(c => c.AdjudicatedDate, to),
            b.Eq(c => c.SupersededAt, null));
        if (!string.IsNullOrWhiteSpace(memberId))
            filter &= b.Eq(c => c.MemberId, memberId);
        if (payAndChaseOnly)
            filter &= b.Eq(c => c.AdjudicationResult!.ThirdPartyLiability!.Decision, "PayAndChase");
        return (await _collection.Find(filter).SortBy(c => c.AdjudicatedDate).Limit(limit).ToListAsync(ct)).Select(Hydrate).ToList();
    }

    public async Task<IReadOnlyList<Claim>> FindFinalizedForAccumulatorSweepAsync(
        DateTime from, DateTime to, int limit, CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        var filter = b.And(
            b.Eq(c => c.TenantId, GetTenantId()),
            b.In(c => c.Status, new[] { ClaimStatus.Approved, ClaimStatus.PartiallyPaid, ClaimStatus.Paid }),
            b.Gte(c => c.LastUpdatedDate, from),
            b.Lte(c => c.LastUpdatedDate, to),
            b.Ne(c => c.VersionState, ClaimVersionState.Adjusted),
            b.Eq(c => c.SupersededAt, null),
            b.Eq(c => c.PendingAccumulatorCommit, null));
        return (await _collection.Find(filter).Limit(limit).ToListAsync(ct)).Select(Hydrate).ToList();
    }

    public async Task<bool> RecordAccumulatorReviewAsync(
        string tenantId, string claimId, AccumulatorClampReview review, CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        var row = b.And(b.Eq(c => c.TenantId, tenantId), b.Eq(c => c.Id, claimId));
        var result = await _collection.UpdateOneAsync(
            b.And(row, b.Or(b.Eq(c => c.AccumulatorClampReview, null), b.Ne(c => c.AccumulatorClampReview!.CommitId, review.CommitId))),
            Bumped(Builders<Claim>.Update.Set(c => c.AccumulatorClampReview, review)),
            cancellationToken: ct);
        if (result.MatchedCount == 1) return true;
        return await _collection.Find(row).AnyAsync(ct); // already holds this commit's review
    }

    public async Task<bool> ResolveAccumulatorClampReviewAsync(string claimId, string resolvedBy, CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        var result = await _collection.UpdateOneAsync(
            b.And(b.Eq(c => c.TenantId, GetTenantId()), b.Eq(c => c.Id, claimId), b.Ne(c => c.AccumulatorClampReview, null)),
            Bumped(Builders<Claim>.Update
                .Set(c => c.AccumulatorClampReview!.Resolved, true)
                .Set(c => c.AccumulatorClampReview!.ResolvedBy, resolvedBy)
                .Set(c => c.AccumulatorClampReview!.ResolvedAt, DateTime.UtcNow)),
            cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public async Task<bool> MarkSupersededProjectionAsync(
        string tenantId,
        string claimId,
        string supersessorVersionId,
        DateTime supersededAt,
        string? actorId,
        CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        var filter = b.And(
            b.Eq(c => c.TenantId, tenantId),
            b.Eq(c => c.Id, claimId));

        var update = Builders<Claim>.Update
            .Set(c => c.SupersededAt, supersededAt)
            .Set(c => c.SupersededByVersionId, supersessorVersionId)
            .Set(c => c.VersionState, ClaimVersionState.Adjusted)
            .Set(c => c.LastUpdatedDate, DateTime.UtcNow)
            .Set(c => c.LastUpdatedBy, actorId);

        var result = await _collection.UpdateOneAsync(filter, Bumped(update), cancellationToken: ct);
        return result.MatchedCount > 0;
    }

    public async Task<bool> TryAcquireResolutionLockAsync(
        string tenantId, string claimId, string token, string? actorId, DateTime now, TimeSpan duration,
        CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        var filter = b.And(
            b.Eq(c => c.TenantId, tenantId),
            b.Eq(c => c.Id, claimId),
            b.In(c => c.Status, ClaimStatusSemantics.ReprocessableStatuses),
            b.Or(
                b.Eq(c => c.ResolutionLock, null),
                b.Lt(c => c.ResolutionLock!.ExpiresAt, now)));
        var update = Builders<Claim>.Update.Set(c => c.ResolutionLock, new ExaminerResolutionLock
        {
            Token = token, LockedBy = actorId, ExpiresAt = now + duration,
        });
        var result = await _collection.UpdateOneAsync(filter, Bumped(update), cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task<Claim?> UpdateHoldingResolutionLockAsync(Claim claim, string lockToken, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        if (claim.TenantId != tenantId)
            throw new InvalidOperationException("Cross-tenant updates are not allowed.");
        var b = Builders<Claim>.Filter;
        var filter = b.And(
            b.Eq(c => c.Id, claim.Id),
            b.Eq(c => c.TenantId, tenantId),
            b.Eq(c => c.ResolutionLock!.Token, lockToken));
        // The lock fences other resolvers; the revision fences every other
        // writer (a status patch, an outbox clear) since this claim was read.
        var expected = claim.Revision;
        claim.Revision = expected + 1;
        var result = await _collection.ReplaceOneAsync(filter & RevisionIs(expected), claim, cancellationToken: ct);
        if (result.MatchedCount == 1) return claim;
        claim.Revision = expected;
        // Lock still held, revision moved on: a concurrent write, not a lost lock.
        if (await _collection.Find(filter).AnyAsync(ct))
            throw new ClaimConcurrencyException(claim.Id, expected);
        return null;
    }

    public async Task<bool> HoldsResolutionLockAsync(
        string tenantId, string claimId, string token, DateTime now, CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        var filter = b.And(
            b.Eq(c => c.TenantId, tenantId),
            b.Eq(c => c.Id, claimId),
            b.In(c => c.Status, ClaimStatusSemantics.ReprocessableStatuses),
            b.Eq(c => c.ResolutionLock!.Token, token),
            b.Gt(c => c.ResolutionLock!.ExpiresAt, now));
        return await _collection.CountDocumentsAsync(filter, new CountOptions { Limit = 1 }, ct) > 0;
    }

    public async Task ReleaseResolutionLockAsync(string tenantId, string claimId, string token, CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;
        await _collection.UpdateOneAsync(
            b.And(b.Eq(c => c.TenantId, tenantId), b.Eq(c => c.Id, claimId), b.Eq(c => c.ResolutionLock!.Token, token)),
            Bumped(Builders<Claim>.Update.Set(c => c.ResolutionLock, null)),
            cancellationToken: ct);
    }

    public async Task<bool> MarkVoidedProjectionAsync(
        string tenantId,
        string claimId,
        DateTime voidedAt,
        string? actorId,
        CancellationToken ct = default,
        AccumulatorOutboxItem? pendingAccumulatorReversal = null,
        ClaimEventOutboxItem? claimEvent = null)
    {
        var b = Builders<Claim>.Filter;
        var filter = b.And(
            b.Eq(c => c.TenantId, tenantId),
            b.Eq(c => c.Id, claimId));

        var update = Builders<Claim>.Update
            .Set(c => c.Status, ClaimStatus.Voided)
            .Set(c => c.VersionState, ClaimVersionState.Voided)
            .Set(c => c.LastUpdatedDate, voidedAt)
            .Set(c => c.LastUpdatedBy, actorId);
        // The void's accumulator reversal lands with the void, or not at all.
        if (pendingAccumulatorReversal is not null)
            update = update.Set(c => c.PendingAccumulatorReversal, pendingAccumulatorReversal);
        // So does the claim event that reports it.
        if (claimEvent is not null)
            update = update.Push(c => c.PendingClaimEvents, claimEvent);

        var result = await _collection.UpdateOneAsync(filter, Bumped(update), cancellationToken: ct);
        return result.MatchedCount > 0;
    }

    public async Task<IReadOnlyList<Claim>> FindDuplicateCandidatesAsync(
        string tenantId,
        string memberId,
        DateTime serviceDateFrom,
        DateTime serviceDateTo,
        string excludeClaimVersionId,
        CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;

        // Leading equality + range predicates ride the
        // (TenantId, MemberId, ServiceDateFrom) index created by
        // ClaimIndexInitializer; the remaining predicates are residual
        // filters over that bounded member/date slice. Ne/Nin also match
        // documents missing the field, so legacy rows stay visible.
        var filter = b.And(
            b.Eq(c => c.TenantId, tenantId),
            b.Eq(c => c.MemberId, memberId),
            b.Lte(c => c.ServiceDateFrom, serviceDateTo),
            b.Gte(c => c.ServiceDateTo, serviceDateFrom),
            b.Ne(c => c.Id, excludeClaimVersionId),
            b.Ne(c => c.ClaimVersionId, excludeClaimVersionId),
            b.Nin(c => c.Status, new[] { ClaimStatus.Deny, ClaimStatus.Denied, ClaimStatus.Voided }),
            b.Nin(c => c.VersionState, new[]
            {
                ClaimVersionState.Draft,
                ClaimVersionState.Denied,
                ClaimVersionState.Voided,
                ClaimVersionState.Adjusted,
            }),
            b.Eq(c => c.SupersededAt, (DateTime?)null),
            b.Ne(c => c.ClaimFrequencyCode, "8"));

        var items = await _collection.Find(filter)
            .Limit(ClaimRepository.MaxDuplicateCandidates)
            .ToListAsync(ct);

        return items.Select(Hydrate).ToList();
    }

    public async Task<IReadOnlyList<(DateTime Received, DateTime? Adjudicated)>> GetReceiptToAdjudicationAsync(ClaimCriteria criteria, int limit, CancellationToken ct = default)
    {
        var rows = await _collection.Find(ClaimCriteriaFilter.Build(GetTenantId(), criteria))
            .SortBy(c => c.SubmittedDate)
            .Limit(Math.Max(limit, 1))
            .Project(c => new { c.SubmittedDate, c.AdjudicatedDate })
            .ToListAsync(ct);
        return rows.Select(r => (r.SubmittedDate, r.AdjudicatedDate)).ToList();
    }

    public async Task<(IReadOnlyList<Claim> Rows, int TotalCount)> GetReportRowsAsync(ClaimCriteria criteria, int limit, CancellationToken ct = default)
    {
        var filter = ClaimCriteriaFilter.Build(GetTenantId(), criteria);
        var total = (int)await _collection.CountDocumentsAsync(filter, cancellationToken: ct);
        var docs = await _collection.Find(filter)
            .SortBy(c => c.SubmittedDate).ThenBy(c => c.Id)
            .Limit(Math.Max(limit, 1))
            .ToListAsync(ct);
        return (docs.Select(Hydrate).ToList(), total);
    }

    public async Task<IReadOnlyList<Finance.ClaimPaymentVersion>> GetPaymentVersionsAsync(
        DateTime incurredFrom, DateTime incurredBefore, LineOfBusiness? lineOfBusiness, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var b = Builders<Claim>.Filter;
        var filter = b.Eq(c => c.TenantId, tenantId)
                     & b.Gte(c => c.ServiceDateFrom, incurredFrom)
                     & b.Lt(c => c.ServiceDateFrom, incurredBefore);
        if (lineOfBusiness.HasValue)
            filter &= b.Eq(c => c.LineOfBusiness, lineOfBusiness.Value);

        // Projected and streamed: a year of a large plan's claims is millions
        // of versions, of which only five fields are needed.
        var rows = await _collection.Find(filter)
            .Project(c => new
            {
                c.ClaimNumber,
                c.VersionNumber,
                c.ServiceDateFrom,
                c.PaidDate,
                Paid = c.AdjudicationResult == null ? 0m : c.AdjudicationResult.PayerPayment,
                c.Status,
                c.VersionState
            })
            .ToListAsync(ct);
        return rows.Select(r => new Finance.ClaimPaymentVersion(
            r.ClaimNumber, r.VersionNumber, r.ServiceDateFrom, r.PaidDate, r.Paid,
            r.Status == ClaimStatus.Voided || r.VersionState == ClaimVersionState.Voided)).ToList();
    }
}
