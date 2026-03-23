using Microsoft.Azure.Cosmos;
using CapitationService.Models;

namespace CapitationService.Repositories;

public interface ICapitationRunRepository
{
    Task<CapitationRun?> GetByIdAsync(string id);
    Task<IEnumerable<CapitationRun>> SearchAsync(DateTime? from = null, DateTime? to = null, CapitationRunStatus? status = null, LineOfBusiness? lineOfBusiness = null);
    Task<IEnumerable<CapitationRun>> GetByStatusAsync(CapitationRunStatus status);
    Task<CapitationRun> CreateAsync(CapitationRun run);
    Task<CapitationRun> UpdateAsync(CapitationRun run);
}

public class CapitationRunRepository : ICapitationRunRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<CapitationRunRepository> _logger;

    public CapitationRunRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<CapitationRunRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        _container = cosmosClient.GetContainer(databaseName, "CapitationRuns");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("TenantId not found in request context");
        return tenantId;
    }

    public async Task<CapitationRun?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        try
        {
            var response = await _container.ReadItemAsync<CapitationRun>(id, new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IEnumerable<CapitationRun>> SearchAsync(DateTime? from = null, DateTime? to = null, CapitationRunStatus? status = null, LineOfBusiness? lineOfBusiness = null)
    {
        var tenantId = GetTenantId();
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId";
        var parameters = new List<(string, object)> { ("@tenantId", tenantId) };

        if (from.HasValue)
        {
            queryText += " AND c.capitationPeriod >= @from";
            parameters.Add(("@from", from.Value));
        }
        if (to.HasValue)
        {
            queryText += " AND c.capitationPeriod <= @to";
            parameters.Add(("@to", to.Value));
        }
        if (status.HasValue)
        {
            queryText += " AND c.status = @status";
            parameters.Add(("@status", status.Value.ToString()));
        }
        if (lineOfBusiness.HasValue)
        {
            queryText += " AND c.lineOfBusiness = @lineOfBusiness";
            parameters.Add(("@lineOfBusiness", lineOfBusiness.Value.ToString()));
        }

        queryText += " ORDER BY c.createdAt DESC";

        var queryDefinition = new QueryDefinition(queryText);
        foreach (var param in parameters)
            queryDefinition = queryDefinition.WithParameter(param.Item1, param.Item2);

        return await ExecuteQueryAsync(queryDefinition);
    }

    public async Task<IEnumerable<CapitationRun>> GetByStatusAsync(CapitationRunStatus status)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.status = @status ORDER BY c.createdAt DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@status", status.ToString());

        return await ExecuteQueryAsync(query);
    }

    public async Task<CapitationRun> CreateAsync(CapitationRun run)
    {
        run.TenantId = GetTenantId();
        run.CreatedAt = DateTime.UtcNow;
        var response = await _container.CreateItemAsync(run, new PartitionKey(run.TenantId));
        _logger.LogInformation("Created capitation run {RunNumber}", run.RunNumber);
        return response.Resource;
    }

    public async Task<CapitationRun> UpdateAsync(CapitationRun run)
    {
        var response = await _container.ReplaceItemAsync(run, run.Id, new PartitionKey(run.TenantId));
        _logger.LogInformation("Updated capitation run {RunNumber}", run.RunNumber);
        return response.Resource;
    }

    private async Task<List<CapitationRun>> ExecuteQueryAsync(QueryDefinition query)
    {
        var iterator = _container.GetItemQueryIterator<CapitationRun>(query);
        var results = new List<CapitationRun>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }
}
