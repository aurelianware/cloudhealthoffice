using Microsoft.Azure.Cosmos;
using CapitationService.Models;

namespace CapitationService.Repositories;

public interface ICapitationContractRepository
{
    Task<CapitationContract?> GetByIdAsync(string id);
    Task<CapitationContract?> GetByProviderNpiAsync(string npi);
    Task<IEnumerable<CapitationContract>> GetActiveContractsAsync(LineOfBusiness? lob = null, ContractType? type = null);
    Task<IEnumerable<CapitationContract>> GetByPlanIdAsync(string planId);
    Task<IEnumerable<CapitationContract>> SearchAsync(
        string? providerNpi = null,
        LineOfBusiness? lob = null,
        ContractType? type = null,
        CapitationRateConfigStatus? status = null,
        int page = 1,
        int pageSize = 50);
    Task<CapitationContract> CreateAsync(CapitationContract contract);
    Task<CapitationContract> UpdateAsync(CapitationContract contract);
}

public class CapitationContractRepository : ICapitationContractRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<CapitationContractRepository> _logger;

    public CapitationContractRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<CapitationContractRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        _container = cosmosClient.GetContainer(databaseName, "CapitationContracts");
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

    public async Task<CapitationContract?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        try
        {
            var response = await _container.ReadItemAsync<CapitationContract>(id, new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<CapitationContract?> GetByProviderNpiAsync(string npi)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.providerNPI = @npi AND c.status = @active")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@npi", npi)
            .WithParameter("@active", CapitationRateConfigStatus.Active.ToString());

        var results = await ExecuteQueryAsync(query);
        return results.FirstOrDefault();
    }

    public async Task<IEnumerable<CapitationContract>> GetActiveContractsAsync(LineOfBusiness? lob = null, ContractType? type = null)
    {
        var tenantId = GetTenantId();
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.status = @active";
        var parameters = new List<(string, object)>
        {
            ("@tenantId", tenantId),
            ("@active", CapitationRateConfigStatus.Active.ToString())
        };

        if (lob.HasValue)
        {
            queryText += " AND c.lineOfBusiness = @lob";
            parameters.Add(("@lob", lob.Value.ToString()));
        }
        if (type.HasValue)
        {
            queryText += " AND c.contractType = @type";
            parameters.Add(("@type", type.Value.ToString()));
        }

        queryText += " ORDER BY c.providerName";

        var queryDefinition = new QueryDefinition(queryText);
        foreach (var param in parameters)
            queryDefinition = queryDefinition.WithParameter(param.Item1, param.Item2);

        return await ExecuteQueryAsync(queryDefinition);
    }

    public async Task<IEnumerable<CapitationContract>> GetByPlanIdAsync(string planId)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND ARRAY_CONTAINS(c.planIds, @planId) AND c.status = @active ORDER BY c.providerName")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@planId", planId)
            .WithParameter("@active", CapitationRateConfigStatus.Active.ToString());

        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<CapitationContract>> SearchAsync(
        string? providerNpi = null,
        LineOfBusiness? lob = null,
        ContractType? type = null,
        CapitationRateConfigStatus? status = null,
        int page = 1,
        int pageSize = 50)
    {
        var tenantId = GetTenantId();
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId";
        var parameters = new List<(string, object)> { ("@tenantId", tenantId) };

        if (!string.IsNullOrEmpty(providerNpi))
        {
            queryText += " AND c.providerNPI = @npi";
            parameters.Add(("@npi", providerNpi));
        }
        if (lob.HasValue)
        {
            queryText += " AND c.lineOfBusiness = @lob";
            parameters.Add(("@lob", lob.Value.ToString()));
        }
        if (type.HasValue)
        {
            queryText += " AND c.contractType = @type";
            parameters.Add(("@type", type.Value.ToString()));
        }
        if (status.HasValue)
        {
            queryText += " AND c.status = @status";
            parameters.Add(("@status", status.Value.ToString()));
        }

        queryText += " ORDER BY c.createdAt DESC";
        queryText += $" OFFSET {(page - 1) * pageSize} LIMIT {pageSize}";

        var queryDefinition = new QueryDefinition(queryText);
        foreach (var param in parameters)
            queryDefinition = queryDefinition.WithParameter(param.Item1, param.Item2);

        return await ExecuteQueryAsync(queryDefinition);
    }

    public async Task<CapitationContract> CreateAsync(CapitationContract contract)
    {
        contract.TenantId = GetTenantId();
        contract.CreatedAt = DateTime.UtcNow;
        contract.LastUpdatedAt = DateTime.UtcNow;
        var response = await _container.CreateItemAsync(contract, new PartitionKey(contract.TenantId));
        _logger.LogInformation("Created capitation contract {ContractNumber} for provider {NPI}",
            SanitizeForLog(contract.ContractNumber), SanitizeForLog(contract.ProviderNPI));
        return response.Resource;
    }

    public async Task<CapitationContract> UpdateAsync(CapitationContract contract)
    {
        contract.LastUpdatedAt = DateTime.UtcNow;
        var response = await _container.ReplaceItemAsync(contract, contract.Id, new PartitionKey(contract.TenantId));
        _logger.LogInformation("Updated capitation contract {ContractNumber}", SanitizeForLog(contract.ContractNumber));
        return response.Resource;
    }

    private async Task<List<CapitationContract>> ExecuteQueryAsync(QueryDefinition query)
    {
        var iterator = _container.GetItemQueryIterator<CapitationContract>(query);
        var results = new List<CapitationContract>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
