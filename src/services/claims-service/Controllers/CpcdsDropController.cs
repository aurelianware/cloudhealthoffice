using ClaimsService.Import.Cpcds.Drop;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// The tenant's CPCDS file drop: what each load took and did, and a way to
/// load what is waiting now instead of at the next poll.
/// See docs/integration/CPCDS-FILE-DROP-IMPORTER.md.
/// </summary>
[ApiController]
[Route("api/v1/claims/import/cpcds/drops")]
[Produces("application/json")]
public class CpcdsDropController : ControllerBase
{
    private readonly ICpcdsDropRunRepository _runs;
    private readonly CpcdsDropRunner _runner;
    private readonly CpcdsDropTenants _tenants;
    private readonly ICpcdsDropTenantSettingsRepository _settings;
    private readonly CpcdsFreshnessReporter _reporter;
    private readonly ICurrentActor _actor;
    private readonly TimeProvider _clock;

    public CpcdsDropController(
        ICpcdsDropRunRepository runs, CpcdsDropRunner runner, CpcdsFreshnessReporter reporter,
        CpcdsDropTenants tenants, ICpcdsDropTenantSettingsRepository settings, ICurrentActor actor,
        TimeProvider? clock = null)
    {
        _reporter = reporter;
        _runs = runs;
        _runner = runner;
        _tenants = tenants;
        _settings = settings;
        _actor = actor;
        _clock = clock ?? TimeProvider.System;
    }

    private string TenantId =>
        HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenant ? tenant : _actor.TenantId;

    /// <summary>The tenant's most recent drop runs, newest first.</summary>
    [HttpGet]
    [RequirePermission(ClaimsPermissions.Import)]
    [ProducesResponseType(typeof(IReadOnlyList<CpcdsDropRun>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CpcdsDropRun>>> List([FromQuery] int limit = 30, CancellationToken ct = default)
        => Ok(await _runs.ListAsync(TenantId, Math.Clamp(limit, 1, 200), ct));

    /// <summary>
    /// Whether the tenant's core-fed data is current (newest claim extract
    /// loaded, its age, recent failures) and whether the loads add up (claims
    /// and paid amounts in the files against what loaded, over the last
    /// <paramref name="days"/> days), with what needs attention.
    /// </summary>
    [HttpGet("report")]
    [RequirePermission(ClaimsPermissions.Import)]
    [ProducesResponseType(typeof(CpcdsFreshnessReport), StatusCodes.Status200OK)]
    public async Task<ActionResult<CpcdsFreshnessReport>> Report([FromQuery] int days = 30, CancellationToken ct = default)
        => Ok(await _reporter.BuildAsync(TenantId, days, ct));

    /// <summary>
    /// The tenant's drop settings, where they came from (saved by the tenant,
    /// deployment configuration, or none), and whether this deployment loads
    /// drops at all.
    /// </summary>
    [HttpGet("settings")]
    [RequirePermission(ClaimsPermissions.Import)]
    [ProducesResponseType(typeof(CpcdsDropSettingsView), StatusCodes.Status200OK)]
    public async Task<ActionResult<CpcdsDropSettingsView>> GetSettings(CancellationToken ct = default)
        => Ok(await ViewAsync(ct));

    /// <summary>
    /// Saves the tenant's drop settings, turning its drop on or off without a
    /// redeploy. Replaces any entry deployment configuration has for the
    /// tenant. Takes effect at the next poll.
    /// </summary>
    [HttpPut("settings")]
    [RequirePermission(ClaimsPermissions.Import)]
    [ProducesResponseType(typeof(CpcdsDropSettingsView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CpcdsDropSettingsView>> PutSettings(
        [FromBody] CpcdsDropSettingsRequest request, CancellationToken ct = default)
    {
        if (!CpcdsDropTenants.TenantIdPattern.IsMatch(TenantId))
            return BadRequest(new { error = "This tenant's id cannot name a drop folder." });

        var errors = request.Validate();
        if (errors.Count > 0)
            return BadRequest(new { error = "invalid_settings", errors });

        await _settings.SaveAsync(new CpcdsDropTenantSettings
        {
            TenantId = TenantId,
            Enabled = request.Enabled,
            Settings = request.ToOptions(),
            UpdatedAt = _clock.GetUtcNow().UtcDateTime,
            UpdatedBy = _actor.UserId,
        }, ct);

        return Ok(await ViewAsync(ct));
    }

    [HttpGet("{runId}")]
    [RequirePermission(ClaimsPermissions.Import)]
    [ProducesResponseType(typeof(CpcdsDropRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CpcdsDropRun>> Get(string runId, CancellationToken ct = default)
        => await _runs.GetAsync(TenantId, runId, ct) is { } run ? Ok(run) : NotFound();

    /// <summary>
    /// Loads what is waiting in the tenant's drop now (files still being
    /// written are left for later). 204 when nothing is ready.
    /// </summary>
    [HttpPost("run")]
    [RequirePermission(ClaimsPermissions.Import)]
    [ProducesResponseType(typeof(CpcdsDropRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RunNow(CancellationToken ct = default)
    {
        if (await _tenants.ResolveAsync(TenantId, ct) is null)
            return NotFound(new { error = "No CPCDS drop is configured for this tenant; save drop settings first." });

        return await _runner.RunAsync(TenantId, ct) is { } run ? Ok(run) : NoContent();
    }

    private async Task<CpcdsDropSettingsView> ViewAsync(CancellationToken ct)
    {
        var (settings, enabled, source) = await _tenants.DescribeAsync(TenantId, ct);
        var stored = source == CpcdsDropSettingsSource.Stored ? await _settings.GetAsync(TenantId, ct) : null;
        return new CpcdsDropSettingsView
        {
            TenantId = TenantId,
            DropEnabledForDeployment = _tenants.DropEnabled,
            Enabled = enabled,
            Loading = _tenants.DropEnabled && enabled && settings is not null,
            Source = source,
            InboundFolder = $"{TenantId}/inbound",
            Settings = settings is null ? null : CpcdsDropSettingsRequest.FromOptions(enabled, settings),
            UpdatedAt = stored?.UpdatedAt,
            UpdatedBy = stored?.UpdatedBy,
        };
    }
}
