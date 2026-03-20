using Microsoft.Azure.Cosmos;
using TenantService.Models;

namespace TenantService.Services;

public class TenantUserRepository : ITenantUserRepository
{
    private readonly Container _container;
    private readonly ILogger<TenantUserRepository> _logger;

    public TenantUserRepository(CosmosClient cosmosClient, IConfiguration configuration, ILogger<TenantUserRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = configuration["CosmosDb:UserContainerName"] ?? "TenantUsers";

        _container = cosmosClient.GetContainer(databaseName, containerName);
        _logger = logger;
    }

    public async Task<TenantUser?> GetByIdAsync(string id)
    {
        try
        {
            var response = await _container.ReadItemAsync<TenantUser>(id, new PartitionKey(id));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("TenantUser with ID {UserId} not found", SanitizeForLog(id));
            return null;
        }
    }

    public async Task<TenantUser?> GetByEmailAsync(string tenantId, string email)
    {
        var normalizedEmail = email.ToLowerInvariant();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.emailNormalized = @email")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@email", normalizedEmail);

        var iterator = _container.GetItemQueryIterator<TenantUser>(query);
        var response = await iterator.ReadNextAsync();

        return response.FirstOrDefault();
    }

    public async Task<TenantUser?> GetByAzureAdObjectIdAsync(string azureAdObjectId)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.azureAdObjectId = @oid")
            .WithParameter("@oid", azureAdObjectId);

        var iterator = _container.GetItemQueryIterator<TenantUser>(query);
        var response = await iterator.ReadNextAsync();

        return response.FirstOrDefault();
    }

    public async Task<IEnumerable<TenantUser>> GetByTenantIdAsync(string tenantId)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId ORDER BY c.displayName")
            .WithParameter("@tenantId", tenantId);

        var iterator = _container.GetItemQueryIterator<TenantUser>(query);
        var users = new List<TenantUser>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            users.AddRange(response);
        }

        return users;
    }

    public async Task<IEnumerable<TenantUser>> GetByRoleAsync(string tenantId, string roleName)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND ARRAY_CONTAINS(c.roles, @roleName)")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@roleName", roleName);

        var iterator = _container.GetItemQueryIterator<TenantUser>(query);
        var users = new List<TenantUser>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            users.AddRange(response);
        }

        return users;
    }

    public async Task<IEnumerable<TenantUser>> GetByDepartmentAsync(string tenantId, string department)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.department = @department ORDER BY c.displayName")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@department", department);

        var iterator = _container.GetItemQueryIterator<TenantUser>(query);
        var users = new List<TenantUser>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            users.AddRange(response);
        }

        return users;
    }

    public async Task<IEnumerable<TenantUser>> GetBySupervisorIdAsync(string tenantId, string supervisorId)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.supervisorId = @supervisorId ORDER BY c.displayName")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@supervisorId", supervisorId);

        var iterator = _container.GetItemQueryIterator<TenantUser>(query);
        var users = new List<TenantUser>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            users.AddRange(response);
        }

        return users;
    }

    public async Task<TenantUser> CreateAsync(TenantUser user)
    {
        user.CreatedAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        user.EmailNormalized = user.Email.ToLowerInvariant();

        var response = await _container.CreateItemAsync(user, new PartitionKey(user.Id));
        _logger.LogInformation("Created tenant user {Email} for tenant {TenantId}",
            SanitizeForLog(user.Email), SanitizeForLog(user.TenantId));

        return response.Resource;
    }

    public async Task<TenantUser> UpdateAsync(TenantUser user)
    {
        user.UpdatedAt = DateTime.UtcNow;
        user.EmailNormalized = user.Email.ToLowerInvariant();

        var response = await _container.ReplaceItemAsync(user, user.Id, new PartitionKey(user.Id));
        _logger.LogInformation("Updated tenant user {UserId}", SanitizeForLog(user.Id));

        return response.Resource;
    }

    public async Task DeleteAsync(string id)
    {
        try
        {
            await _container.DeleteItemAsync<TenantUser>(id, new PartitionKey(id));
            _logger.LogInformation("Deleted tenant user {UserId}", SanitizeForLog(id));
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Attempted to delete non-existent user {UserId}", SanitizeForLog(id));
        }
    }

    public async Task<bool> ExistsAsync(string tenantId, string email)
    {
        var user = await GetByEmailAsync(tenantId, email);
        return user != null;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
