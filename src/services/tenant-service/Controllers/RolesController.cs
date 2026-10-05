using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TenantService.Models;
using TenantService.Security;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// The role catalogue. It is one collection shared by every tenant (roles are
/// not stored per tenant), so changing it changes every tenant's catalogue:
/// writes need <c>roles:manage</c> and <c>platform:tenants</c> and are
/// audited. Any authenticated caller may read it.
/// </summary>
[ApiController]
[Route("api/v1/roles")]
public class RolesController : ControllerBase
{
    private readonly ITenantRoleRepository _roleRepository;
    private readonly ICurrentActor _actor;
    private readonly TenantAuditLog _audit;
    private readonly ILogger<RolesController> _logger;

    public RolesController(
        ITenantRoleRepository roleRepository, ICurrentActor actor, TenantAuditLog audit, ILogger<RolesController> logger)
    {
        _roleRepository = roleRepository;
        _actor = actor;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// Get all available roles and their permissions
    /// </summary>
    [HttpGet]
    [Authorize(Policy = TenantPermissions.MemberPolicy)]
    [ProducesResponseType(typeof(IEnumerable<TenantRole>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<TenantRole>>> GetAllRoles()
    {
        var roles = await _roleRepository.GetAllAsync();
        return Ok(roles);
    }

    /// <summary>
    /// Get a specific role by name
    /// </summary>
    [HttpGet("{roleName}")]
    [Authorize(Policy = TenantPermissions.MemberPolicy)]
    [ProducesResponseType(typeof(TenantRole), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantRole>> GetRole(string roleName)
    {
        var role = await _roleRepository.GetByRoleNameAsync(roleName);
        if (role == null)
            return NotFound(new { error = $"Role {roleName} not found" });

        return Ok(role);
    }

    /// <summary>
    /// Create a custom role (non-built-in). Requires roles:manage permission.
    /// </summary>
    [HttpPost]
    [RequirePermission(TenantPermissions.RolesManage)]
    [RequirePermission(TenantPermissions.PlatformTenants)]
    [ProducesResponseType(typeof(TenantRole), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TenantRole>> CreateRole([FromBody] CreateRoleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RoleName))
            return BadRequest(new { error = "RoleName is required" });

        if (request.Permissions == null || request.Permissions.Count == 0)
            return BadRequest(new { error = "At least one permission is required" });

        var existing = await _roleRepository.GetByRoleNameAsync(request.RoleName);
        if (existing != null)
            return BadRequest(new { error = $"Role {request.RoleName} already exists" });

        var role = new TenantRole
        {
            RoleName = request.RoleName,
            Description = request.Description ?? string.Empty,
            Permissions = request.Permissions,
            IsBuiltIn = false,
            CreatedBy = _actor.UserId,
            UpdatedBy = _actor.UserId
        };

        var created = await _roleRepository.CreateAsync(role);
        _audit.Record("create role " + created.RoleName, "*");
        return CreatedAtAction(nameof(GetRole), new { roleName = created.RoleName }, created);
    }

    /// <summary>
    /// Update a custom role's permissions (built-in roles cannot be modified).
    /// Requires roles:manage permission.
    /// </summary>
    [HttpPut("{roleName}")]
    [RequirePermission(TenantPermissions.RolesManage)]
    [RequirePermission(TenantPermissions.PlatformTenants)]
    [ProducesResponseType(typeof(TenantRole), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantRole>> UpdateRole(string roleName, [FromBody] UpdateRoleRequest request)
    {
        var existing = await _roleRepository.GetByRoleNameAsync(roleName);
        if (existing == null)
            return NotFound(new { error = $"Role {roleName} not found" });

        if (existing.IsBuiltIn)
            return BadRequest(new { error = "Built-in roles cannot be modified" });

        if (request.Description != null) existing.Description = request.Description;
        if (request.Permissions != null)
        {
            if (request.Permissions.Count == 0)
                return BadRequest(new { error = "At least one permission is required" });
            existing.Permissions = request.Permissions;
        }

        existing.UpdatedBy = _actor.UserId;
        var updated = await _roleRepository.UpdateAsync(existing);
        _audit.Record("update role " + existing.RoleName, "*");
        return Ok(updated);
    }

    /// <summary>
    /// Delete a custom role (built-in roles cannot be deleted).
    /// Requires roles:manage permission.
    /// </summary>
    [HttpDelete("{roleName}")]
    [RequirePermission(TenantPermissions.RolesManage)]
    [RequirePermission(TenantPermissions.PlatformTenants)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteRole(string roleName)
    {
        var existing = await _roleRepository.GetByRoleNameAsync(roleName);
        if (existing == null)
            return NotFound(new { error = $"Role {roleName} not found" });

        if (existing.IsBuiltIn)
            return BadRequest(new { error = "Built-in roles cannot be deleted" });

        await _roleRepository.DeleteAsync(roleName);
        _audit.Record("delete role " + roleName, "*");
        return NoContent();
    }

    /// <summary>
    /// Re-seed standard roles (useful after upgrades). Requires roles:manage permission.
    /// </summary>
    [HttpPost("seed")]
    [RequirePermission(TenantPermissions.RolesManage)]
    [RequirePermission(TenantPermissions.PlatformTenants)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> SeedRoles()
    {
        await _roleRepository.SeedStandardRolesAsync();
        _audit.Record("seed roles", "*");
        return Ok(new { message = "Standard roles seeded successfully" });
    }
}
