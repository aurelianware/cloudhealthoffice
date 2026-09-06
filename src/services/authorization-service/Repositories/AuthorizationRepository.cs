using Microsoft.Azure.Cosmos;
using AuthorizationService.Models;
using AuthorizationService.Services.Retention;

namespace AuthorizationService.Repositories;

public interface IAuthorizationRepository
{
    Task<Authorization?> GetByIdAsync(string id);
    Task<Authorization?> GetByAuthorizationNumberAsync(string authorizationNumber);
    Task<IEnumerable<Authorization>> SearchAsync(
        string? memberId,
        string? providerNPI,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        AuthorizationStatus? status,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize);
    Task<AuthorizationsSummary> GetAuthorizationsSummaryAsync(DateTime from, DateTime to, LineOfBusiness? lineOfBusiness);
    Task<IEnumerable<Authorization>> GetOpenAuthorizationsAsync(string? tenantId = null);
    Task<Authorization> CreateAsync(Authorization authorization);
    Task<Authorization> UpdateAsync(Authorization authorization);
    Task DeleteAsync(string id);

    // ── Retention (PAT-03) ───────────────────────────────────────────────────
    // Background-callable: every method takes the tenant EXPLICITLY, so a sweep
    // never depends on an ambient HttpContext, and a CancellationToken so a
    // shutdown stops mid-sweep rather than at the end of it.

    /// <summary>
    /// Tenants that have authorization data, so a sweep can iterate them
    /// without a second service. Mirrors provider-service's
    /// <c>ListProviderTenantIdsAsync</c>.
    /// </summary>
    Task<IReadOnlyList<string>> ListTenantIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Terminal authorizations for one tenant whose retention anchor is at or
    /// before <paramref name="anchorCutoffUtc"/>, capped at
    /// <paramref name="limit"/>. Coarse filtering only — the caller applies the
    /// retention policy per record before deleting anything.
    /// </summary>
    Task<IReadOnlyList<Authorization>> FindRetentionCandidatesAsync(
        string tenantId, DateTime anchorCutoffUtc, int limit, CancellationToken ct = default);

    /// <summary>
    /// Deletes one authorization ONLY IF it is still in
    /// <paramref name="expectedStatus"/>. Returns false when the record moved on
    /// or was already gone, so a sweep cannot delete a record that became active
    /// again between being listed and being purged. Tenant is explicit and
    /// always part of the delete predicate.
    /// </summary>
    Task<bool> PurgeIfStillEligibleAsync(
        string tenantId, string id, AuthorizationStatus expectedStatus, CancellationToken ct = default);
}

