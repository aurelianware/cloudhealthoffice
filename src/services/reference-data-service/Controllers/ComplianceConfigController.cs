using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using ReferenceDataService.Models;

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

    // In-memory store until a Cosmos DB repository is wired up.
    // Key: tenantId (lowercase)
    private static readonly Dictionary<string, TenantComplianceConfig> _store = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    public ComplianceConfigController(
        IMemoryCache cache,
        ILogger<ComplianceConfigController> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Get the full compliance configuration document for a tenant.
    /// Includes state compliance parameters, FMMIS credentials, and MPIP flag.
    /// </summary>
    [HttpGet("{tenantId}")]
    [ProducesResponseType(typeof(TenantComplianceConfig), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<TenantComplianceConfig> GetConfig(string tenantId)
    {
        _logger.LogInformation("Fetching compliance config for tenant {TenantId}",
            SanitizeForLog(tenantId));

        var cacheKey = $"compliance:{tenantId}";
        var config = _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            lock (_lock)
            {
                _store.TryGetValue(tenantId, out var found);
                return found;
            }
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
    public ActionResult<StateComplianceConfig> GetStateConfig(string tenantId)
    {
        _logger.LogInformation("Fetching state compliance config for tenant {TenantId}",
            SanitizeForLog(tenantId));

        var cacheKey = $"compliance:{tenantId}";
        var config = _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            lock (_lock)
            {
                _store.TryGetValue(tenantId, out var found);
                return found;
            }
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
    // Auth: see docs/decisions/adr-031-compliance-config-auth.md
    [HttpPut("{tenantId}")]
    [ProducesResponseType(typeof(TenantComplianceConfig), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<TenantComplianceConfig> UpsertConfig(
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

        lock (_lock)
        {
            if (_store.TryGetValue(tenantId, out var existing))
            {
                existing.StateCode = config.StateCode;
                existing.StateConfig = config.StateConfig;
                existing.FmmisSubmitterId = config.FmmisSubmitterId;
                existing.FmmisInterchangeSenderId = config.FmmisInterchangeSenderId;
                existing.MpipEnabled = config.MpipEnabled;
                existing.UpdatedAt = DateTime.UtcNow;
                config = existing;
            }
            else
            {
                config.Id = Guid.NewGuid().ToString();
                config.CreatedAt = DateTime.UtcNow;
                config.UpdatedAt = DateTime.UtcNow;
                _store[tenantId] = config;
            }
        }

        // Invalidate cache so next read picks up the new values
        _cache.Remove($"compliance:{tenantId}");

        return Ok(config);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
