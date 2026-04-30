using Microsoft.Azure.Cosmos;
using ClaimsService.Exceptions;
using ClaimsService.Models;

namespace ClaimsService.Repositories;

public interface IClaimRepository
{
    Task<Claim?> GetByIdAsync(string id);
    Task<Claim?> GetByClaimNumberAsync(string claimNumber);
    Task<IEnumerable<Claim>> SearchAsync(
        string? memberId,
        string? providerNPI,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        ClaimStatus? status,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize);

    /// <summary>
    /// Member-scoped search with the filters the portal Member Details dialog
    /// exposes: date range, status, provider, claim type, amount range. Always
    /// requires a memberId — this method exists so the v1 member endpoint has a
    /// single repository path and so amountRange/claimType filters don't force
    /// a signature change on the wider SearchAsync.
    /// Returns (matching page, totalCount) where totalCount reflects the full
    /// result set across all pages so the portal can paginate.
    /// </summary>
    Task<(IReadOnlyList<Claim> Page, int TotalCount)> SearchForMemberAsync(
        string memberId,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        ClaimStatus? status,
        string? providerNPI,
        ClaimType? claimType,
        decimal? amountMin,
        decimal? amountMax,
        int page,
        int pageSize);

    Task<ClaimsSummary> GetClaimsSummaryAsync(DateTime from, DateTime to, LineOfBusiness? lineOfBusiness);

    /// <summary>
    /// Aggregate finalized claim cost-share amounts by accumulator type and network tier
    /// for a member (Individual scope) or all family members (Family scope).
    ///
    /// Called by the Redis accumulator service on a cache miss to rebuild from claim history.
    /// The plan year is converted to a Jan 1 – Dec 31 date range for standard calendar plans.
    /// </summary>
    /// <param name="ownerId">memberId for Individual scope; subscriberId for Family scope.</param>
    /// <param name="scope">"Individual" or "Family"</param>
    Task<AccumulatorTotalsResponse> GetAccumulatorTotalsAsync(
        string ownerId,
        string scope,
        string benefitPlanId,
        string planYear,
        CancellationToken ct = default);

    Task<Claim> CreateAsync(Claim claim);
    Task<Claim> UpdateAsync(Claim claim);
    Task DeleteAsync(string id);

    // ── Versioning surface (5.1) ─────────────────────────────────────────
    // Mirrors IProviderRepository / IBenefitPlanRepository. asOf is the
    // time-travel pivot; passing DateTime.UtcNow returns the current head
    // version. Capability 5.6 (network/credentialing-as-of-service-date)
    // is the first consumer of asOf semantics on claims.

    /// <summary>
    /// Latest non-Draft version of the chain identified by
    /// <paramref name="claimVersionId"/> in effect at <paramref name="asOf"/>.
    /// "In effect" means <c>PublishedAt &lt;= asOf</c> and either
    /// <c>SupersededAt</c> is null or <c>SupersededAt &gt; asOf</c>. Returns
    /// null when no such version exists.
    /// </summary>
    Task<Claim?> GetLatestVersionAsync(string claimVersionId, DateTime asOf);

    /// <summary>Look up a single version by its per-row <c>Id</c>.</summary>
    Task<Claim?> GetVersionAsync(string claimVersionId, string versionId);

    /// <summary>
    /// Newest-first list of every version for the chain identified by
    /// <paramref name="claimVersionId"/>, paginated with a continuation
    /// token. Mirrors <c>IProviderRepository.ListVersionsAsync</c>.
    /// </summary>
    Task<(IReadOnlyList<Claim> Items, string? ContinuationToken)> ListVersionsAsync(
        string claimVersionId, int pageSize, string? continuationToken);

    /// <summary>
    /// Projection-metadata bypass for adjudication writes. Patches only the
    /// adjudication-related fields on the head version of
    /// <paramref name="claimVersionId"/> for <paramref name="tenantId"/>;
    /// does NOT create a new version row and does NOT trip the
    /// <see cref="UpdateAsync"/> terminal-state guard.
    ///
    /// 5th instance of the projection-metadata bypass pattern (Provider 5.4.5
    /// integrity, Provider 5.6 credentialing, Provider 5.7+ panel-gating, BP
    /// 5.5 network tiers). Same justification: adjudication state is
    /// operationally distinct from claim identity, and each adjudication run
    /// shouldn't produce a new version. Adjustments DO produce new versions
    /// via <see cref="UpdateAsync"/>; this bypass is for the routine path.
    ///
    /// Returns true on success, false when no head row was found for the
    /// chain.
    /// </summary>
    Task<bool> UpdateAdjudicationProjectionAsync(
        string tenantId,
        string claimVersionId,
        AdjudicationResult adjudicationResult,
        IReadOnlyList<LineAdjudicationResult> lineResults,
        CancellationToken ct = default);
}