public class AuthorizationRepository : IAuthorizationRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuthorizationRepository> _logger;

    public AuthorizationRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuthorizationRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = configuration["CosmosDb:ContainerName"] ?? "Authorizations";

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

    public async Task<Authorization?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();

        try
        {
            var response = await _container.ReadItemAsync<Authorization>(
                id,
                new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Authorization?> GetByAuthorizationNumberAsync(string authorizationNumber)
    {
        var tenantId = GetTenantId();

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.authorizationNumber = @authorizationNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@authorizationNumber", authorizationNumber);

        var iterator = _container.GetItemQueryIterator<Authorization>(query);
        var results = new List<Authorization>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results.FirstOrDefault();
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
            conditions.Add("(c.requestingProviderNPI = @providerNPI OR c.servicingProviderNPI = @providerNPI)");
            parameters["@providerNPI"] = providerNPI;
        }

        if (serviceDateFrom.HasValue)
        {
            conditions.Add("c.requestedServiceDateFrom >= @serviceDateFrom");
            parameters["@serviceDateFrom"] = serviceDateFrom.Value;
        }

        if (serviceDateTo.HasValue)
        {
            conditions.Add("(c.requestedServiceDateTo <= @serviceDateTo OR c.requestedServiceDateTo = null)");
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

        var iterator = _container.GetItemQueryIterator<Authorization>(queryDef);
        var results = new List<Authorization>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<AuthorizationsSummary> GetAuthorizationsSummaryAsync(
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
                COUNT(1) as TotalAuthorizations,
                SUM(CASE WHEN c.status = 'Approved' THEN 1 ELSE 0 END) as ApprovedAuthorizations,
                SUM(CASE WHEN c.status = 'Denied' THEN 1 ELSE 0 END) as DeniedAuthorizations,
                SUM(CASE WHEN c.status = 'Pended' THEN 1 ELSE 0 END) as PendedAuthorizations,
                SUM(CASE WHEN c.status = 'Modified' THEN 1 ELSE 0 END) as ModifiedAuthorizations
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
        var summary = new AuthorizationsSummary();

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            var result = response.FirstOrDefault();

            if (result != null)
            {
                summary.TotalAuthorizations = result.TotalAuthorizations ?? 0;
                summary.ApprovedAuthorizations = result.ApprovedAuthorizations ?? 0;
                summary.DeniedAuthorizations = result.DeniedAuthorizations ?? 0;
                summary.PendedAuthorizations = result.PendedAuthorizations ?? 0;
                summary.ModifiedAuthorizations = result.ModifiedAuthorizations ?? 0;

                // Calculate approval rate (approved + modified)
                if (summary.TotalAuthorizations > 0)
                {
                    summary.ApprovalRate = (decimal)(summary.ApprovedAuthorizations + summary.ModifiedAuthorizations) /
                                          summary.TotalAuthorizations * 100;
                }
            }
        }

        // AverageReviewDays: raw submission-to-decision time (always from SubmittedDate)
        var reviewQueryText = $@"
            SELECT AVG(
                DateTimeDiff('day', c.submittedDate, c.reviewedDate)
            ) as AvgDays
            FROM c
            WHERE c.tenantId = @tenantId
            AND c.submittedDate >= @from
            AND c.submittedDate <= @to
            AND c.reviewedDate != null
            {lobCondition}";

        var reviewQueryDef = new QueryDefinition(reviewQueryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@from", from)
            .WithParameter("@to", to);

        if (lineOfBusiness.HasValue)
        {
            reviewQueryDef.WithParameter("@lineOfBusiness", lineOfBusiness.Value.ToString());
        }

        var reviewIterator = _container.GetItemQueryIterator<dynamic>(reviewQueryDef);
        if (reviewIterator.HasMoreResults)
        {
            var response = await reviewIterator.ReadNextAsync();
            var result = response.FirstOrDefault();
            summary.AverageReviewDays = result?.AvgDays ?? 0;
        }

        // AverageTurnaroundDays: SLA-adjusted time (from SlaResumedAt when RFAI was issued)
        var turnaroundQueryText = $@"
            SELECT AVG(
                DateTimeDiff('day',
                    IIF(IS_NULL(c.slaResumedAt), c.submittedDate, c.slaResumedAt),
                    c.reviewedDate)
            ) as AvgDays
            FROM c
            WHERE c.tenantId = @tenantId
            AND c.submittedDate >= @from
            AND c.submittedDate <= @to
            AND c.reviewedDate != null
            {lobCondition}";

        var turnaroundQueryDef = new QueryDefinition(turnaroundQueryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@from", from)
            .WithParameter("@to", to);

        if (lineOfBusiness.HasValue)
        {
            turnaroundQueryDef.WithParameter("@lineOfBusiness", lineOfBusiness.Value.ToString());
        }

        var turnaroundIterator = _container.GetItemQueryIterator<dynamic>(turnaroundQueryDef);
        if (turnaroundIterator.HasMoreResults)
        {
            var response = await turnaroundIterator.ReadNextAsync();
            var result = response.FirstOrDefault();
            summary.AverageTurnaroundDays = result?.AvgDays ?? 0;
        }

        return summary;
    }

    // ── Retention (PAT-03) ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> ListTenantIdsAsync(CancellationToken ct = default)
    {
        var queryDef = new QueryDefinition("SELECT DISTINCT VALUE c.tenantId FROM c");
        var iterator = _container.GetItemQueryIterator<string>(queryDef);
        var tenants = new List<string>();

        while (iterator.HasMoreResults)
        {
            ct.ThrowIfCancellationRequested();
            var response = await iterator.ReadNextAsync(ct);
            tenants.AddRange(response.Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        return tenants;
    }

    public async Task<IReadOnlyList<Authorization>> FindRetentionCandidatesAsync(
        string tenantId, DateTime anchorCutoffUtc, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant is required for a retention sweep.", nameof(tenantId));

        // Terminal statuses only, and bounded. The date predicate uses
        // submittedDate as a COARSE floor: a record submitted after the cutoff
        // cannot possibly have a last status change before it, so this is a safe
        // over-select. The policy then decides per record from the real anchor.
        var queryText = @"
            SELECT * FROM c
            WHERE c.tenantId = @tenantId
            AND c.status IN ('Approved', 'Modified', 'Denied', 'Expired', 'Cancelled')
            AND c.submittedDate <= @cutoff
            OFFSET 0 LIMIT @limit";

        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@cutoff", anchorCutoffUtc)
            .WithParameter("@limit", limit);

        var iterator = _container.GetItemQueryIterator<Authorization>(
            queryDef,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId),
                MaxItemCount = limit,
            });

        var results = new List<Authorization>();
        while (iterator.HasMoreResults && results.Count < limit)
        {
            ct.ThrowIfCancellationRequested();
            var response = await iterator.ReadNextAsync(ct);
            results.AddRange(response);
        }

        return results.Count > limit ? results.Take(limit).ToList() : results;
    }

    public async Task<bool> PurgeIfStillEligibleAsync(
        string tenantId, string id, AuthorizationStatus expectedStatus, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant is required for a retention purge.", nameof(tenantId));

        var partition = new PartitionKey(tenantId);

        try
        {
            // Re-read inside the purge and carry the ETag into the delete, so a
            // record that changed between being listed and being deleted fails
            // the precondition instead of being removed. Authorization has no
            // version field of its own; the store's ETag is the concurrency
            // token available here.
            var current = await _container.ReadItemAsync<Authorization>(id, partition, cancellationToken: ct);

            if (current.Resource is null
                || current.Resource.Status != expectedStatus
                || current.Resource.Status.IsOpen())
            {
                return false;
            }

            await _container.DeleteItemAsync<Authorization>(
                id,
                partition,
                new ItemRequestOptions { IfMatchEtag = current.ETag },
                ct);

            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone. A repeated sweep is a no-op, not a failure.
            return false;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            // Changed under us. Leave it; the next sweep re-evaluates.
            return false;
        }
    }

    public async Task<IEnumerable<Authorization>> GetOpenAuthorizationsAsync(string? tenantId = null)
    {
        var effectiveTenantId = tenantId ?? GetTenantId();

        var queryText = @"
            SELECT * FROM c
            WHERE c.tenantId = @tenantId
            AND c.status IN ('Submitted', 'InReview', 'Pended')";

        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId", effectiveTenantId);

        var iterator = _container.GetItemQueryIterator<Authorization>(queryDef);
        var results = new List<Authorization>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<Authorization> CreateAsync(Authorization authorization)
    {
        var tenantId = GetTenantId();
        authorization.TenantId = tenantId;

        var response = await _container.CreateItemAsync(authorization, new PartitionKey(tenantId));
        return response.Resource;
    }

    public async Task<Authorization> UpdateAsync(Authorization authorization)
    {
        var tenantId = GetTenantId();
        authorization.TenantId = tenantId;

        var response = await _container.ReplaceItemAsync(
            authorization,
            authorization.Id,
            new PartitionKey(tenantId));
        return response.Resource;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        await _container.DeleteItemAsync<Authorization>(id, new PartitionKey(tenantId));
    }
}
