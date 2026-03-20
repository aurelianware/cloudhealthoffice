using Microsoft.Azure.Cosmos;
using TenantService.Models;

namespace TenantService.Services;

public class TenantRoleRepository : ITenantRoleRepository
{
    private readonly Container _container;
    private readonly ILogger<TenantRoleRepository> _logger;

    public TenantRoleRepository(CosmosClient cosmosClient, IConfiguration configuration, ILogger<TenantRoleRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = configuration["CosmosDb:RoleContainerName"] ?? "TenantRoles";

        _container = cosmosClient.GetContainer(databaseName, containerName);
        _logger = logger;
    }

    public async Task<TenantRole?> GetByRoleNameAsync(string roleName)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.roleName = @roleName")
            .WithParameter("@roleName", roleName);

        var iterator = _container.GetItemQueryIterator<TenantRole>(query);
        var response = await iterator.ReadNextAsync();

        return response.FirstOrDefault();
    }

    public async Task<IEnumerable<TenantRole>> GetAllAsync()
    {
        var query = new QueryDefinition("SELECT * FROM c ORDER BY c.roleName");
        var iterator = _container.GetItemQueryIterator<TenantRole>(query);
        var roles = new List<TenantRole>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            roles.AddRange(response);
        }

        return roles;
    }

    public async Task<TenantRole> CreateAsync(TenantRole role)
    {
        var response = await _container.CreateItemAsync(role, new PartitionKey(role.Id));
        _logger.LogInformation("Created role {RoleName}", SanitizeForLog(role.RoleName));

        return response.Resource;
    }

    public async Task<TenantRole> UpdateAsync(TenantRole role)
    {
        var response = await _container.ReplaceItemAsync(role, role.Id, new PartitionKey(role.Id));
        _logger.LogInformation("Updated role {RoleName}", SanitizeForLog(role.RoleName));

        return response.Resource;
    }

    public async Task DeleteAsync(string roleName)
    {
        var role = await GetByRoleNameAsync(roleName);
        if (role != null)
        {
            await _container.DeleteItemAsync<TenantRole>(role.Id, new PartitionKey(role.Id));
            _logger.LogInformation("Deleted role {RoleName}", SanitizeForLog(roleName));
        }
    }

    public async Task SeedStandardRolesAsync()
    {
        foreach (var standardRole in StandardRoles.All)
        {
            var existing = await GetByRoleNameAsync(standardRole.RoleName);
            if (existing == null)
            {
                var role = new TenantRole
                {
                    RoleName = standardRole.RoleName,
                    Description = standardRole.Description,
                    Permissions = new List<string>(standardRole.Permissions),
                    IsBuiltIn = true
                };

                await CreateAsync(role);
                _logger.LogInformation("Seeded standard role {RoleName}", SanitizeForLog(role.RoleName));
            }
            else
            {
                // Update permissions if the built-in role definition has changed
                // Always enforce IsBuiltIn flag in case a custom role was created with a standard name
                existing.Description = standardRole.Description;
                existing.Permissions = new List<string>(standardRole.Permissions);
                existing.IsBuiltIn = true;
                await UpdateAsync(existing);
            }
        }
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
