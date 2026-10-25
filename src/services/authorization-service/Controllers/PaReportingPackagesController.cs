using System.ComponentModel.DataAnnotations;
using AuthorizationService.Backends;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace AuthorizationService.Controllers;

[ApiController]
[Route("api/authorizations/cms-0057/packages")]
[RequirePermission("authorizations:read")]
[RequirePermission(FreezePermission)]
public sealed class PaReportingPackagesController(IPaReportingPackageRepository packages,
    IAuthorizationBackendSelector backends, ICurrentActor actor) : ControllerBase
{
    public const string FreezePermission = "authorizations:reporting-freeze";

    [HttpPost]
    public Task<IActionResult> Freeze(FreezePaReportingPackage command, CancellationToken ct) => Guard(async () =>
        Ok(await packages.FreezeAsync(command, actor.UserId, ct)));

    [HttpGet("{id}")]
    [AuditRecordAccess("AuthorizationReportingPackage", "Read", RecordIdRouteValue = "id")]
    public Task<IActionResult> Get(string id, CancellationToken ct) => Guard(async () =>
    {
        var package = await packages.GetAsync(id, ct);
        return package is null ? NotFound() : Ok(package);
    });

    [HttpGet("{id}/inputs")]
    [AuditRecordAccess("AuthorizationReportingPackage", "Inputs", RecordIdRouteValue = "id")]
    public Task<IActionResult> Inputs(string id, CancellationToken ct, [FromQuery] int afterOrdinal = -1,
        [FromQuery] int pageSize = 100) => Guard(async () =>
    {
        var rows = await packages.ReadInputsAsync(id, afterOrdinal, pageSize, ct);
        return rows is null ? NotFound() : Ok(rows);
    });

    private async Task<IActionResult> Guard(Func<Task<IActionResult>> operation)
    {
        Response.Headers.CacheControl = "no-store";
        if (!backends.Resolve().IsAuthoritative)
            return Conflict(new { code = "NATIVE_REPORTING_REQUIRED" });
        try { return await operation(); }
        catch (Exception ex) when (ex is ArgumentException or ValidationException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { return BadRequest(new { code = "INVALID_REPORTING_SCOPE" }); }
        catch (ConcurrencyConflictException) { return Conflict(new { code = "REPORTING_KEY_CONFLICT" }); }
        catch (PaReportingPackageLimitException)
        { return UnprocessableEntity(new { code = "REPORTING_FREEZE_LIMIT", message = "No partial package was committed. Review workload/limits before retrying." }); }
        catch (PaReportingPackageIntegrityException)
        { return StatusCode(503, new { code = "REPORTING_PACKAGE_INTEGRITY_FAILURE" }); }
        catch (Exception ex) when (ex is PaReportingPackageUnavailableException or MongoException or NotSupportedException)
        { return StatusCode(503, new { code = "REPORTING_FREEZE_UNAVAILABLE" }); }
    }
}
