using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using ReferenceDataService.Models;
using ReferenceDataService.Repositories;

namespace ReferenceDataService.Controllers;

/// <summary>
/// Serves tenant-level state compliance configuration (prompt pay deadlines,
/// PA timelines, FMMIS credentials, MPIP flags) consumed at runtime by
/// claims, authorization, appeals, encounter, and payment services.
/// </summary>
[ApiController]
[Route("api/compliance-config")]
[Produces("application/json")]
public class ComplianceConfigController : ControllerBase
{
    private readonly ILogger<ComplianceConfigController> _logger;
    private readonly IMemoryCache _cache;
    private readonly IComplianceConfigRepository _repository;
    private readonly IWebHostEnvironment _env;

    public ComplianceConfigController(
        IMemoryCache cache,
        IComplianceConfigRepository repository,
        IWebHostEnvironment env,
        ILogger<ComplianceConfigController> logger)
    {
        _cache = cache;
        _repository = repository;
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// Get the full compliance configuration document for a tenant.
    /// Includes state compliance parameters, FMMIS credentials, and MPIP flag.
    /// </summary>
    [HttpGet("{tenantId}")]
    [ProducesResponseType(typeof(TenantComplianceConfig), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantComplianceConfig>> GetConfig(string tenantId)
    {
        _logger.LogInformation("Fetching compliance config for tenant {TenantId}",
            SanitizeForLog(tenantId));

        var cacheKey = $"compliance:{tenantId}";
        var config = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await _repository.GetAsync(tenantId);
        });

        if (config is null)
        {
            return NotFound(new { message = $"No compliance config found for tenant {SanitizeForLog(tenantId)}" });
        }

        return Ok(config);
    }

    /// <summary>
    /// Get only the state compliance parameters for a tenant (prompt pay deadlines,
    /// PA timelines, appeal windows, encounter submission limits).
    /// </summary>
    [HttpGet("{tenantId}/state")]
    [ProducesResponseType(typeof(StateComplianceConfig), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StateComplianceConfig>> GetStateConfig(string tenantId)
    {
        _logger.LogInformation("Fetching state compliance config for tenant {TenantId}",
            SanitizeForLog(tenantId));

        var cacheKey = $"compliance:{tenantId}";
        var config = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await _repository.GetAsync(tenantId);
        });

        if (config is null)
        {
            return NotFound(new { message = $"No compliance config found for tenant {SanitizeForLog(tenantId)}" });
        }

        return Ok(config.StateConfig);
    }

    /// <summary>
    /// Create or update the compliance configuration for a tenant.
    /// Requires the AdminPolicy authorization policy.
    /// </summary>
    [HttpPut("{tenantId}")]
    [Authorize(Policy = "AdminPolicy")]
    [ProducesResponseType(typeof(TenantComplianceConfig), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TenantComplianceConfig>> UpsertConfig(
        string tenantId,
        [FromBody] TenantComplianceConfig config)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        _logger.LogInformation("Upserting compliance config for tenant {TenantId}, state {StateCode}",
            SanitizeForLog(tenantId), SanitizeForLog(config.StateCode));

        // Route tenantId takes precedence over body
        config.TenantId = tenantId;

        var existing = await _repository.GetAsync(tenantId);
        config.CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow;
        config.UpdatedAt = DateTime.UtcNow;

        var saved = await _repository.UpsertAsync(config);

        // Invalidate cache so next read picks up the new values
        _cache.Remove($"compliance:{tenantId}");

        return Ok(saved);
    }

    /// <summary>
    /// Development-only: seed compliance config without requiring AdminPolicy auth.
    /// Available only when ASPNETCORE_ENVIRONMENT is Development or Test.
    /// This endpoint exists to support E2E test environments where Azure AD is not configured.
    /// </summary>
    [HttpPost("{tenantId}/dev-seed")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [ProducesResponseType(typeof(TenantComplianceConfig), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TenantComplianceConfig>> DevSeedConfig(
        string tenantId,
        [FromBody] TenantComplianceConfig config)
    {
        if (!_env.IsDevelopment() && !string.Equals(_env.EnvironmentName, "Test", StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        _logger.LogInformation(
            "Dev-seed compliance config for tenant {TenantId} (env={Env})",
            SanitizeForLog(tenantId), _env.EnvironmentName);

        config.TenantId = tenantId;

        var existing = await _repository.GetAsync(tenantId);
        config.CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow;
        config.UpdatedAt = DateTime.UtcNow;

        var saved = await _repository.UpsertAsync(config);
        _cache.Remove($"compliance:{tenantId}");

        return Ok(saved);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
