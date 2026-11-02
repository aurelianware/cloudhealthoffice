using BenefitPlanService.Import.FeeSchedules;
using BenefitPlanService.Middleware;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

/// <summary>
/// The shared fee schedule library and a tenant's own schedules, for guided
/// onboarding: browse the library, open a schedule's rates, and deploy it into
/// the tenant at a percentage as a plan default or a standalone schedule.
/// Platform administrators publish a schedule they loaded with an importer
/// into the library. See docs/integration/FEE-SCHEDULE-LIBRARY.md.
/// Schedule ids carry colons and free-text names, so they travel as a query
/// parameter or in the body, never as a path segment.
/// </summary>
[ApiController]
public sealed class FeeScheduleLibraryController : ControllerBase
{
    private readonly IFeeScheduleLibraryService _library;
    private readonly ICurrentActor _actor;

    public FeeScheduleLibraryController(IFeeScheduleLibraryService library, ICurrentActor actor)
    {
        _library = library;
        _actor = actor;
    }

    /// <summary>Schedules in the shared library, optionally for one state.</summary>
    [HttpGet("api/v1/fee-schedule-library")]
    [RequirePermission("benefits:read")]
    [ProducesResponseType(typeof(IReadOnlyList<FeeScheduleSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<FeeScheduleSummary>>> ListLibrary([FromQuery] string? jurisdiction, CancellationToken ct) =>
        Ok(await _library.ListAsync(_library.LibraryTenantId, jurisdiction, ct));

    /// <summary>One library schedule with a page of its rates (filter by code prefix).</summary>
    [HttpGet("api/v1/fee-schedule-library/schedule")]
    [RequirePermission("benefits:read")]
    [ProducesResponseType(typeof(FeeScheduleDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<FeeScheduleDetail>> GetLibrarySchedule(
        [FromQuery] string id, [FromQuery] string? code, [FromQuery] int skip = 0, [FromQuery] int take = 100, CancellationToken ct = default) =>
        Detail(_library.LibraryTenantId, id, code, skip, take, ct);

    /// <summary>
    /// Copy a library schedule into this tenant at a percentage. Dry run by
    /// default: the answer previews common codes at the library and deployed
    /// rates without writing.
    /// </summary>
    [HttpPost("api/v1/fee-schedule-library/deploy")]
    [RequirePermission("settings:manage")]
    [ProducesResponseType(typeof(FeeScheduleDeployReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FeeScheduleDeployReport>> Deploy([FromBody] FeeScheduleDeployRequest request, CancellationToken ct)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId)) return BadRequest(new { message = "Tenant context missing." });
        return await Run(() => _library.DeployAsync(tenantId, _actor.UserId, request, ct));
    }

    /// <summary>
    /// Copy a schedule from the caller's tenant into the shared library
    /// (platform administrators). Dry run by default.
    /// </summary>
    [HttpPost("api/v1/fee-schedule-library/publish")]
    [RequirePermission("platform:admin")]
    [ProducesResponseType(typeof(FeeScheduleDeployReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FeeScheduleDeployReport>> Publish([FromBody] FeeSchedulePublishRequest request, CancellationToken ct)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId)) return BadRequest(new { message = "Tenant context missing." });
        return await Run(() => _library.PublishToLibraryAsync(tenantId, _actor.UserId, request, ct));
    }

    /// <summary>This tenant's schedules (plan defaults, imports and deployed copies), without their lines.</summary>
    [HttpGet("api/v1/fee-schedules")]
    [RequirePermission("benefits:read")]
    [ProducesResponseType(typeof(IReadOnlyList<FeeScheduleSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<FeeScheduleSummary>>> ListTenant([FromQuery] string? jurisdiction, CancellationToken ct)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId)) return BadRequest(new { message = "Tenant context missing." });
        return Ok(await _library.ListAsync(tenantId, jurisdiction, ct));
    }

    /// <summary>One of this tenant's schedules with a page of its rates.</summary>
    [HttpGet("api/v1/fee-schedules/schedule")]
    [RequirePermission("benefits:read")]
    [ProducesResponseType(typeof(FeeScheduleDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<FeeScheduleDetail>> GetTenantSchedule(
        [FromQuery] string id, [FromQuery] string? code, [FromQuery] int skip = 0, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId))
            return Task.FromResult<ActionResult<FeeScheduleDetail>>(BadRequest(new { message = "Tenant context missing." }));
        return Detail(tenantId, id, code, skip, take, ct);
    }

    private async Task<ActionResult<FeeScheduleDetail>> Detail(string tenantId, string id, string? code, int skip, int take, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest(new { message = "id is required." });
        var detail = await _library.GetAsync(tenantId, id, code, skip, take, ct);
        return detail is null ? NotFound(new { message = $"Schedule '{id}' not found." }) : Ok(detail);
    }

    private async Task<ActionResult<FeeScheduleDeployReport>> Run(Func<Task<FeeScheduleDeployReport>> action)
    {
        FeeScheduleDeployReport report;
        try
        {
            report = await action();
        }
        catch (FeeScheduleConcurrencyException)
        {
            return Conflict(new { message = "The target schedule changed while deploying; try again." });
        }

        if (report.IsNotFound) return NotFound(report);
        if (report.Error is not null) return BadRequest(report);
        return Ok(report);
    }
}
