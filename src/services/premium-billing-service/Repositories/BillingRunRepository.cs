using Microsoft.Azure.Cosmos;
using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

public interface IBillingRunRepository
{
    Task<BillingRun?> GetByIdAsync(string id);
    Task<BillingRun?> GetByBillingRunNumberAsync(string billingRunNumber);
    Task<IEnumerable<BillingRun>> SearchAsync(DateTime? from, DateTime? to, BillingRunStatus? status = null);
    Task<BillingRun> CreateAsync(BillingRun billingRun);
    Task<BillingRun> UpdateAsync(BillingRun billingRun);
    Task DeleteAsync(string id);
}

public class BillingRunRepository : IBillingRunRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<BillingRunRepository> _logger;

    public BillingRunRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<BillingRunRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        _container = cosmosClient.GetContainer(databaseName, "BillingRuns");
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

    public async Task<BillingRun?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        try
        {
            var response = await _container.ReadItemAsync<BillingRun>(id, new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<BillingRun?> GetByBillingRunNumberAsync(string billingRunNumber)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.billingRunNumber = @billingRunNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@billingRunNumber", billingRunNumber);

        var iterator = _container.GetItemQueryIterator<BillingRun>(query);
        var results = new List<BillingRun>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results.FirstOrDefault();
    }

    public async Task<IEnumerable<BillingRun>> SearchAsync(DateTime? from, DateTime? to, BillingRunStatus? status = null)
    {
        var tenantId = GetTenantId();
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId";
        var parameters = new List<(string, object)> { ("@tenantId", tenantId) };

        if (from.HasValue)
        {
            queryText += " AND c.billingPeriod >= @from";
            parameters.Add(("@from", from.Value));
        }
        if (to.HasValue)
        {
            queryText += " AND c.billingPeriod <= @to";
            parameters.Add(("@to", to.Value));
        }
        if (status.HasValue)
        {
            queryText += " AND c.status = @status";
            parameters.Add(("@status", (int)status.Value));
        }

        queryText += " ORDER BY c.createdAt DESC";

        var queryDefinition = new QueryDefinition(queryText);
        foreach (var param in parameters)
            queryDefinition = queryDefinition.WithParameter(param.Item1, param.Item2);

        var iterator = _container.GetItemQueryIterator<BillingRun>(queryDefinition);
        var results = new List<BillingRun>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }

    public async Task<BillingRun> CreateAsync(BillingRun billingRun)
    {
        billingRun.TenantId = GetTenantId();
        billingRun.CreatedAt = DateTime.UtcNow;
        var response = await _container.CreateItemAsync(billingRun, new PartitionKey(billingRun.TenantId));
        _logger.LogInformation("Created billing run {BillingRunNumber}", billingRun.BillingRunNumber);
        return response.Resource;
    }

    public async Task<BillingRun> UpdateAsync(BillingRun billingRun)
    {
        var response = await _container.ReplaceItemAsync(billingRun, billingRun.Id, new PartitionKey(billingRun.TenantId));
        _logger.LogInformation("Updated billing run {BillingRunNumber}", billingRun.BillingRunNumber);
        return response.Resource;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        await _container.DeleteItemAsync<BillingRun>(id, new PartitionKey(tenantId));
        _logger.LogInformation("Deleted billing run {Id}", SanitizeForLog(id));
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
