using Microsoft.AspNetCore.Mvc;
using ProviderService.Services;

namespace ProviderService.Controllers;

/// <summary>
/// Admin-callable surface for the integrity-projection write-back path
/// (capability 5.4.5). Backfill iterates every Provider in a named
/// tenant and forces a verification refresh — used after this PR ships
/// to populate <c>IntegrityScore</c> on legacy rows.
///
/// <para>
/// **Auth posture.** This endpoint is not gated at the application
/// layer in this PR — provider-service does not yet have an admin-role
/// middleware. Operators must restrict access at the deployment layer
/// (NetworkPolicy, gateway ACL) until a platform-wide admin-auth
/// pattern lands. Documented in
/// <c>docs/architecture/verification-writeback.md</c> "Backfill activation".
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/admin/providers")]
[Produces("application/json")]
public sealed class IntegrityProjectionAdminController : ControllerBase
{
    private readonly IProviderIntegrityProjectionService _projection;
    private readonly ILogger<IntegrityProjectionAdminController> _logger;

    public IntegrityProjectionAdminController(
        IProviderIntegrityProjectionService projection,
        ILogger<IntegrityProjectionAdminController> logger)
    {
        _projection = projection;
        _logger = logger;
    }

    /// <summary>
    /// Force-refresh integrity projections for every provider in
    /// <paramref name="tenantId"/>. Idempotent — only patches rows
    /// where the projection is null or older than the configured
    /// refresh window. Re-running completes any gap from a previous
    /// partial run.
    /// </summary>
    [HttpPost("backfill-integrity-projection")]
    [ProducesResponseType(typeof(IntegrityProjectionTenantSweepResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IntegrityProjectionTenantSweepResult>> BackfillIntegrityProjection(
        [FromQuery] string tenantId,
        [FromQuery] int? maxProviders,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "tenantId query parameter is required" });
        }

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
                ActorId = "admin:backfill-integrity-projection",
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
