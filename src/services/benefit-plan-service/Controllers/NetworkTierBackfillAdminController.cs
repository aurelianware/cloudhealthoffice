using BenefitPlanService.Middleware;
using BenefitPlanService.Models;
using BenefitPlanService.Services;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Controllers;

/// <summary>
/// Admin endpoint for the one-shot, operator-triggered backfill that
/// populates <see cref="NetworkTier.NetworkId"/> on existing benefit
/// plans (capability 5.5 — NetworkTier as Reference to Organization).
///
/// <para>
/// Authorization is layered: the deployment layer (NetworkPolicy /
/// gateway ACL) is the load-bearing control, and the
/// <see cref="NetworkTierBackfillOptions.AdminBackfillEnabled"/> flag
/// is a defence-in-depth tripwire. When the flag is false the
/// controller returns 503 Service Unavailable; a misconfigured
/// gateway can't expose the route just because it's registered.
/// </para>
///
/// <para>
/// Operations are per-tenant; a multi-tenant rollout is scripted
/// externally as one call per tenant. Reruns are safe — the service
/// only writes a <c>NetworkId</c> on tiers where it is currently
/// null (skipped on reruns).
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/admin/benefit-plans")]
public sealed class NetworkTierBackfillAdminController : ControllerBase
{
    private readonly INetworkTierBackfillService _backfill;
    private readonly IOptionsMonitor<NetworkTierBackfillOptions> _options;
    private readonly ICurrentActor _actor;
    private readonly ILogger<NetworkTierBackfillAdminController> _logger;

    public NetworkTierBackfillAdminController(
        INetworkTierBackfillService backfill,
        IOptionsMonitor<NetworkTierBackfillOptions> options,
        ICurrentActor actor,
        ILogger<NetworkTierBackfillAdminController> logger)
    {
        _backfill = backfill;
        _options = options;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>
    /// Apply operator-supplied <c>(planId, tierName) → networkId</c>
    /// mappings to the caller's tenant (from the token). The optional
    /// <c>tenantId</c> query parameter is accepted only as an echo of the
    /// token tenant; it never selects the tenant. The endpoint is idempotent:
    /// reruns skip tiers that already have a <c>NetworkId</c> and
    /// only patch newly-mapped pairs.
    /// </summary>
    [HttpPost("backfill-network-tiers")]
    [ProducesResponseType(typeof(NetworkTierBackfillResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<NetworkTierBackfillResult>> BackfillNetworkTiers(
        [FromQuery] string? tenantId,
        [FromBody] NetworkTierBackfillRequest request,
        CancellationToken ct)
    {
        if (!_options.CurrentValue.AdminBackfillEnabled)
        {
            // 503 (not 404) so operators know the endpoint exists and
            // is intentionally gated — a 404 would falsely suggest the
            // route was never registered.
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    error = "admin_backfill_disabled",
                    message =
                        "Network-tier NetworkId backfill is gated. Set " +
                        "NetworkTierBackfill:AdminBackfillEnabled=true to enable, " +
                        "and ensure the deployment layer (NetworkPolicy / gateway ACL) " +
                        "restricts access to this route.",
                });
        }

        var contextTenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(contextTenantId))
        {
            return BadRequest(new { error = "tenant_required", message = "Tenant context is required." });
        }

        if (!string.IsNullOrWhiteSpace(tenantId)
            && !string.Equals(contextTenantId, tenantId, StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                error = "tenant_mismatch",
                message = "tenantId query parameter does not match the tenant in the access token.",
            });
        }

        if (request is null)
        {
            return BadRequest(new { error = "request_body_required" });
        }

        var max = _options.CurrentValue.MaxMappingsPerCall;
        if (max > 0 && request.Mappings.Count > max)
        {
            return BadRequest(new
            {
                error = "too_many_mappings",
                message = $"Mappings count {request.Mappings.Count} exceeds MaxMappingsPerCall={max}.",
            });
        }

        // The operator is the authenticated caller; a body actorId is ignored.
        request.ActorId = _actor.UserId;
        request.CorrelationId ??= HttpContext.TraceIdentifier;

        _logger.LogInformation(
            "network-tier backfill triggered tenant={Tenant} mappings={Mappings} actor={Actor}",
            Sanitize(contextTenantId), request.Mappings.Count, Sanitize(request.ActorId));

        var result = await _backfill.RunTenantAsync(contextTenantId, request, ct);
        return Ok(result);
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
