using TenantService.Models;

namespace TenantService.Services;

public class TenantUserManagementService : ITenantUserService
{
    private readonly ITenantUserRepository _userRepository;
    private readonly ITenantRoleRepository _roleRepository;
    private readonly ITenantRepository _tenantRepository;
    private readonly ILogger<TenantUserManagementService> _logger;

    public TenantUserManagementService(
        ITenantUserRepository userRepository,
        ITenantRoleRepository roleRepository,
        ITenantRepository tenantRepository,
        ILogger<TenantUserManagementService> logger)
    {
        _userRepository = userRepository;
        _roleRepository = roleRepository;
        _tenantRepository = tenantRepository;
        _logger = logger;
    }

    public async Task<TenantUser> CreateUserAsync(string tenantId, CreateTenantUserRequest request)
    {
        // Verify tenant exists
        var tenant = await _tenantRepository.GetByTenantIdAsync(tenantId);
        if (tenant == null)
            throw new KeyNotFoundException($"Tenant {tenantId} not found");

        // Check for duplicate email within tenant
        if (await _userRepository.ExistsAsync(tenantId, request.Email))
            throw new InvalidOperationException($"User with email {request.Email} already exists in tenant {tenantId}");

        // Validate roles exist
        await ValidateRolesAsync(request.Roles);

        // Validate supervisor exists if specified
        if (!string.IsNullOrEmpty(request.SupervisorId))
        {
            var supervisor = await _userRepository.GetByIdAsync(request.SupervisorId);
            if (supervisor == null || supervisor.TenantId != tenantId)
                throw new InvalidOperationException($"Supervisor {request.SupervisorId} not found in tenant {tenantId}");
        }

        var user = new TenantUser
        {
            TenantId = tenantId,
            Email = request.Email,
            AzureAdObjectId = request.AzureAdObjectId,
            DisplayName = request.DisplayName,
            FirstName = request.FirstName,
            LastName = request.LastName,
            Roles = request.Roles,
            Department = request.Department,
            SupervisorId = request.SupervisorId,
            Status = "Active"
        };

        return await _userRepository.CreateAsync(user);
    }

    public async Task<TenantUser> UpdateUserAsync(string tenantId, string userId, UpdateTenantUserRequest request)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null || user.TenantId != tenantId)
            throw new KeyNotFoundException($"User {userId} not found in tenant {tenantId}");

        if (request.DisplayName != null) user.DisplayName = request.DisplayName;
        if (request.FirstName != null) user.FirstName = request.FirstName;
        if (request.LastName != null) user.LastName = request.LastName;
        if (request.Email != null)
        {
            // Check for duplicate email within tenant (excluding current user)
            var existingWithEmail = await _userRepository.GetByEmailAsync(tenantId, request.Email);
            if (existingWithEmail != null && existingWithEmail.Id != userId)
                throw new InvalidOperationException($"User with email {request.Email} already exists in tenant {tenantId}");
            user.Email = request.Email;
        }
        if (request.AzureAdObjectId != null) user.AzureAdObjectId = request.AzureAdObjectId;
        if (request.Department != null) user.Department = request.Department;
        if (request.SupervisorId != null) user.SupervisorId = request.SupervisorId;

        if (request.Status != null)
        {
            var validStatuses = new[] { "Active", "Disabled", "Locked" };
            if (!validStatuses.Contains(request.Status, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Invalid status: {request.Status}. Valid values: {string.Join(", ", validStatuses)}");
            user.Status = request.Status;
        }

        if (request.Roles != null)
        {
            await ValidateRolesAsync(request.Roles);
            user.Roles = request.Roles;
        }

        return await _userRepository.UpdateAsync(user);
    }

    public async Task<TenantUser?> GetUserAsync(string userId)
    {
        return await _userRepository.GetByIdAsync(userId);
    }

    public async Task<TenantUser?> GetUserByEmailAsync(string tenantId, string email)
    {
        return await _userRepository.GetByEmailAsync(tenantId, email);
    }

    public async Task<TenantUser?> GetUserByAzureAdObjectIdAsync(string azureAdObjectId)
    {
        return await _userRepository.GetByAzureAdObjectIdAsync(azureAdObjectId);
    }

    public async Task<IEnumerable<TenantUser>> GetUsersByTenantAsync(string tenantId)
    {
        return await _userRepository.GetByTenantIdAsync(tenantId);
    }

    public async Task<IEnumerable<TenantUser>> GetUsersByRoleAsync(string tenantId, string roleName)
    {
        return await _userRepository.GetByRoleAsync(tenantId, roleName);
    }

    public async Task<IEnumerable<TenantUser>> GetUsersByDepartmentAsync(string tenantId, string department)
    {
        return await _userRepository.GetByDepartmentAsync(tenantId, department);
    }

    public async Task<IEnumerable<TenantUser>> GetDirectReportsAsync(string tenantId, string supervisorId)
    {
        return await _userRepository.GetBySupervisorIdAsync(tenantId, supervisorId);
    }

    public async Task DeleteUserAsync(string tenantId, string userId)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null || user.TenantId != tenantId)
            throw new KeyNotFoundException($"User {userId} not found in tenant {tenantId}");

        await _userRepository.DeleteAsync(userId);
    }

    public async Task<bool> HasPermissionAsync(string userId, string permission)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null || user.Status != "Active")
            return false;

        var allRoles = await _roleRepository.GetAllAsync();
        return StandardRoles.HasPermission(user.Roles, permission, allRoles);
    }

    public async Task RecordLoginAsync(string userId)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null)
            throw new KeyNotFoundException($"User {userId} not found");

        user.LastLoginAt = DateTime.UtcNow;
        await _userRepository.UpdateAsync(user);
    }

    private async Task ValidateRolesAsync(List<string> roleNames)
    {
        var allRoles = await _roleRepository.GetAllAsync();
        var validRoleNames = allRoles.Select(r => r.RoleName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var invalidRoles = roleNames.Where(r => !validRoleNames.Contains(r)).ToList();
        if (invalidRoles.Any())
            throw new InvalidOperationException($"Invalid roles: {string.Join(", ", invalidRoles)}");
    }
}
