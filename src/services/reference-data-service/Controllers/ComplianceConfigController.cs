using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using ReferenceDataService.Models;

namespace ReferenceDataService.Controllers;

/// <summary>
/// Serves tenant-level compliance configuration (prompt pay deadlines,
/// PA timelines, FMMIS credentials, MPIP flags) to downstream services.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ComplianceConfigController : ControllerBase
{
    private readonly ILogger<ComplianceConfigController> _logger;
    private readonly IMemoryCache _cache;

    // In-memory store until a Cosmos DB repository is wired up.
    private static readonly List<TenantComplianceConfig> _configs = new();
    private static readonly object _lock = new();

    public ComplianceConfigController(
        IMemoryCache cache,
        ILogger<ComplianceConfigController> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Get compliance configuration for a tenant and state.
    /// </summary>
    [HttpGet("{tenantId}/{stateCode}")]
    [ProducesResponseType(typeof(TenantComplianceConfig), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<TenantComplianceConfig> GetConfig(string tenantId, string stateCode)
    {
        _logger.LogInformation("Fetching compliance config for tenant {TenantId}, state {StateCode}",
            SanitizeForLog(tenantId), SanitizeForLog(stateCode));

        var cacheKey = $"compliance:{tenantId}:{stateCode}";
        var config = _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            lock (_lock)
            {
                return _configs.FirstOrDefault(c =>
                    c.TenantId == tenantId &&
                    c.StateCode.Equals(stateCode, StringComparison.OrdinalIgnoreCase));
            }
        });

        if (config is null)
        {
            return NotFound($"No compliance config found for tenant {SanitizeForLog(tenantId)} in state {SanitizeForLog(stateCode)}");
        }

        return Ok(config);
    }

    /// <summary>
    /// List all compliance configurations for a tenant.
    /// </summary>
    [HttpGet("{tenantId}")]
    [ProducesResponseType(typeof(IEnumerable<TenantComplianceConfig>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<TenantComplianceConfig>> GetConfigsByTenant(string tenantId)
    {
        _logger.LogInformation("Listing compliance configs for tenant {TenantId}", SanitizeForLog(tenantId));

        List<TenantComplianceConfig> results;
        lock (_lock)
        {
            results = _configs.Where(c => c.TenantId == tenantId).ToList();
        }

        return Ok(results);
    }

    /// <summary>
    /// Create or update compliance configuration for a tenant/state pair.
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(TenantComplianceConfig), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<TenantComplianceConfig> UpsertConfig([FromBody] TenantComplianceConfig config)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        _logger.LogInformation("Upserting compliance config for tenant {TenantId}, state {StateCode}",
            SanitizeForLog(config.TenantId), SanitizeForLog(config.StateCode));

        lock (_lock)
        {
            var existing = _configs.FirstOrDefault(c =>
                c.TenantId == config.TenantId &&
                c.StateCode.Equals(config.StateCode, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
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
                _configs.Add(config);
            }
        }

        // Invalidate cache for this tenant/state pair
        _cache.Remove($"compliance:{config.TenantId}:{config.StateCode}");

        return Ok(config);
    }

    /// <summary>
    /// Get the Florida default compliance configuration (convenience endpoint).
    /// Returns a <see cref="StateComplianceConfig"/> with AHCA / SMMC 3.0 defaults.
    /// </summary>
    [HttpGet("defaults/FL")]
    [ProducesResponseType(typeof(StateComplianceConfig), StatusCodes.Status200OK)]
    public ActionResult<StateComplianceConfig> GetFloridaDefaults()
    {
        _logger.LogInformation("Returning FL AHCA default compliance parameters");
        return Ok(StateComplianceConfig.Florida());
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
