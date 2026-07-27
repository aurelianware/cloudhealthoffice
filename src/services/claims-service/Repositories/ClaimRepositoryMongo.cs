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
        CancellationToken ct = default,
        PendDetails? pendDetails = null,
        bool isPend = false,
        ClaimStatus? resolvedStatus = null,
        string? resolvedBenefitPlanId = null)
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
        // pipeline populated it. See Cosmos sibling for the
        // null-leaves-untouched contract.
        if (pendDetails is not null)
        {
            update = update.Set(c => c.PendDetails, pendDetails);
        }

        // See Cosmos sibling for why this bypass write is the only path
        // back to the row for the orchestrator's in-memory-resolved
        // BenefitPlanId. Only patch when the row doesn't already carry one.
        if (!string.IsNullOrWhiteSpace(resolvedBenefitPlanId) && string.IsNullOrWhiteSpace(head.BenefitPlanId))
        {
            update = update.Set(c => c.BenefitPlanId, resolvedBenefitPlanId);
        }

        var rowFilter = b.And(b.Eq(c => c.TenantId, tenantId), b.Eq(c => c.Id, head.Id));
        var result = await _collection.UpdateOneAsync(rowFilter, update, cancellationToken: ct);
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
            var pendUpdate = Builders<Claim>.Update.Set(c => c.Status, ClaimStatus.Pended);
            await _collection.UpdateOneAsync(pendFilter, pendUpdate, cancellationToken: ct);
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
                    head.Status)
                .ConfigureAwait(false);
        }

        return true;
    }

    public async Task<StatusWriteResult> UpdateAdjudicationSummaryAsync(
        string tenantId,
        string claimVersionId,
        AdjudicationResult adjudicationResult,
        ClaimStatus status,
        CancellationToken ct = default)
    {
        var b = Builders<Claim>.Filter;

        var chainFilter = b.Or(
            b.Eq(c => c.ClaimVersionId, claimVersionId),
            b.And(
                b.Or(b.Eq(c => c.ClaimVersionId, string.Empty), b.Eq(c => c.ClaimVersionId, (string?)null)),
                b.Eq(c => c.Id, claimVersionId)));

        var filter = b.And(
            b.Eq(c => c.TenantId, tenantId),
            chainFilter,
            b.Ne(c => c.VersionState, ClaimVersionState.Draft));
        var now = DateTime.UtcNow;

        // Residual-race fix — financial/audit data (AdjudicationResult, dates)
        // persists by chain/id even if async adjudication already finalized
        // the row. Only Status + VersionState are guarded, in a separate
        // conditional update — see TryPatchStatusAsync.
        var summaryUpdate = Builders<Claim>.Update
            .Set(c => c.AdjudicationResult, adjudicationResult)
            .Set(c => c.AdjudicatedDate, now)
            .Set(c => c.LastUpdatedDate, now);

        var options = new FindOneAndUpdateOptions<Claim>
        {
            Sort = Builders<Claim>.Sort.Descending(c => c.VersionNumber),
            Projection = Builders<Claim>.Projection
                .Include(c => c.Id)
                .Include(c => c.Status),
            ReturnDocument = ReturnDocument.Before,
        };

        var preWriteHead = await _collection.FindOneAndUpdateAsync(filter, summaryUpdate, options, ct);
        if (preWriteHead is null)
        {
            return StatusWriteResult.NotFoundResult;
        }

        return await TryPatchStatusAsync(
            tenantId,
            preWriteHead.Id,
            status,
            ClaimVersionState.Adjudicated,
            ct,
            adjudicationResult,
            preWriteHead.Status);
    }

    public Task<StatusWriteResult> TryTransitionStatusAsync(
        string tenantId,
        string claimId,
        ClaimStatus desiredStatus,
        CancellationToken ct = default) =>
        TryPatchStatusAsync(tenantId, claimId, desiredStatus, ClaimRepository.MapStatusToVersionState(desiredStatus), ct);

    /// <summary>
    /// Shared atomic status write behind <see cref="UpdateAdjudicationSummaryAsync"/>
    /// and <see cref="TryTransitionStatusAsync"/>. The precedence guard
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
        ClaimStatus? preWriteStatus = null)
    {
        var b = Builders<Claim>.Filter;
        var statusFilter = b.And(
            b.Eq(c => c.TenantId, tenantId),
            b.Eq(c => c.Id, rowId),
            b.Not(b.In(c => c.Status, ClaimRepository.SynchronousWritebackBlockedStatuses)));
        var statusUpdate = Builders<Claim>.Update
            .Set(c => c.Status, desiredStatus)
            .Set(c => c.VersionState, desiredVersionState);

        var result = await _collection.UpdateOneAsync(statusFilter, statusUpdate, cancellationToken: ct);
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
            var repairEvidenceFilter = desiredStatus == ClaimStatus.Denied
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
                    b.Eq(c => c.Status, ClaimStatus.Denied),
                    b.Gt(c => c.AdjudicationResult!.PayerPayment, 0m),
                    b.Or(
                        b.Eq(c => c.AdjudicationResult!.DenialReasonCode, null),
                        b.Eq(c => c.AdjudicationResult!.DenialReasonCode, string.Empty)));
            var repairFilter = b.And(
                b.Eq(c => c.TenantId, tenantId),
                b.Eq(c => c.Id, rowId),
                repairEvidenceFilter);

            var repairResult = await _collection.UpdateOneAsync(repairFilter, statusUpdate, cancellationToken: ct);
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
            builder.Ne(c => c.AdjudicationResult, null)
        );

        // Add scope-specific owner filter
        if (scope == "Family")
            filter = builder.And(filter, builder.Eq(c => c.SubscriberId, ownerId));
        else
            filter = builder.And(filter, builder.Eq(c => c.MemberId, ownerId));

        var rows = await _collection
            .Aggregate()
            .Match(filter)
            .Group(
                c => c.AdjudicationResult!.NetworkTier,
                g => new AccumulatorTotalsAggregationRow
                {
                    NetworkTier = g.Key,
                    DeductibleAmount = g.Sum(c => c.AdjudicationResult!.DeductibleAmount),
                    CoinsuranceAmount = g.Sum(c => c.AdjudicationResult!.CoinsuranceAmount),
                    CopayAmount = g.Sum(c => c.AdjudicationResult!.CopayAmount),
                    PatientResponsibility = g.Sum(c => c.AdjudicationResult!.PatientResponsibility)
                })
            .ToListAsync(ct);

        var deductibleType = scope == "Family" ? "FamilyDeductible"     : "IndividualDeductible";
        var oopType        = scope == "Family" ? "FamilyOutOfPocketMax"  : "IndividualOutOfPocketMax";

        var deductible  = new Dictionary<string, decimal>();
        var oop         = new Dictionary<string, decimal>();
        var coinsurance = new Dictionary<string, decimal>();
        var copay       = new Dictionary<string, decimal>();

        foreach (var row in rows)
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

    private sealed class AccumulatorTotalsAggregationRow
    {
        public string? NetworkTier { get; set; }
        public decimal DeductibleAmount { get; set; }
        public decimal CoinsuranceAmount { get; set; }
        public decimal CopayAmount { get; set; }
        public decimal PatientResponsibility { get; set; }
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

        var result = await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount > 0;
    }

    public async Task<bool> MarkVoidedProjectionAsync(
        string tenantId,
        string claimId,
        DateTime voidedAt,
        string? actorId,
        CancellationToken ct = default)
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

        var result = await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount > 0;
    }
}
