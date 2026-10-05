using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ProviderService.Models;
using ProviderService.Security;
using ProviderService.Services;

namespace ProviderService.Controllers;

/// <summary>
/// Admin-callable surface for the integrity-projection write-back path
/// (capability 5.4.5). Backfill iterates every Provider in a named
/// tenant and forces a verification refresh — used after this PR ships
/// to populate <c>IntegrityScore</c> on legacy rows.
///
/// <para>
/// **Auth posture (defence in depth).** Two gates protect this route:
/// </para>
///
/// <list type="number">
///   <item>
///     <see cref="IntegrityProjectionOptions.AdminBackfillEnabled"/>
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
/// See <c>docs/architecture/verification-writeback.md</c>
/// "Backfill — admin HTTP endpoint" for the deployment-layer
/// requirement.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/admin/providers")]
[Produces("application/json")]
public sealed class IntegrityProjectionAdminController : ControllerBase
{
    private readonly IProviderIntegrityProjectionService _projection;
    private readonly IOptionsMonitor<IntegrityProjectionOptions> _options;
    private readonly ILogger<IntegrityProjectionAdminController> _logger;

    public IntegrityProjectionAdminController(
        IProviderIntegrityProjectionService projection,
        IOptionsMonitor<IntegrityProjectionOptions> options,
        ILogger<IntegrityProjectionAdminController> logger)
    {
        _projection = projection;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Forces an integrity-projection refresh for every Active
    /// provider in the specified tenant. All rows are treated as due,
    /// regardless of <see cref="Provider.NextVerificationDue"/>.
    /// Idempotent because last-write-wins on projection-metadata
    /// fields. Use to populate legacy null projections, recover from
    /// extended verification-service outages, or operator-driven
    /// data-quality refresh.
    /// </summary>
    [HttpPost("backfill-integrity-projection")]
    [RequirePermission("settings:manage")]
    [ProducesResponseType(typeof(IntegrityProjectionTenantSweepResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IntegrityProjectionTenantSweepResult>> BackfillIntegrityProjection(
        [FromQuery] string? tenantId,
        [FromQuery] int? maxProviders,
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
                        "Integrity-projection backfill is gated. Set " +
                        "IntegrityProjection:AdminBackfillEnabled=true to enable, " +
                        "and ensure the deployment layer (NetworkPolicy / gateway ACL) " +
                        "restricts access to this route.",
                });
        }

        // The tenant is the token's. A query tenant is only an echo of it.
        var tokenTenant = this.TokenTenantId();
        if (!string.IsNullOrEmpty(tenantId) && !string.Equals(tenantId, tokenTenant, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "integrity projection backfill refused: query tenant {QueryTenant} does not match the authenticated tenant {TokenTenant}",
                Sanitize(tenantId), Sanitize(tokenTenant));
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "The tenantId query parameter does not match the authenticated tenant." });
        }
        tenantId = tokenTenant;

        _logger.LogInformation(
            "integrity projection backfill triggered for tenant={Tenant} maxProviders={Max}",
            Sanitize(tenantId), maxProviders);

        var result = await _projection.RefreshTenantAsync(
            tenantId,
            new IntegrityProjectionTenantSweepRequest
            {
                DueBefore = DateTimeOffset.UtcNow.AddYears(100), // force "every row is due"
                IncludeNeverVerified = true,
                MaxProviders = maxProviders,
                ActorId = this.TokenActorId(),
            },
            ct);

        _logger.LogInformation(
            "integrity projection backfill complete: tenant={Tenant} inspected={Inspected} patched={Patched} skipped={Skipped} failed={Failed}",
            Sanitize(tenantId), result.Inspected, result.Patched,
            result.Skipped, result.Failed);

        return Ok(result);
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
