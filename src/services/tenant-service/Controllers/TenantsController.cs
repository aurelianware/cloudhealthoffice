using CloudHealthOffice.OperatingMode;
using Microsoft.AspNetCore.Mvc;
using TenantService.Models;
using TenantService.Services;

namespace TenantService.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class TenantsController : ControllerBase
{
    private readonly ITenantService _tenantService;
    private readonly ILogger<TenantsController> _logger;

    public TenantsController(ITenantService tenantService, ILogger<TenantsController> logger)
    {
        _tenantService = tenantService;
        _logger = logger;
    }

    /// <summary>
    /// Create a new tenant (payer/health plan)
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Tenant), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Tenant>> CreateTenant([FromBody] CreateTenantRequest request)
    {
        try
        {
            var tenant = await _tenantService.CreateTenantAsync(request);
            return CreatedAtAction(nameof(GetTenant), new { tenantId = tenant.TenantId }, tenant);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get tenant by ID
    /// </summary>
    [HttpGet("{tenantId}")]
    [ProducesResponseType(typeof(Tenant), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Tenant>> GetTenant(string tenantId)
    {
        var tenant = await _tenantService.GetTenantAsync(tenantId);
        if (tenant == null)
        {
            return NotFound(new { error = $"Tenant {tenantId} not found" });
        }

        return Ok(tenant);
    }

    /// <summary>
    /// Get all tenants
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<Tenant>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Tenant>>> GetAllTenants()
    {
        var tenants = await _tenantService.GetAllTenantsAsync();
        return Ok(tenants);
    }

    /// <summary>
    /// Update tenant
    /// </summary>
    [HttpPut("{tenantId}")]
    [ProducesResponseType(typeof(Tenant), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Tenant>> UpdateTenant(string tenantId, [FromBody] UpdateTenantRequest request)
    {
        try
        {
            var tenant = await _tenantService.UpdateTenantAsync(tenantId, request);
            return Ok(tenant);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Activate tenant (move from pending to active)
    /// </summary>
    [HttpPost("{tenantId}/activate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateTenant(string tenantId)
    {
        try
        {
            await _tenantService.ActivateTenantAsync(tenantId);
            return Ok(new { message = $"Tenant {tenantId} activated" });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Suspend tenant (e.g., for non-payment)
    /// </summary>
    [HttpPost("{tenantId}/suspend")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SuspendTenant(string tenantId)
    {
        try
        {
            await _tenantService.SuspendTenantAsync(tenantId);
            return Ok(new { message = $"Tenant {tenantId} suspended" });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete tenant (soft delete recommended, use suspend instead)
    /// </summary>
    [HttpDelete("{tenantId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteTenant(string tenantId)
    {
        await _tenantService.DeleteTenantAsync(tenantId);
        return NoContent();
    }

    /// <summary>
    /// Create API key for tenant
    /// </summary>
    [HttpPost("{tenantId}/api-keys")]
    [ProducesResponseType(typeof(ApiKeyResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiKeyResponse>> CreateApiKey(string tenantId, [FromBody] CreateApiKeyRequest request)
    {
        try
        {
            var apiKey = await _tenantService.CreateApiKeyAsync(tenantId, request);
            return CreatedAtAction(nameof(GetApiKeys), new { tenantId }, apiKey);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get all API keys for tenant
    /// </summary>
    [HttpGet("{tenantId}/api-keys")]
    [ProducesResponseType(typeof(IEnumerable<ApiKey>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<ApiKey>>> GetApiKeys(string tenantId)
    {
        try
        {
            var keys = await _tenantService.GetApiKeysAsync(tenantId);
            return Ok(keys);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Revoke API key
    /// </summary>
    [HttpDelete("{tenantId}/api-keys/{keyId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeApiKey(string tenantId, string keyId)
    {
        try
        {
            await _tenantService.RevokeApiKeyAsync(tenantId, keyId);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get operating mode configuration for tenant
    /// </summary>
    [HttpGet("{tenantId}/operating-mode")]
    [ProducesResponseType(typeof(OperatingModeConfiguration), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OperatingModeConfiguration>> GetOperatingMode(string tenantId)
    {
        try
        {
            var operatingMode = await _tenantService.GetOperatingModeAsync(tenantId);
            return Ok(operatingMode);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update operating mode configuration for tenant.
    /// Allows setting individual engines to "augment" or "replace" mode.
    /// </summary>
    [HttpPut("{tenantId}/operating-mode")]
    [ProducesResponseType(typeof(OperatingModeConfiguration), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OperatingModeConfiguration>> UpdateOperatingMode(
        string tenantId, [FromBody] UpdateOperatingModeRequest request)
    {
        try
        {
            var operatingMode = await _tenantService.UpdateOperatingModeAsync(tenantId, request);
            return Ok(operatingMode);
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
    /// Get usage metrics for tenant
    /// </summary>
    [HttpGet("{tenantId}/usage")]
    [ProducesResponseType(typeof(UsageMetrics), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UsageMetrics>> GetUsage(string tenantId)
    {
        try
        {
            var usage = await _tenantService.GetUsageAsync(tenantId);
            return Ok(usage);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
