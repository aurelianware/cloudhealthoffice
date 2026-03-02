using Microsoft.Azure.Cosmos;
using PaymentService.Models;

namespace PaymentService.Repositories;

public interface IPaymentRunRepository
{
    Task<PaymentRun?> GetByIdAsync(string id);
    Task<PaymentRun?> GetByPaymentRunNumberAsync(string paymentRunNumber);
    Task<IEnumerable<PaymentRun>> SearchAsync(DateTime from, DateTime to, PaymentRunStatus? status = null);
    Task<PaymentRun> CreateAsync(PaymentRun paymentRun);
    Task<PaymentRun> UpdateAsync(PaymentRun paymentRun);
    Task DeleteAsync(string id);
}

public class PaymentRunRepository : IPaymentRunRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<PaymentRunRepository> _logger;

    public PaymentRunRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<PaymentRunRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = "PaymentRuns"; // Separate container for payment runs

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

    public async Task<PaymentRun?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();

        try
        {
            var response = await _container.ReadItemAsync<PaymentRun>(
                id,
                new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<PaymentRun?> GetByPaymentRunNumberAsync(string paymentRunNumber)
    {
        var tenantId = GetTenantId();

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.paymentRunNumber = @paymentRunNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@paymentRunNumber", paymentRunNumber);

        var iterator = _container.GetItemQueryIterator<PaymentRun>(query);
        var results = new List<PaymentRun>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results.FirstOrDefault();
    }

    public async Task<IEnumerable<PaymentRun>> SearchAsync(DateTime from, DateTime to, PaymentRunStatus? status = null)
    {
        var tenantId = GetTenantId();

        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.createdAt >= @from AND c.createdAt <= @to";
        var parameters = new List<(string, object)>
        {
            ("@tenantId", tenantId),
            ("@from", from),
            ("@to", to)
        };

        if (status.HasValue)
        {
            queryText += " AND c.status = @status";
            parameters.Add(("@status", (int)status.Value));
        }

        queryText += " ORDER BY c.createdAt DESC";

        var queryDefinition = new QueryDefinition(queryText);
        foreach (var param in parameters)
        {
            queryDefinition = queryDefinition.WithParameter(param.Item1, param.Item2);
        }

        var iterator = _container.GetItemQueryIterator<PaymentRun>(queryDefinition);
        var results = new List<PaymentRun>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<PaymentRun> CreateAsync(PaymentRun paymentRun)
    {
        paymentRun.TenantId = GetTenantId();
        paymentRun.CreatedAt = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(
            paymentRun,
            new PartitionKey(paymentRun.TenantId));

        _logger.LogInformation("Created payment run {PaymentRunNumber}", paymentRun.PaymentRunNumber);

        return response.Resource;
    }

    public async Task<PaymentRun> UpdateAsync(PaymentRun paymentRun)
    {
        var response = await _container.ReplaceItemAsync(
            paymentRun,
            paymentRun.Id,
            new PartitionKey(paymentRun.TenantId));

        _logger.LogInformation("Updated payment run {PaymentRunNumber}", paymentRun.PaymentRunNumber);

        return response.Resource;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();

        await _container.DeleteItemAsync<PaymentRun>(
            id,
            new PartitionKey(tenantId));

        _logger.LogInformation("Deleted payment run {Id}", SanitizeForLog(id));
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