public class ClaimRepository : IClaimRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ClaimRepository> _logger;

    public ClaimRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ClaimRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "ClaimsDB";
        var containerName = configuration["CosmosDb:ContainerName"] ?? "Claims";

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

    /// <summary>
    /// Hydrates legacy claim documents (predating versioning fields) with
    /// sensible defaults. Idempotent — running on a fully-versioned row
    /// is a no-op. Mirrors <c>ProviderRepository.Hydrate</c>.
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
            claim.VersionState = MapStatusToVersionState(claim.Status);
        }
        return claim;
    }

    /// <summary>
    /// Maps the legacy <see cref="ClaimStatus"/> operational signal onto a
    /// versioning <see cref="ClaimVersionState"/>. Public so the Mongo repo
    /// and tests share one canonical mapping; the table is documented in
    /// docs/architecture/claim-versioning.md.
    /// </summary>
    public static ClaimVersionState MapStatusToVersionState(ClaimStatus status) => status switch
    {
        ClaimStatus.Submitted or
        ClaimStatus.Received or
        ClaimStatus.InAdjudication or
        ClaimStatus.Pended => ClaimVersionState.Submitted,
        ClaimStatus.Approved => ClaimVersionState.Adjudicated,
        ClaimStatus.Paid or
        ClaimStatus.PartiallyPaid => ClaimVersionState.Paid,
        ClaimStatus.Denied => ClaimVersionState.Denied,
        ClaimStatus.Voided => ClaimVersionState.Voided,
        _ => ClaimVersionState.Submitted
    };

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

        try
        {
            var response = await _container.ReadItemAsync<Claim>(
                id,
                new PartitionKey(id));

            // Verify tenant isolation
            if (response.Resource.TenantId != tenantId)
            {
                return null;
            }

            return Hydrate(response.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Claim?> GetByClaimNumberAsync(string claimNumber)
    {
        var tenantId = GetTenantId();

        // TOP 1 + ORDER BY versionNumber DESC keeps RU cost bounded — only
        // the head version is needed; pulling every version-row for the
        // claim into memory just to take the first wastes RUs at scale.
        var query = new QueryDefinition(
            "SELECT TOP 1 * FROM c WHERE c.tenantId = @tenantId AND c.claimNumber = @claimNumber " +
            "ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimNumber", claimNumber);

        var iterator = _container.GetItemQueryIterator<Claim>(query);
        if (!iterator.HasMoreResults) return null;
        var page = await iterator.ReadNextAsync();
        var head = page.FirstOrDefault();
        return head != null ? Hydrate(head) : null;
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

        // Build dynamic query
        var conditions = new List<string> { "c.tenantId = @tenantId" };
        var parameters = new Dictionary<string, object> { { "@tenantId", tenantId } };

        if (!string.IsNullOrEmpty(memberId))
        {
            conditions.Add("c.memberId = @memberId");
            parameters["@memberId"] = memberId;
        }

        if (!string.IsNullOrEmpty(providerNPI))
        {
            conditions.Add("(c.billingProviderNPI = @providerNPI OR c.renderingProviderNPI = @providerNPI)");
            parameters["@providerNPI"] = providerNPI;
        }

        if (serviceDateFrom.HasValue)
        {
            conditions.Add("c.serviceDateFrom >= @serviceDateFrom");
            parameters["@serviceDateFrom"] = serviceDateFrom.Value;
        }

        if (serviceDateTo.HasValue)
        {
            conditions.Add("c.serviceDateTo <= @serviceDateTo");
            parameters["@serviceDateTo"] = serviceDateTo.Value;
        }

        if (status.HasValue)
        {
            conditions.Add("c.status = @status");
            parameters["@status"] = status.Value.ToString();
        }

        if (lineOfBusiness.HasValue)
        {
            conditions.Add("c.lineOfBusiness = @lineOfBusiness");
            parameters["@lineOfBusiness"] = lineOfBusiness.Value.ToString();
        }

        var queryText = $@"
            SELECT * FROM c
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY c.submittedDate DESC
            OFFSET {(page - 1) * pageSize} LIMIT {pageSize}";

        var queryDef = new QueryDefinition(queryText);
        foreach (var (key, value) in parameters)
        {
            queryDef.WithParameter(key, value);
        }

        var iterator = _container.GetItemQueryIterator<Claim>(queryDef);
        var results = new List<Claim>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results.Select(Hydrate);
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

        var conditions = new List<string>
        {
            "c.tenantId = @tenantId",
            "c.memberId = @memberId"
        };
        var parameters = new Dictionary<string, object>
        {
            ["@tenantId"] = tenantId,
            ["@memberId"] = memberId
        };

        if (serviceDateFrom.HasValue)
        {
            conditions.Add("c.serviceDateFrom >= @serviceDateFrom");
            parameters["@serviceDateFrom"] = serviceDateFrom.Value;
        }
        if (serviceDateTo.HasValue)
        {
            conditions.Add("c.serviceDateTo <= @serviceDateTo");
            parameters["@serviceDateTo"] = serviceDateTo.Value;
        }
        if (status.HasValue)
        {
            conditions.Add("c.status = @status");
            parameters["@status"] = status.Value.ToString();
        }
        if (!string.IsNullOrEmpty(providerNPI))
        {
            conditions.Add("(c.billingProviderNPI = @providerNPI OR c.renderingProviderNPI = @providerNPI)");
            parameters["@providerNPI"] = providerNPI;
        }
        if (claimType.HasValue)
        {
            conditions.Add("c.claimType = @claimType");
            parameters["@claimType"] = claimType.Value.ToString();
        }
        if (amountMin.HasValue)
        {
            conditions.Add("c.totalChargeAmount >= @amountMin");
            parameters["@amountMin"] = amountMin.Value;
        }
        if (amountMax.HasValue)
        {
            conditions.Add("c.totalChargeAmount <= @amountMax");
            parameters["@amountMax"] = amountMax.Value;
        }

        var where = string.Join(" AND ", conditions);

        var countQuery = new QueryDefinition($"SELECT VALUE COUNT(1) FROM c WHERE {where}");
        foreach (var (k, v) in parameters) countQuery.WithParameter(k, v);

        var totalCount = 0;
        var countIterator = _container.GetItemQueryIterator<int>(countQuery);
        while (countIterator.HasMoreResults)
        {
            var response = await countIterator.ReadNextAsync();
            totalCount += response.FirstOrDefault();
        }

        var pageQuery = new QueryDefinition(
            $"SELECT * FROM c WHERE {where} " +
            $"ORDER BY c.submittedDate DESC " +
            $"OFFSET {(page - 1) * pageSize} LIMIT {pageSize}");
        foreach (var (k, v) in parameters) pageQuery.WithParameter(k, v);

        var items = new List<Claim>();
        var iterator = _container.GetItemQueryIterator<Claim>(pageQuery);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            items.AddRange(response);
        }

        return (items.Select(Hydrate).ToList(), totalCount);
    }

    public async Task<ClaimsSummary> GetClaimsSummaryAsync(
        DateTime from,
        DateTime to,
        LineOfBusiness? lineOfBusiness)
    {
        var tenantId = GetTenantId();

        var lobCondition = lineOfBusiness.HasValue
            ? "AND c.lineOfBusiness = @lineOfBusiness"
            : "";

        var queryText = $@"
            SELECT
                COUNT(1) as TotalClaims,
                SUM(CASE WHEN c.status = 'Approved' THEN 1 ELSE 0 END) as ApprovedClaims,
                SUM(CASE WHEN c.status = 'Denied' THEN 1 ELSE 0 END) as DeniedClaims,
                SUM(CASE WHEN c.status = 'Pended' THEN 1 ELSE 0 END) as PendedClaims,
                SUM(CASE WHEN c.status = 'Paid' THEN 1 ELSE 0 END) as PaidClaims,
                SUM(c.totalChargeAmount) as TotalChargeAmount,
                SUM(c.adjudicationResult.allowedAmount ?? 0) as TotalAllowedAmount,
                SUM(c.adjudicationResult.payerPayment ?? 0) as TotalPaidAmount
            FROM c
            WHERE c.tenantId = @tenantId
            AND c.submittedDate >= @from
            AND c.submittedDate <= @to
            {lobCondition}";

        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@from", from)
            .WithParameter("@to", to);

        if (lineOfBusiness.HasValue)
        {
            queryDef.WithParameter("@lineOfBusiness", lineOfBusiness.Value.ToString());
        }

        var iterator = _container.GetItemQueryIterator<dynamic>(queryDef);
        var summary = new ClaimsSummary();

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            var result = response.FirstOrDefault();

            if (result != null)
            {
                summary.TotalClaims = result.TotalClaims ?? 0;
                summary.ApprovedClaims = result.ApprovedClaims ?? 0;
                summary.DeniedClaims = result.DeniedClaims ?? 0;
                summary.PendedClaims = result.PendedClaims ?? 0;
                summary.PaidClaims = result.PaidClaims ?? 0;
                summary.TotalChargeAmount = result.TotalChargeAmount ?? 0;
                summary.TotalAllowedAmount = result.TotalAllowedAmount ?? 0;
                summary.TotalPaidAmount = result.TotalPaidAmount ?? 0;

                // Calculate approval rate
                if (summary.TotalClaims > 0)
                {
                    summary.ApprovalRate = (decimal)summary.ApprovedClaims / summary.TotalClaims * 100;
                }
            }
        }

        // Calculate average processing days (separate query for adjudicated claims)
        var processingQueryText = $@"
            SELECT AVG(
                DateTimeDiff('day', c.submittedDate, c.adjudicatedDate)
            ) as AvgDays
            FROM c
            WHERE c.tenantId = @tenantId
            AND c.submittedDate >= @from
            AND c.submittedDate <= @to
            AND c.adjudicatedDate != null
            {lobCondition}";

        var processingQueryDef = new QueryDefinition(processingQueryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@from", from)
            .WithParameter("@to", to);

        if (lineOfBusiness.HasValue)
        {
            processingQueryDef.WithParameter("@lineOfBusiness", lineOfBusiness.Value.ToString());
        }

        var processingIterator = _container.GetItemQueryIterator<dynamic>(processingQueryDef);
        if (processingIterator.HasMoreResults)
        {
            var response = await processingIterator.ReadNextAsync();
            var result = response.FirstOrDefault();
            summary.AverageProcessingDays = result?.AvgDays ?? 0;
        }

        return summary;
    }

    public async Task<Claim> CreateAsync(Claim claim)
    {
        var tenantId = GetTenantId();
        claim.TenantId = tenantId;

        if (string.IsNullOrEmpty(claim.Id))
        {
            claim.Id = Guid.NewGuid().ToString();
        }

        // Initialize the version chain if the caller hasn't done so. New claims
        // start at VersionState=Submitted (matching the existing default
        // ClaimStatus.Submitted on uninitialized rows). Capability 5.3
        // (Submission API) ratified this Submitted-on-create behavior — there
        // is no Draft state in the canonical payer submission flow; Draft is
        // reserved for the future adjustment workflow (capability 5.12).
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
            claim.VersionState = MapStatusToVersionState(claim.Status);
        }

        var response = await _container.CreateItemAsync(claim, new PartitionKey(claim.Id));
        return response.Resource;
    }

    public async Task<Claim> UpdateAsync(Claim claim)
    {
        var tenantId = GetTenantId();
        claim.TenantId = tenantId;

        // Reject mutations on terminal versions. Adjustments must go through
        // the explicit "create new version with PredecessorVersionId" path
        // (capability 5.12). This guard mirrors ProviderRepository.UpdateAsync.
        Claim? existing;
        try
        {
            var read = await _container.ReadItemAsync<Claim>(claim.Id, new PartitionKey(claim.Id));
            existing = read.Resource.TenantId == tenantId ? Hydrate(read.Resource) : null;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            existing = null;
        }

        if (existing == null)
        {
            // Surface a domain-specific not-found rather than letting the
            // ReplaceItemAsync 404 bubble through ExceptionHandlingMiddleware
            // as a 500. Controllers map IsNotFound to HTTP 404.
            throw new ClaimVersionStateException(
                claim.ClaimVersionId, claim.Id, claim.VersionState,
                $"Claim version {claim.Id} not found for update.")
            {
                IsNotFound = true
            };
        }

        if (IsTerminal(existing.VersionState))
        {
            throw new ClaimVersionStateException(
                existing.ClaimVersionId, existing.Id, existing.VersionState,
                $"Claim version {existing.Id} is in terminal state {existing.VersionState} and cannot be updated. " +
                "Create an adjustment version via the adjustment workflow.");
        }

        try
        {
            var response = await _container.ReplaceItemAsync(
                claim,
                claim.Id,
                new PartitionKey(claim.Id));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Race: the row was deleted between the pre-read above and the
            // replace. Same domain-specific not-found surface.
            throw new ClaimVersionStateException(
                claim.ClaimVersionId, claim.Id, claim.VersionState,
                $"Claim version {claim.Id} not found for update (deleted concurrently).")
            {
                IsNotFound = true
            };
        }
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        await _container.DeleteItemAsync<Claim>(id, new PartitionKey(id));
    }

    // ── Versioning surface (5.1) ─────────────────────────────────────────

    public async Task<Claim?> GetLatestVersionAsync(string claimVersionId, DateTime asOf)
    {
        var tenantId = GetTenantId();

        // "In effect at asOf" means PublishedAt <= asOf AND
        // (SupersededAt is null OR SupersededAt > asOf). For legacy rows
        // missing PublishedAt/SupersededAt/versionState, the (NOT IS_DEFINED
        // OR null) clauses keep them visible — hydration on read maps
        // Status onto VersionState so the caller sees the legacy row as the
        // head. The versionState predicate uses (NOT IS_DEFINED OR ...)
        // because Cosmos SQL evaluates undefined-vs-anything as undefined
        // (≠ true), which would silently drop legacy rows.
        var query = new QueryDefinition(@"
            SELECT TOP 1 *
            FROM c
            WHERE c.tenantId = @tenantId
              AND (c.claimVersionId = @claimVersionId
                   OR (NOT IS_DEFINED(c.claimVersionId) AND c.id = @claimVersionId)
                   OR (c.claimVersionId = '' AND c.id = @claimVersionId))
              AND (NOT IS_DEFINED(c.versionState) OR c.versionState = null OR c.versionState != @draft)
              AND (NOT IS_DEFINED(c.publishedAt) OR c.publishedAt = null OR c.publishedAt <= @asOf)
              AND (NOT IS_DEFINED(c.supersededAt) OR c.supersededAt = null OR c.supersededAt > @asOf)
            ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimVersionId", claimVersionId)
            .WithParameter("@draft", ClaimVersionState.Draft.ToString())
            .WithParameter("@asOf", asOf);

        var iterator = _container.GetItemQueryIterator<Claim>(query);
        if (!iterator.HasMoreResults) return null;
        var page = await iterator.ReadNextAsync();
        var head = page.FirstOrDefault();
        return head != null ? Hydrate(head) : null;
    }

    public async Task<Claim?> GetVersionAsync(string claimVersionId, string versionId)
    {
        var tenantId = GetTenantId();

        var query = new QueryDefinition(@"
            SELECT TOP 1 *
            FROM c
            WHERE c.tenantId = @tenantId
              AND c.id = @versionId
              AND (c.claimVersionId = @claimVersionId
                   OR (NOT IS_DEFINED(c.claimVersionId) AND c.id = @claimVersionId)
                   OR (c.claimVersionId = '' AND c.id = @claimVersionId))")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@versionId", versionId)
            .WithParameter("@claimVersionId", claimVersionId);

        var iterator = _container.GetItemQueryIterator<Claim>(query);
        if (!iterator.HasMoreResults) return null;
        var page = await iterator.ReadNextAsync();
        var match = page.FirstOrDefault();
        return match != null ? Hydrate(match) : null;
    }

    public async Task<(IReadOnlyList<Claim> Items, string? ContinuationToken)> ListVersionsAsync(
        string claimVersionId, int pageSize, string? continuationToken)
    {
        var tenantId = GetTenantId();

        var query = new QueryDefinition(@"
            SELECT *
            FROM c
            WHERE c.tenantId = @tenantId
              AND (c.claimVersionId = @claimVersionId
                   OR (NOT IS_DEFINED(c.claimVersionId) AND c.id = @claimVersionId)
                   OR (c.claimVersionId = '' AND c.id = @claimVersionId))
            ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimVersionId", claimVersionId);

        var iterator = _container.GetItemQueryIterator<Claim>(
            query,
            continuationToken,
            new QueryRequestOptions { MaxItemCount = pageSize });

        if (!iterator.HasMoreResults)
        {
            return (Array.Empty<Claim>(), null);
        }

        var page = await iterator.ReadNextAsync();
        var items = page.Select(Hydrate).ToList();
        return (items, page.ContinuationToken);
    }

    public async Task<bool> UpdateAdjudicationProjectionAsync(
        string tenantId,
        string claimVersionId,
        AdjudicationResult adjudicationResult,
        IReadOnlyList<LineAdjudicationResult> lineResults,
        CancellationToken ct = default)
    {
        // Resolve the head (non-terminal-but-adjudicatable) row by chain key.
        // PatchItemAsync is keyed on the per-row document Id, so we look up
        // the row id first. We accept any version that isn't Draft or
        // Voided — adjudication runs against Submitted / Adjudicated rows
        // (re-adjudication is allowed).
        var query = new QueryDefinition(@"
            SELECT TOP 1 c.id
            FROM c
            WHERE c.tenantId = @tenantId
              AND (c.claimVersionId = @claimVersionId
                   OR (NOT IS_DEFINED(c.claimVersionId) AND c.id = @claimVersionId)
                   OR (c.claimVersionId = '' AND c.id = @claimVersionId))
              AND (NOT IS_DEFINED(c.versionState)
                   OR c.versionState = @submitted
                   OR c.versionState = @adjudicated
                   OR c.versionState = @unknown)
            ORDER BY c.versionNumber DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimVersionId", claimVersionId)
            .WithParameter("@submitted", ClaimVersionState.Submitted.ToString())
            .WithParameter("@adjudicated", ClaimVersionState.Adjudicated.ToString())
            .WithParameter("@unknown", ClaimVersionState.Unknown.ToString());

        string? rowId = null;
        var iterator = _container.GetItemQueryIterator<HeadIdResult>(query);
        if (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            rowId = page.FirstOrDefault()?.Id;
        }

        if (string.IsNullOrEmpty(rowId)) return false;

        // Apply the line adjudication results to the head row's claim lines
        // by line number. We can't patch nested array elements positionally
        // in Cosmos without knowing indexes, so we read-modify-write the
        // ClaimLines array. AdjudicationResult itself is a flat patch.
        Claim? head;
        try
        {
            var read = await _container.ReadItemAsync<Claim>(rowId, new PartitionKey(rowId), cancellationToken: ct);
            head = read.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        // The query above accepts rows with NOT IS_DEFINED(c.versionState)
        // so legacy claims (no version fields) can still receive
        // adjudication writes. But a legacy row with Status=Paid hydrates
        // to VersionState=Paid — which is terminal and must NOT be patched.
        // Hydrate then enforce the terminal-state guard before mutating.
        head = Hydrate(head);
        if (IsTerminal(head.VersionState)) return false;

        // The adjudication orchestrator (5.5) emits one LineAdjudicationResult
        // per ClaimLine in claim-line order; counts that disagree are a 5.5
        // input-validation issue, not a 5.1 bypass concern. Apply when shapes
        // match; otherwise leave existing line results untouched.
        if (head.ClaimLines.Count == lineResults.Count)
        {
            for (var i = 0; i < head.ClaimLines.Count; i++)
            {
                head.ClaimLines[i].AdjudicationResult = lineResults[i];
            }
        }

        var ops = new List<PatchOperation>
        {
            PatchOperation.Set("/adjudicationResult", adjudicationResult),
            PatchOperation.Set("/claimLines", head.ClaimLines),
            PatchOperation.Set("/lastUpdatedDate", DateTime.UtcNow),
        };

        try
        {
            await _container.PatchItemAsync<Claim>(
                rowId,
                new PartitionKey(rowId),
                ops,
                cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Row deleted between lookup and patch.
            return false;
        }
    }

    public async Task<AccumulatorTotalsResponse> GetAccumulatorTotalsAsync(
        string ownerId,
        string scope,
        string benefitPlanId,
        string planYear,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();

        // Standard calendar plan year — plan-year-aware plans should store explicit dates
        // but planYear == "2026" → Jan 1 2026 through Dec 31 2026 is the safe default.
        var yearStart = new DateTime(int.Parse(planYear), 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = new DateTime(int.Parse(planYear), 12, 31, 23, 59, 59, DateTimeKind.Utc);

        // For Individual scope filter by memberId; for Family scope by subscriberId.
        var ownerFilter = scope == "Family"
            ? "c.subscriberId = @ownerId"
            : "c.memberId = @ownerId";

        // Filter on (a) the legacy ClaimStatus values that have always counted
        // toward accumulators AND (b) the new ClaimVersionState values that
        // map to the same operational notion. Either clause matching keeps a
        // row in the result set, so legacy unhydrated rows continue to count
        // and new versioned rows count once they reach Adjudicated/Paid.
        // Note: the legacy ClaimStatus filter compares the integer-serialized
        // enum against string literals; that is a pre-existing oddity tracked
        // outside 5.1's scope. The versionState clause is the forward path.
        var queryText = $@"
            SELECT c.adjudicationResult.deductibleAmount,
                   c.adjudicationResult.coinsuranceAmount,
                   c.adjudicationResult.copayAmount,
                   c.adjudicationResult.patientResponsibility,
                   c.adjudicationResult.networkTier
            FROM c
            WHERE c.tenantId       = @tenantId
              AND {ownerFilter}
              AND c.benefitPlanId  = @benefitPlanId
              AND c.serviceDateFrom >= @yearStart
              AND c.serviceDateFrom <= @yearEnd
              AND (
                    c.status = 'Approved' OR c.status = 'PartiallyPaid' OR c.status = 'Paid'
                    OR c.versionState = @adjudicated OR c.versionState = @paid
                  )
              AND IS_DEFINED(c.adjudicationResult)";

        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId",      tenantId)
            .WithParameter("@ownerId",       ownerId)
            .WithParameter("@benefitPlanId", benefitPlanId)
            .WithParameter("@yearStart",     yearStart)
            .WithParameter("@yearEnd",       yearEnd)
            .WithParameter("@adjudicated",   ClaimVersionState.Adjudicated.ToString())
            .WithParameter("@paid",          ClaimVersionState.Paid.ToString());

        var iterator = _container.GetItemQueryIterator<dynamic>(queryDef);

        // Accumulate by network tier
        var deductible   = new Dictionary<string, decimal>();
        var oop          = new Dictionary<string, decimal>();
        var coinsurance  = new Dictionary<string, decimal>();
        var copay        = new Dictionary<string, decimal>();

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            foreach (var row in page)
            {
                var tier = (string?)row.networkTier ?? "InNetwork";

                deductible[tier]  = (deductible.GetValueOrDefault(tier))  + (decimal)(row.deductibleAmount  ?? 0.0);
                oop[tier]         = (oop.GetValueOrDefault(tier))         + (decimal)(row.patientResponsibility ?? 0.0);
                coinsurance[tier] = (coinsurance.GetValueOrDefault(tier)) + (decimal)(row.coinsuranceAmount ?? 0.0);
                copay[tier]       = (copay.GetValueOrDefault(tier))       + (decimal)(row.copayAmount       ?? 0.0);
            }
        }

        // Map to accumulator type names the benefit engine understands.
        // Individual scope → IndividualDeductible / IndividualOutOfPocketMax
        // Family scope     → FamilyDeductible     / FamilyOutOfPocketMax
        var deductibleType = scope == "Family" ? "FamilyDeductible"    : "IndividualDeductible";
        var oopType        = scope == "Family" ? "FamilyOutOfPocketMax" : "IndividualOutOfPocketMax";

        var totals = new List<AccumulatorTotalEntry>();

        foreach (var (tier, amount) in deductible)
            if (amount > 0) totals.Add(new AccumulatorTotalEntry { AccumulatorType = deductibleType,  NetworkTier = tier, AccumulatedAmount = amount });

        foreach (var (tier, amount) in oop)
            if (amount > 0) totals.Add(new AccumulatorTotalEntry { AccumulatorType = oopType,         NetworkTier = tier, AccumulatedAmount = amount });

        // Coinsurance and copay also count toward OOP — they are already included in
        // patientResponsibility above, so we don't double-count here.  They are surfaced
        // as separate entries so the portal can display the breakdown by type.
        foreach (var (tier, amount) in coinsurance)
            if (amount > 0) totals.Add(new AccumulatorTotalEntry { AccumulatorType = "Coinsurance", NetworkTier = tier, AccumulatedAmount = amount });

        foreach (var (tier, amount) in copay)
            if (amount > 0) totals.Add(new AccumulatorTotalEntry { AccumulatorType = "Copay",       NetworkTier = tier, AccumulatedAmount = amount });

        return new AccumulatorTotalsResponse { Totals = totals };
    }

    private sealed class HeadIdResult
    {
        public string Id { get; set; } = string.Empty;
    }
}
