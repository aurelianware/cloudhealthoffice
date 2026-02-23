using Microsoft.Azure.Cosmos;
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
    Task<ClaimsSummary> GetClaimsSummaryAsync(DateTime from, DateTime to, LineOfBusiness? lineOfBusiness);
    Task<Claim> CreateAsync(Claim claim);
    Task<Claim> UpdateAsync(Claim claim);
    Task DeleteAsync(string id);
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
            
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Claim?> GetByClaimNumberAsync(string claimNumber)
    {
        var tenantId = GetTenantId();

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.claimNumber = @claimNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimNumber", claimNumber);

        var iterator = _container.GetItemQueryIterator<Claim>(query);
        var results = new List<Claim>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results.FirstOrDefault();
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

        return results;
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

        var response = await _container.CreateItemAsync(claim, new PartitionKey(claim.Id));
        return response.Resource;
    }

    public async Task<Claim> UpdateAsync(Claim claim)
    {
        var tenantId = GetTenantId();
        claim.TenantId = tenantId;

        var response = await _container.ReplaceItemAsync(
            claim,
            claim.Id,
            new PartitionKey(claim.Id));
        return response.Resource;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        await _container.DeleteItemAsync<Claim>(id, new PartitionKey(id));
    }
}
