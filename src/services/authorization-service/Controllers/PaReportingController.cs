using System.ComponentModel.DataAnnotations;
using AuthorizationService.Backends;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.Cms0057;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace AuthorizationService.Controllers;

[ApiController]
[Route("api/authorizations/cms-0057")]
public sealed class PaReportingController(IAuthorizationRepository repository,
    IAuthorizationBackendSelector backends, ICurrentActor actor) : ControllerBase
{
    public const string RecordPermission = "authorizations:reporting-evidence";

    [HttpPost("{id}/facts")]
    [RequirePermission(RecordPermission)]
    [RequirePermission(AuthorizationsController.DecidePermission)]
    public async Task<IActionResult> RecordFact(string id, RecordPaReportingFact command, CancellationToken ct)
    {
        if (!backends.Resolve().IsAuthoritative) return Conflict(new { error = "PA reporting evidence requires the native authoritative backend." });
        try
        {
            var fact = await repository.RecordReportingFactAsync(id, command, actor.UserId, ct);
            return fact is null ? NotFound() : Ok(fact);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (ValidationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("annual-draft")]
    [RequirePermission("authorizations:read")]
    public async Task<IActionResult> AnnualDraft([FromQuery] int year, [FromQuery] string timeZone,
        [FromQuery] LineOfBusiness lineOfBusiness, [FromQuery] DateTimeOffset? outcomeCutoff,
        [FromQuery] DateTimeOffset? knowledgeCutoff, CancellationToken ct)
    {
        if (!backends.Resolve().IsAuthoritative) return Conflict(new { error = "Annual PA reporting requires the native authoritative backend." });
        var now = DateTime.UtcNow;
        var outcome = outcomeCutoff?.UtcDateTime ?? now;
        var knowledge = knowledgeCutoff?.UtcDateTime ?? now;
        if (outcome > now || knowledge > now) return BadRequest(new { error = "Cutoffs cannot be in the future." });
        AnnualPaReporting report;
        try { report = new(year, timeZone, lineOfBusiness, outcome, knowledge); }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { return BadRequest(new { error = "Supply a valid year, time zone and explicit line of business." }); }
        await foreach (var authorization in repository.StreamReportingCohortAsync(report.FromUtc,
            report.ToExclusiveUtc, lineOfBusiness, ct)) report.Add(authorization);
        return Ok(report.Finish());
    }
}
