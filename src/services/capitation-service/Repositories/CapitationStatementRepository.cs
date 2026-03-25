using Microsoft.Azure.Cosmos;
using CapitationService.Models;

namespace CapitationService.Repositories;

public interface ICapitationStatementRepository
{
    Task<CapitationStatement?> GetByIdAsync(string id);
    Task<IEnumerable<CapitationStatement>> GetByRunIdAsync(string runId);
    Task<IEnumerable<CapitationStatement>> GetByProviderNpiAsync(string npi, DateTime? periodFrom = null, DateTime? periodTo = null);
    Task<IEnumerable<CapitationStatement>> GetByStatusAsync(CapitationStatementStatus status);
    Task<IEnumerable<CapitationStatement>> GetUnpaidStatementsAsync();
    Task<CapitationStatement> CreateAsync(CapitationStatement statement);
    Task<CapitationStatement> UpdateAsync(CapitationStatement statement);
}

public class CapitationStatementRepository : ICapitationStatementRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<CapitationStatementRepository> _logger;

    public CapitationStatementRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<CapitationStatementRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        _container = cosmosClient.GetContainer(databaseName, "CapitationStatements");
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

    public async Task<CapitationStatement?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        try
        {
            var response = await _container.ReadItemAsync<CapitationStatement>(id, new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IEnumerable<CapitationStatement>> GetByRunIdAsync(string runId)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.capitationRunId = @runId ORDER BY c.providerName")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@runId", runId);

        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<CapitationStatement>> GetByProviderNpiAsync(string npi, DateTime? periodFrom = null, DateTime? periodTo = null)
    {
        var tenantId = GetTenantId();
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.providerNPI = @npi";
        var parameters = new List<(string, object)>
        {
            ("@tenantId", tenantId),
            ("@npi", npi)
        };

        if (periodFrom.HasValue)
        {
            queryText += " AND c.capitationPeriodStart >= @periodFrom";
            parameters.Add(("@periodFrom", periodFrom.Value));
        }
        if (periodTo.HasValue)
        {
            queryText += " AND c.capitationPeriodStart <= @periodTo";
            parameters.Add(("@periodTo", periodTo.Value));
        }

        queryText += " ORDER BY c.capitationPeriodStart DESC";

        var queryDefinition = new QueryDefinition(queryText);
        foreach (var param in parameters)
            queryDefinition = queryDefinition.WithParameter(param.Item1, param.Item2);

        return await ExecuteQueryAsync(queryDefinition);
    }

    public async Task<IEnumerable<CapitationStatement>> GetByStatusAsync(CapitationStatementStatus status)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.status = @status ORDER BY c.capitationPeriodStart DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@status", status.ToString());

        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<CapitationStatement>> GetUnpaidStatementsAsync()
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.status != @paid AND c.status != @voided AND c.netPayable > 0 ORDER BY c.capitationPeriodStart")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@paid", CapitationStatementStatus.Paid.ToString())
            .WithParameter("@voided", CapitationStatementStatus.Voided.ToString());

        return await ExecuteQueryAsync(query);
    }

    public async Task<CapitationStatement> CreateAsync(CapitationStatement statement)
    {
        statement.TenantId = GetTenantId();
        statement.CreatedAt = DateTime.UtcNow;
        statement.LastUpdatedAt = DateTime.UtcNow;
        var response = await _container.CreateItemAsync(statement, new PartitionKey(statement.TenantId));
        _logger.LogInformation("Created capitation statement {StatementNumber} for provider {NPI}",
            statement.StatementNumber, statement.ProviderNPI);
        return response.Resource;
    }

    public async Task<CapitationStatement> UpdateAsync(CapitationStatement statement)
    {
        statement.LastUpdatedAt = DateTime.UtcNow;
        var response = await _container.ReplaceItemAsync(statement, statement.Id, new PartitionKey(statement.TenantId));
        _logger.LogInformation("Updated capitation statement {StatementNumber}", statement.StatementNumber);
        return response.Resource;
    }

    private async Task<List<CapitationStatement>> ExecuteQueryAsync(QueryDefinition query)
    {
        var iterator = _container.GetItemQueryIterator<CapitationStatement>(query);
        var results = new List<CapitationStatement>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }
}
