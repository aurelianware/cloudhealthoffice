using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ProviderService.Models;
using ProviderService.Security;
using ProviderService.Services;

namespace ProviderService.Controllers;

/// <summary>
/// Admin-callable surface for the network-participation panel-gating
/// backfill (capability 5.5). One-shot, tenant-scoped: each call
/// iterates the head-Active providers in the named tenant and patches
/// participations whose panel-gating fields are at type defaults.
///
/// <para>
/// **Auth posture (defence in depth).** Two gates protect this route,
/// mirroring <see cref="IntegrityProjectionAdminController"/>:
/// </para>
///
/// <list type="number">
///   <item>
///     <see cref="NetworkParticipationBackfillOptions.AdminBackfillEnabled"/>
///     defaults to <c>false</c>. The controller returns
///     <see cref="StatusCodes.Status503ServiceUnavailable"/> until an
///     operator explicitly opts in via configuration.
///   </item>
///   <item>
///     The caller needs a CHO token holding <c>settings:manage</c>
///     (TenantAdmin). The backfill runs in the token's tenant only; the
///     optional <c>tenantId</c> query parameter is accepted as an echo of
///     that tenant and a different value is refused with 403. To cover
///     several tenants an operator runs it once per tenant token.
///   </item>
/// </list>
///
/// <para>
/// See <c>docs/architecture/network-participation-backfill.md</c>
/// "Backfill — admin HTTP endpoint" for the deployment-layer
/// requirement.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/admin/providers")]
[Produces("application/json")]
public sealed class NetworkParticipationBackfillAdminController : ControllerBase
{
    private readonly INetworkParticipationBackfillService _backfill;
    private readonly IOptionsMonitor<NetworkParticipationBackfillOptions> _options;
    private readonly ILogger<NetworkParticipationBackfillAdminController> _logger;

    public NetworkParticipationBackfillAdminController(
        INetworkParticipationBackfillService backfill,
        IOptionsMonitor<NetworkParticipationBackfillOptions> options,
        ILogger<NetworkParticipationBackfillAdminController> logger)
    {
        _backfill = backfill;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Backfills <see cref="NetworkParticipation"/> panel-gating fields
    /// (<c>PanelLimit</c>, <c>PanelAccepted</c>, <c>AcceptedLobs</c>,
    /// <c>MinAcceptedAgeYears</c>, <c>MaxAcceptedAgeYears</c>) on every
    /// participation in the specified tenant whose fields are at type
    /// defaults — i.e. has not been touched by panel-gating-aware code
    /// yet. **Reruns are safe but not skip-based idempotent**: the
    /// patch writes the panel-gating fields to their type defaults, so
    /// a patched row remains eligible until some panel-gating-aware
    /// write surface populates real values. A rerun therefore
    /// re-applies the same defaults (value-preserving) and emits a
    /// fresh `PanelGatingBackfilled` event under a new
    /// `backfillRunId`. See
    /// `docs/architecture/network-participation-backfill.md` "Rerun
    /// behavior". The operation is one-shot per call; operators script
    /// across tenant ids externally for multi-tenant coverage.
    /// </summary>
    [HttpPost("backfill-network-participations")]
    [RequirePermission("settings:manage")]
    [ProducesResponseType(typeof(NetworkParticipationBackfillResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<NetworkParticipationBackfillResult>> BackfillNetworkParticipations(
        [FromQuery] string? tenantId,
        [FromQuery] int? maxProviders,
        [FromQuery] int? pageSize,
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
                        "Network-participation panel-gating backfill is gated. Set " +
                        "NetworkParticipationBackfill:AdminBackfillEnabled=true to enable, " +
                        "and ensure the deployment layer (NetworkPolicy / gateway ACL) " +
                        "restricts access to this route.",
                });
        }

        // The tenant is the token's. A query tenant is only an echo of it.
        var tokenTenant = this.TokenTenantId();
        if (!string.IsNullOrEmpty(tenantId) && !string.Equals(tenantId, tokenTenant, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "panel-gating backfill refused: query tenant {QueryTenant} does not match the authenticated tenant {TokenTenant}",
                Sanitize(tenantId), Sanitize(tokenTenant));
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "The tenantId query parameter does not match the authenticated tenant." });
        }
        tenantId = tokenTenant;

        _logger.LogInformation(
            "panel-gating backfill triggered tenant={Tenant} maxProviders={Max} pageSize={PageSize}",
            Sanitize(tenantId), maxProviders, pageSize);

        var actorId = this.TokenActorId();
        var correlationId = HttpContext.TraceIdentifier;

        var result = await _backfill.RunTenantAsync(
            tenantId,
            new NetworkParticipationBackfillRequest
            {
                MaxProviders = maxProviders,
                PageSize = pageSize,
                ActorId = actorId,
                CorrelationId = correlationId,
            },
            ct);

        _logger.LogInformation(
            "panel-gating backfill complete tenant={Tenant} runId={RunId} providersInspected={ProvIn} participationsInspected={PartIn} backfilled={Bf} skipped={Sk} failed={Fa} etag={Etag}",
            Sanitize(tenantId), result.BackfillRunId,
            result.ProvidersInspected, result.ParticipationsInspected,
            result.ParticipationsBackfilled, result.ParticipationsSkipped,
            result.ParticipationsFailed, result.EtagConflicts);

        return Ok(result);
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
