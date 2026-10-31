using ClaimsService.Import.Cpcds.Drop;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
    private readonly CpcdsDropOptions _options;
    private readonly CpcdsFreshnessReporter _reporter;
    private readonly ICurrentActor _actor;

    public CpcdsDropController(
        ICpcdsDropRunRepository runs, CpcdsDropRunner runner, CpcdsFreshnessReporter reporter,
        IOptions<CpcdsDropOptions> options, ICurrentActor actor)
    {
        _reporter = reporter;
        _runs = runs;
        _runner = runner;
        _options = options.Value;
        _actor = actor;
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
        if (!_options.Enabled || !_options.Tenants.ContainsKey(TenantId))
            return NotFound(new { error = $"No CPCDS drop is configured for this tenant ({CpcdsDropOptions.SectionName})." });

        return await _runner.RunAsync(TenantId, ct) is { } run ? Ok(run) : NoContent();
    }
}
