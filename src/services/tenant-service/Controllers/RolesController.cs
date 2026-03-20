using Microsoft.AspNetCore.Mvc;
using TenantService.Models;
using TenantService.Services;

namespace TenantService.Controllers;

[ApiController]
[Route("api/v1/roles")]
public class RolesController : ControllerBase
{
    private readonly ITenantRoleRepository _roleRepository;
    private readonly ILogger<RolesController> _logger;

    public RolesController(ITenantRoleRepository roleRepository, ILogger<RolesController> logger)
    {
        _roleRepository = roleRepository;
        _logger = logger;
    }

    /// <summary>
    /// Get all available roles and their permissions
    /// </summary>
    [HttpGet]
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
    /// Create a custom role (non-built-in)
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(TenantRole), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TenantRole>> CreateRole([FromBody] TenantRole role)
    {
        var existing = await _roleRepository.GetByRoleNameAsync(role.RoleName);
        if (existing != null)
            return BadRequest(new { error = $"Role {role.RoleName} already exists" });

        role.IsBuiltIn = false; // Custom roles are never built-in
        var created = await _roleRepository.CreateAsync(role);
        return CreatedAtAction(nameof(GetRole), new { roleName = created.RoleName }, created);
    }

    /// <summary>
    /// Update a custom role's permissions (built-in roles cannot be modified)
    /// </summary>
    [HttpPut("{roleName}")]
    [ProducesResponseType(typeof(TenantRole), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantRole>> UpdateRole(string roleName, [FromBody] TenantRole role)
    {
        var existing = await _roleRepository.GetByRoleNameAsync(roleName);
        if (existing == null)
            return NotFound(new { error = $"Role {roleName} not found" });

        if (existing.IsBuiltIn)
            return BadRequest(new { error = "Built-in roles cannot be modified" });

        existing.Description = role.Description;
        existing.Permissions = role.Permissions;

        var updated = await _roleRepository.UpdateAsync(existing);
        return Ok(updated);
    }

    /// <summary>
    /// Delete a custom role (built-in roles cannot be deleted)
    /// </summary>
    [HttpDelete("{roleName}")]
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
        return NoContent();
    }

    /// <summary>
    /// Re-seed standard roles (useful after upgrades)
    /// </summary>
    [HttpPost("seed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> SeedRoles()
    {
        await _roleRepository.SeedStandardRolesAsync();
        return Ok(new { message = "Standard roles seeded successfully" });
    }
}
