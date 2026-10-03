using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using TenantService.Models;
using TenantService.Security;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// A tenant's users. Every action needs <c>users:manage</c> in the route's
/// tenant (<see cref="RouteTenantFilter"/>; <c>platform:tenants</c> may act on
/// any tenant and is audited). token-service reads the roles stored here, so
/// granting <c>PlatformAdmin</c> or <c>cho.service</c> additionally needs
/// <c>platform:tenants</c>.
/// </summary>
[ApiController]
[Route("api/v1/tenants/{tenantId}/users")]
[RequirePermission(TenantPermissions.UsersManage)]
public class UsersController : ControllerBase
{
    private readonly ITenantUserService _userService;
    private readonly ICurrentActor _actor;
    private readonly ILogger<UsersController> _logger;

    public UsersController(ITenantUserService userService, ICurrentActor actor, ILogger<UsersController> logger)
    {
        _userService = userService;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>
    /// 403 when <paramref name="roles"/> adds a platform-only role the user does
    /// not already hold and the caller cannot grant it. Keeping or removing
    /// such a role is allowed, so an edit to an existing platform administrator
    /// still goes through.
    /// </summary>
    private async Task<ObjectResult?> PlatformRoleRefusedAsync(
        IEnumerable<string>? roles, string tenantId, string? existingUserId = null)
    {
        static bool PlatformOnly(string? r) => TenantPermissions.PlatformOnlyRoles.Contains(r?.Trim() ?? string.Empty);

        if (roles == null || !roles.Any(PlatformOnly) || _actor.HasPermission(TenantPermissions.PlatformTenants))
            return null;

        if (existingUserId != null)
        {
            var existing = await _userService.GetUserAsync(existingUserId);
            var held = existing != null && existing.TenantId == tenantId
                ? new HashSet<string>(existing.Roles ?? new List<string>(), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (roles.Where(PlatformOnly).All(r => held.Contains(r.Trim())))
                return null;
        }

        _logger.LogWarning("Refused to grant a platform-only role (caller {Subject})", TenantAuditLog.Sanitize(_actor.UserId));
        return StatusCode(StatusCodes.Status403Forbidden,
            new { error = "Granting PlatformAdmin or cho.service needs platform:tenants." });
    }

    /// <summary>
    /// Create a new user within a tenant. Requires users:manage permission.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(TenantUser), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantUser>> CreateUser(string tenantId, [FromBody] CreateTenantUserRequest request)
    {
        if (await PlatformRoleRefusedAsync(request.Roles, tenantId) is { } refused) return refused;
        try
        {
            var user = await _userService.CreateUserAsync(tenantId, request);
            return CreatedAtAction(nameof(GetUser), new { tenantId, userId = user.Id }, user);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get a specific user by ID
    /// </summary>
    [HttpGet("{userId}")]
    [ProducesResponseType(typeof(TenantUser), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantUser>> GetUser(string tenantId, string userId)
    {
        var user = await _userService.GetUserAsync(userId);
        if (user == null || user.TenantId != tenantId)
            return NotFound(new { error = $"User {userId} not found in tenant {tenantId}" });

        return Ok(user);
    }

    /// <summary>
    /// Get all users for a tenant, optionally filtered by role or department
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<TenantUser>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<TenantUser>>> GetUsers(
        string tenantId,
        [FromQuery] string? role = null,
        [FromQuery] string? department = null)
    {
        IEnumerable<TenantUser> users;

        if (!string.IsNullOrEmpty(role))
            users = await _userService.GetUsersByRoleAsync(tenantId, role);
        else if (!string.IsNullOrEmpty(department))
            users = await _userService.GetUsersByDepartmentAsync(tenantId, department);
        else
            users = await _userService.GetUsersByTenantAsync(tenantId);

        return Ok(users);
    }

    /// <summary>
    /// Update user details. Requires users:manage permission.
    /// </summary>
    [HttpPut("{userId}")]
    [ProducesResponseType(typeof(TenantUser), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TenantUser>> UpdateUser(
        string tenantId, string userId, [FromBody] UpdateTenantUserRequest request)
    {
        if (await PlatformRoleRefusedAsync(request.Roles, tenantId, userId) is { } refused) return refused;
        try
        {
            var user = await _userService.UpdateUserAsync(tenantId, userId, request);
            return Ok(user);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Partial update of user details (e.g. backfill Azure AD OID).
    /// </summary>
    [HttpPatch("{userId}")]
    [ProducesResponseType(typeof(TenantUser), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TenantUser>> PatchUser(
        string tenantId, string userId, [FromBody] UpdateTenantUserRequest request)
    {
        if (await PlatformRoleRefusedAsync(request.Roles, tenantId, userId) is { } refused) return refused;
        try
        {
            var user = await _userService.UpdateUserAsync(tenantId, userId, request);
            return Ok(user);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a user from a tenant. Requires users:manage permission.
    /// </summary>
    [HttpDelete("{userId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteUser(string tenantId, string userId)
    {
        try
        {
            await _userService.DeleteUserAsync(tenantId, userId);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get direct reports for a supervisor
    /// </summary>
    [HttpGet("{userId}/direct-reports")]
    [ProducesResponseType(typeof(IEnumerable<TenantUser>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<TenantUser>>> GetDirectReports(string tenantId, string userId)
    {
        var reports = await _userService.GetDirectReportsAsync(tenantId, userId);
        return Ok(reports);
    }

    /// <summary>
    /// Check if a user has a specific permission
    /// </summary>
    [HttpGet("{userId}/permissions/{permission}")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> CheckPermission(string tenantId, string userId, string permission)
    {
        var user = await _userService.GetUserAsync(userId);
        if (user == null || user.TenantId != tenantId)
            return NotFound(new { error = $"User {userId} not found in tenant {tenantId}" });

        var hasPermission = await _userService.HasPermissionAsync(userId, permission);
        return Ok(new { userId, permission, granted = hasPermission });
    }

    /// <summary>
    /// Record a user login event
    /// </summary>
    [HttpPost("{userId}/login")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RecordLogin(string tenantId, string userId)
    {
        try
        {
            var user = await _userService.GetUserAsync(userId);
            if (user == null || user.TenantId != tenantId)
                return NotFound(new { error = $"User {userId} not found in tenant {tenantId}" });

            await _userService.RecordLoginAsync(userId);
            return Ok(new { message = "Login recorded" });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Lookup user by Azure AD Object ID (for SSO authentication flow).
    /// Scoped to tenant to prevent cross-tenant data leakage.
    /// </summary>
    [HttpGet("by-oid/{azureAdObjectId}")]
    [ProducesResponseType(typeof(TenantUser), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantUser>> GetUserByAzureAdObjectId(string tenantId, string azureAdObjectId)
    {
        var user = await _userService.GetUserByAzureAdObjectIdAsync(azureAdObjectId);
        if (user == null || user.TenantId != tenantId)
            return NotFound(new { error = $"No user found with Azure AD Object ID {azureAdObjectId} in tenant {tenantId}" });

        return Ok(user);
    }
}
