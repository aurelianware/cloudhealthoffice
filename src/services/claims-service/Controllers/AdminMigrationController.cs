using ClaimsService.Models.Migrations;
using ClaimsService.Services.Migrations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ClaimsService.Controllers;

/// <summary>
/// Admin endpoint for the operator-triggered Cosmos partition-key
/// migration that copies claim documents from the legacy
/// <c>Claims</c> container (<c>/memberId</c> Bicep / <c>/Id</c>
/// runtime) to the canonical <c>ClaimsV2</c> container
/// (<c>/tenantId</c>) — capability 5.1b.
///
/// <para>
/// Authorization is layered: the deployment layer (NetworkPolicy /
/// gateway ACL) is the load-bearing control, and the
/// <see cref="ClaimMigrationOptions.MigrationsEnabled"/> flag is a
/// defence-in-depth tripwire. When the flag is false the controller
/// returns 503 Service Unavailable; a misconfigured gateway can't
/// expose the route just because it's registered.
/// </para>
///
/// <para>
/// Mirrors the shape established by
/// <c>NetworkTierBackfillAdminController</c> in benefit-plan-service:
/// <c>[Route("api/v1/admin/...")]</c>, <see cref="IOptionsMonitor{T}"/>,
/// 503-when-disabled, idempotent reruns. Status surface is exposed via
/// <see cref="GetStatus"/> (Decision 15 — GET status IN, chunked
/// streaming OUT).
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/admin/claims/cosmos-migration")]
public sealed class AdminMigrationController : ControllerBase
{
    private readonly IClaimMigrationService _migration;
    private readonly IOptionsMonitor<ClaimMigrationOptions> _options;
    private readonly ILogger<AdminMigrationController> _logger;

    public AdminMigrationController(
        IClaimMigrationService migration,
        IOptionsMonitor<ClaimMigrationOptions> options,
        ILogger<AdminMigrationController> logger)
    {
        _migration = migration;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Run the migration. Defaults to dry-run; set
    /// <see cref="ClaimMigrationRequest.DryRun"/>=false to apply
    /// writes against the target container. Idempotent: rows already
    /// present in the target are skipped.
    /// </summary>
    [HttpPost("run")]
    [ProducesResponseType(typeof(ClaimMigrationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ClaimMigrationResult>> Run(
        [FromBody] ClaimMigrationRequest? request,
        CancellationToken ct)
    {
        if (!_options.CurrentValue.MigrationsEnabled)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    error = "claims_migration_disabled",
                    message =
                        "Cosmos partition-key migration is gated. Set " +
                        "ClaimsCosmosMigration:MigrationsEnabled=true to enable, " +
                        "and ensure the deployment layer (NetworkPolicy / gateway ACL) " +
                        "restricts access to this route.",
                });
        }

        request ??= new ClaimMigrationRequest();

        if (request.BatchSize is < 1)
        {
            return BadRequest(new
            {
                error = "invalid_batch_size",
                message = "batchSize must be greater than zero when supplied.",
            });
        }

        request.ActorId ??= ResolveActorId();
        request.CorrelationId ??= HttpContext.TraceIdentifier;

        _logger.LogInformation(
            "claims cosmos migration triggered dryRun={DryRun} actor={Actor} correlation={Correlation}",
            request.DryRun, Sanitize(request.ActorId), Sanitize(request.CorrelationId));

        try
        {
            var result = await _migration.RunAsync(request, ct);
            return Ok(result);
        }
        catch (MigrationAlreadyRunningException)
        {
            return Conflict(new
            {
                error = "migration_in_progress",
                message = "A claim migration run is already in progress. Retry after it completes.",
            });
        }
    }

    /// <summary>
    /// Snapshot of current migration state. Operators poll this while
    /// a run is active to monitor progress alongside structured logs
    /// and Prometheus counters.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(ClaimMigrationStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<ClaimMigrationStatus> GetStatus()
    {
        if (!_options.CurrentValue.MigrationsEnabled)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    error = "claims_migration_disabled",
                    message =
                        "Cosmos partition-key migration is gated. Set " +
                        "ClaimsCosmosMigration:MigrationsEnabled=true to enable, " +
                        "and ensure the deployment layer (NetworkPolicy / gateway ACL) " +
                        "restricts access to this route.",
                });
        }

        return Ok(_migration.GetStatus());
    }

    private string ResolveActorId()
    {
        var sub = HttpContext.User?.FindFirst("sub")?.Value;
        if (!string.IsNullOrEmpty(sub)) return sub;
        if (HttpContext.Request.Headers.TryGetValue("X-User-Id", out var header) && !string.IsNullOrEmpty(header.ToString()))
            return header.ToString();
        return "admin:claims-cosmos-migration";
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
