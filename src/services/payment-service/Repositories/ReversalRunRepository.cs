using Microsoft.Azure.Cosmos;
using PaymentService.Models;

namespace PaymentService.Repositories;

/// <summary>
/// Persistence surface for the <see cref="ReversalRun"/> aggregate
/// (capability 5.12b). Mirrors <see cref="IPaymentRunRepository"/>
/// shape — Mongo-canonical with a Cosmos-noop fallback inherited from
/// the same DI branching pattern in <c>Program.cs</c>.
/// </summary>
public interface IReversalRunRepository
{
    Task<ReversalRun?> GetByIdAsync(string id);
    Task<ReversalRun?> GetByReversalRunNumberAsync(string reversalRunNumber);
    Task<IEnumerable<ReversalRun>> SearchAsync(DateTime from, DateTime to, ReversalRunStatus? status = null);
    Task<ReversalRun> CreateAsync(ReversalRun reversalRun);
    Task<ReversalRun> UpdateAsync(ReversalRun reversalRun);
    Task DeleteAsync(string id);
}

/// <summary>
/// Cosmos-backed implementation. Used in dev environments where Cosmos
/// is the configured store. Production deployments typically run on
/// Mongo (<see cref="ReversalRunRepositoryMongo"/>).
/// </summary>
public class ReversalRunRepository : IReversalRunRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ReversalRunRepository> _logger;

    public ReversalRunRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ReversalRunRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = "ReversalRuns";

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

    public async Task<ReversalRun?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();

        try
        {
            var response = await _container.ReadItemAsync<ReversalRun>(
                id,
                new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ReversalRun?> GetByReversalRunNumberAsync(string reversalRunNumber)
    {
        var tenantId = GetTenantId();

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.reversalRunNumber = @reversalRunNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@reversalRunNumber", reversalRunNumber);

        var iterator = _container.GetItemQueryIterator<ReversalRun>(query);
        var results = new List<ReversalRun>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results.FirstOrDefault();
    }

    public async Task<IEnumerable<ReversalRun>> SearchAsync(DateTime from, DateTime to, ReversalRunStatus? status = null)
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

        var iterator = _container.GetItemQueryIterator<ReversalRun>(queryDefinition);
        var results = new List<ReversalRun>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<ReversalRun> CreateAsync(ReversalRun reversalRun)
    {
        reversalRun.TenantId = GetTenantId();
        reversalRun.CreatedAt = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(
            reversalRun,
            new PartitionKey(reversalRun.TenantId));

        _logger.LogInformation("Created reversal run {ReversalRunNumber}", reversalRun.ReversalRunNumber);

        return response.Resource;
    }

    public async Task<ReversalRun> UpdateAsync(ReversalRun reversalRun)
    {
        var response = await _container.ReplaceItemAsync(
            reversalRun,
            reversalRun.Id,
            new PartitionKey(reversalRun.TenantId));

        _logger.LogInformation("Updated reversal run {ReversalRunNumber}", reversalRun.ReversalRunNumber);

        return response.Resource;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();

        await _container.DeleteItemAsync<ReversalRun>(
            id,
            new PartitionKey(tenantId));

        _logger.LogInformation("Deleted reversal run {Id}", SanitizeForLog(id));
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
