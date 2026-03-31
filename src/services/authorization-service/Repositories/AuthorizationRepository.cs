using Microsoft.Azure.Cosmos;
using AuthorizationService.Models;

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
