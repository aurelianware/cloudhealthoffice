using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.OperatingMode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TenantService.Models;
using TenantService.Security;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Tenant records. Routes with <c>{tenantId}</c> act only on the caller's own
/// tenant unless the caller holds <c>platform:tenants</c>
/// (<see cref="RouteTenantFilter"/>). Creating, listing, activating,
/// suspending and deleting tenants, and changing a tenant's status or tier,
/// are platform actions.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class TenantsController : ControllerBase
{
    private readonly ITenantService _tenantService;
    private readonly ICurrentActor _actor;
    private readonly TenantAuditLog _audit;
    private readonly ILogger<TenantsController> _logger;

    public TenantsController(
        ITenantService tenantService, ICurrentActor actor, TenantAuditLog audit, ILogger<TenantsController> logger)
    {
        _tenantService = tenantService;
        _actor = actor;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// Create a new tenant (payer/health plan). Platform administrators only.
    /// </summary>
    [HttpPost]
    [RequirePermission(TenantPermissions.PlatformTenants)]
    [ProducesResponseType(typeof(Tenant), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Tenant>> CreateTenant([FromBody] CreateTenantRequest request)
    {
        try
        {
            var tenant = await _tenantService.CreateTenantAsync(request);
            _audit.Record("create tenant", tenant.TenantId);
            return CreatedAtAction(nameof(GetTenant), new { tenantId = tenant.TenantId }, tenant);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get tenant by ID. Any user or service of the tenant may read it (services
    /// read their adapter configuration and capitation reads
    /// <c>configuration.paymentControls</c> with the user's own token). API key
    /// records and Stripe billing ids are returned only to callers holding
    /// <c>settings:manage</c>.
    /// </summary>
    [HttpGet("{tenantId}")]
    [Authorize(Policy = TenantPermissions.MemberPolicy)]
    [ProducesResponseType(typeof(Tenant), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Tenant>> GetTenant(string tenantId)
    {
        var tenant = await _tenantService.GetTenantAsync(tenantId);
        if (tenant == null)
        {
            return NotFound(new { error = $"Tenant {tenantId} not found" });
        }

        return Ok(_actor.HasPermission(TenantPermissions.SettingsManage) ? tenant : WithoutSecrets(tenant));
    }

    /// <summary>
    /// The tenant record as a reader without <c>settings:manage</c> sees it:
    /// no API key records and no billing (Stripe customer/subscription ids).
    /// </summary>
    internal static Tenant WithoutSecrets(Tenant t) => new()
    {
        Id = t.Id,
        TenantId = t.TenantId,
        TenantName = t.TenantName,
        OrganizationName = t.OrganizationName,
        SubscriptionTier = t.SubscriptionTier,
        Status = t.Status,
        ContactInfo = t.ContactInfo,
        ApiKeys = new List<ApiKey>(),
        Configuration = WithoutSecretNames(t.Configuration),
        Billing = null,
        OperatingMode = t.OperatingMode,
        Usage = t.Usage,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt,
        CreatedBy = t.CreatedBy,
        UpdatedBy = t.UpdatedBy,
        ActivatedAt = t.ActivatedAt,
        LastActivityAt = t.LastActivityAt,
    };

    /// <summary>
    /// The configuration with the NACHA transmission Key Vault secret names
    /// replaced by "configured" flags. (No credential value is ever stored.)
    /// </summary>
    internal static TenantConfiguration WithoutSecretNames(TenantConfiguration configuration)
    {
        if (configuration.PaymentControls?.NachaTransmission is not { } nacha)
            return configuration;
        var copy = System.Text.Json.JsonSerializer.Deserialize<TenantConfiguration>(
            System.Text.Json.JsonSerializer.Serialize(configuration))!;
        copy.PaymentControls.NachaTransmission = nacha.WithoutSecretNames();
        return copy;
    }

    /// <summary>
    /// Get all tenants. Platform administrators only.
    /// </summary>
    [HttpGet]
    [RequirePermission(TenantPermissions.PlatformTenants)]
    [ProducesResponseType(typeof(IEnumerable<Tenant>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Tenant>>> GetAllTenants()
    {
        var tenants = await _tenantService.GetAllTenantsAsync();
        _audit.Record("list tenants", "*");
        return Ok(tenants);
    }

    /// <summary>
    /// Update tenant settings (names, contact, configuration including
    /// <c>paymentControls</c>). Needs <c>settings:manage</c>. Status,
    /// subscription tier and <c>paymentControls.nachaTransmission</c> are
    /// platform decisions: changing any of them needs <c>platform:tenants</c>.
    /// </summary>
    [HttpPut("{tenantId}")]
    [RequirePermission(TenantPermissions.SettingsManage)]
    [ProducesResponseType(typeof(Tenant), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Tenant>> UpdateTenant(string tenantId, [FromBody] UpdateTenantRequest request)
    {
        var changesPlatformFields = !string.IsNullOrEmpty(request.Status) || !string.IsNullOrEmpty(request.SubscriptionTier);
        if (changesPlatformFields && !_actor.HasPermission(TenantPermissions.PlatformTenants))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Changing a tenant's status or subscription tier needs platform:tenants." });
        }

        // NACHA transmission decides where payment files go (bank SFTP host,
        // pinned host key) and which Key Vault credentials are sent there, so
        // changing it needs platform:tenants, not settings:manage alone. A
        // configuration write that omits it keeps the stored settings (it no
        // longer silently clears them); resending them unchanged is allowed.
        var changesNacha = false;
        if (request.Configuration != null)
        {
            var stored = (await _tenantService.GetTenantAsync(tenantId))?.Configuration?.PaymentControls?.NachaTransmission;
            request.Configuration.PaymentControls ??= new PaymentControlsConfig();
            if (request.Configuration.PaymentControls.NachaTransmission is not { } requested)
            {
                request.Configuration.PaymentControls.NachaTransmission = stored;
            }
            else
            {
                changesNacha = !NachaTransmissionConfig.SameSettings(stored, requested);
                if (changesNacha && !_actor.HasPermission(TenantPermissions.PlatformTenants))
                {
                    _audit.Record("refused NACHA transmission change (needs platform:tenants)", tenantId, "denied");
                    return StatusCode(StatusCodes.Status403Forbidden,
                        new { error = "Changing paymentControls.nachaTransmission needs platform:tenants." });
                }

                // NACHA transmission settings name Key Vault secrets of this tenant only,
                // pin the bank's host key, and never carry a credential.
                if (requested.Validate(tenantId) is { } problem)
                    return BadRequest(new { error = problem });
                requested.UnknownProperties = null;
            }
        }

        try
        {
            var tenant = await _tenantService.UpdateTenantAsync(tenantId, request);
            if (changesNacha)
            {
                var n = request.Configuration!.PaymentControls.NachaTransmission!;
                _audit.Record($"update NACHA transmission settings (enabled={n.Enabled}, host={n.Host}:{n.Port}, " +
                              $"hostKey={n.HostKeyFingerprint}, dir={n.RemoteDirectory})", tenantId);
            }
            if (changesPlatformFields)
                _audit.Record($"update tenant status/tier (status={request.Status}, tier={request.SubscriptionTier})", tenantId);
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
    [RequirePermission(TenantPermissions.PlatformTenants)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateTenant(string tenantId)
    {
        try
        {
            await _tenantService.ActivateTenantAsync(tenantId);
            _audit.Record("activate tenant", tenantId);
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
    [RequirePermission(TenantPermissions.PlatformTenants)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SuspendTenant(string tenantId)
    {
        try
        {
            await _tenantService.SuspendTenantAsync(tenantId);
            _audit.Record("suspend tenant", tenantId);
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
    [RequirePermission(TenantPermissions.PlatformTenants)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteTenant(string tenantId)
    {
        await _tenantService.DeleteTenantAsync(tenantId);
        _audit.Record("delete tenant", tenantId);
        return NoContent();
    }

    /// <summary>
    /// Create API key for tenant
    /// </summary>
    [HttpPost("{tenantId}/api-keys")]
    [RequirePermission(TenantPermissions.SettingsManage)]
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
    [RequirePermission(TenantPermissions.SettingsManage)]
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
    [RequirePermission(TenantPermissions.SettingsManage)]
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
    [Authorize(Policy = TenantPermissions.MemberPolicy)]
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
    [RequirePermission(TenantPermissions.OperatingModeManage)]
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
    [Authorize(Policy = TenantPermissions.MemberPolicy)]
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
