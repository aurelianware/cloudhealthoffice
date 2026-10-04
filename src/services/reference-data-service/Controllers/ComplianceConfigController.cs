using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using ReferenceDataService.Models;
using ReferenceDataService.Repositories;

namespace ReferenceDataService.Controllers;

/// <summary>
/// Serves tenant-level state compliance configuration (prompt pay deadlines,
/// PA timelines, FMMIS credentials, MPIP flags) consumed at runtime by
/// claims, authorization, appeals, encounter, and payment services.
///
/// Compliance config is per-tenant data. The tenant comes from the validated CHO
/// token. The <c>{tenantId}</c> route segment is kept for existing callers
/// (claims-service, encounter-submission-service) but never selects a tenant: a
/// path tenant that differs from the token's tenant is refused with 403.
/// Permissions: GET needs reference-data:read (service tokens satisfy it), PUT and
/// dev-seed need settings:manage (defaults set in Program.cs). The acting user
/// recorded on a write is the token subject.
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
    private readonly ICurrentActor _actor;

    public ComplianceConfigController(
        IMemoryCache cache,
        IComplianceConfigRepository repository,
        IWebHostEnvironment env,
        ICurrentActor actor,
        ILogger<ComplianceConfigController> logger)
    {
        _cache = cache;
        _repository = repository;
        _env = env;
        _actor = actor;
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
        if (PathTenantMismatch(tenantId) is { } refused) return refused;
        tenantId = _actor.TenantId;
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
        if (PathTenantMismatch(tenantId) is { } refused) return refused;
        tenantId = _actor.TenantId;
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
    /// Requires settings:manage (the default write permission).
    /// </summary>
    [HttpPut("{tenantId}")]
    [ProducesResponseType(typeof(TenantComplianceConfig), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TenantComplianceConfig>> UpsertConfig(
        string tenantId,
        [FromBody] TenantComplianceConfig config)
    {
        if (PathTenantMismatch(tenantId) is { } refused) return refused;
        tenantId = _actor.TenantId;

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        _logger.LogInformation("Upserting compliance config for tenant {TenantId}, state {StateCode}",
            SanitizeForLog(tenantId), SanitizeForLog(config.StateCode));

        var saved = await SaveAsync(tenantId, config);

        // Invalidate cache so next read picks up the new values
        _cache.Remove($"compliance:{tenantId}");

        return Ok(saved);
    }

    /// <summary>
    /// Development-only: seed compliance config for E2E test environments.
    /// Available only when ASPNETCORE_ENVIRONMENT is Development or Test, and like
    /// every write it needs a CHO token with settings:manage for the path tenant.
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
        if (PathTenantMismatch(tenantId) is { } refused) return refused;
        tenantId = _actor.TenantId;

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

        var saved = await SaveAsync(tenantId, config);
        _cache.Remove($"compliance:{tenantId}");

        return Ok(saved);
    }

    /// <summary>
    /// Stores the config under the token tenant. Creation and update stamps,
    /// and the acting user, come from the stored document and the token; the
    /// body's values for them are ignored.
    /// </summary>
    private async Task<TenantComplianceConfig> SaveAsync(string tenantId, TenantComplianceConfig config)
    {
        config.TenantId = tenantId;

        var existing = await _repository.GetAsync(tenantId);
        var now = DateTime.UtcNow;
        config.CreatedAt = existing?.CreatedAt ?? now;
        config.CreatedBy = existing is null ? _actor.UserId : existing.CreatedBy;
        config.UpdatedAt = now;
        config.UpdatedBy = _actor.UserId;

        return await _repository.UpsertAsync(config);
    }

    /// <summary>
    /// The route tenant is only accepted as an echo of the token's tenant.
    /// </summary>
    private ObjectResult? PathTenantMismatch(string pathTenantId)
    {
        if (string.Equals(pathTenantId, _actor.TenantId, StringComparison.Ordinal))
            return null;

        _logger.LogWarning(
            "Compliance config request refused: path tenant {PathTenant} does not match the authenticated tenant {TokenTenant} (subject {Subject})",
            SanitizeForLog(pathTenantId), SanitizeForLog(_actor.TenantId), SanitizeForLog(_actor.UserId));
        return StatusCode(StatusCodes.Status403Forbidden,
            new { message = "The tenant in the path does not match the authenticated tenant." });
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
