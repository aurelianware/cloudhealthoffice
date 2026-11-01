using AuthorizationService.Models;
using AuthorizationService.Reports;
using AuthorizationService.Repositories;
using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Reporting;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace AuthorizationService.Controllers;

/// <summary>
/// Utilization management standard reports for the portal Reports page
/// (portal-reporting.md §4.7). Each needs <c>reports:um</c> or
/// <c>reports:compliance</c>. A run that returns requests is written to the
/// record access log with every request it returned (JSON or CSV); an
/// interactive run returns at most <see cref="StandardReports.MaxRows"/> rows
/// and says when it was cut.
/// </summary>
[ApiController]
[Route("api/authorizations/reports")]
[Produces("application/json")]
[RequirePermission(UmReportCatalog.Permission)]
public class UmReportsController : ControllerBase
{
    private const string RecordType = "Authorization";
    private const string ReportAction = "Report";

    private readonly IAuthorizationRepository _authorizations;
    private readonly ICurrentActor _actor;
    private readonly TimeProvider _clock;

    public UmReportsController(IAuthorizationRepository authorizations, ICurrentActor actor, TimeProvider? clock = null)
    {
        _authorizations = authorizations;
        _actor = actor;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The UM reports: id, parameters, columns.</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<ReportDefinition>> Catalog() => Ok(UmReportCatalog.All);

    /// <summary>Every non-drug request received in [from, to), with its timeframe outcome.</summary>
    [HttpGet(UmReportCatalog.DecisionTimeliness)]
    [AuditRecordAccess(RecordType, ReportAction, ResultIdProperty = "AuthorizationId")]
    public async Task<IActionResult> DecisionTimeliness(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] string? requestType = null,
        [FromQuery] bool? withinTimeframe = null,
        [FromQuery] string? format = null)
    {
        if (BadRequestType(requestType) is { } bad) return bad;
        var asOf = _clock.GetUtcNow().UtcDateTime;
        var (fromDate, toDate, error) = StandardReports.Period(from, to, asOf, TimeSpan.FromDays(30), TimeSpan.FromDays(366));
        if (error is not null) return BadRequest(new { error });

        var activity = await _authorizations.ListActivityBetweenAsync(fromDate, toDate);
        var rows = UmReportBuilder.Received(activity, fromDate, toDate, lineOfBusiness, requestType)
            .Select(a => UmReportBuilder.Decision(a, asOf))
            .Where(r => withinTimeframe is null || r.WithinTimeframe == withinTimeframe)
            .ToList();
        var run = ReportRun<UmDecisionRow>.Of(UmReportCatalog.Find(UmReportCatalog.DecisionTimeliness), _actor.UserId, asOf, rows,
            StandardReports.MaxRows,
            new Dictionary<string, string?>
            {
                ["from"] = fromDate.ToString("O"), ["to"] = toDate.ToString("O"), ["lineOfBusiness"] = lineOfBusiness?.ToString(),
                ["requestType"] = requestType, ["withinTimeframe"] = withinTimeframe?.ToString(),
            },
            [
                "Drug requests are not included (they have their own timeframes).",
                "WithinTimeframe is blank for a request still open and not yet due, or decided with no usable decision time.",
            ]);
        return ReportResults.Respond(this, run, format);
    }

    /// <summary>
    /// Per receipt month and request type: on time, late, open past due, open
    /// not yet due. Counts only, so the run is not a record read.
    /// </summary>
    [HttpGet(UmReportCatalog.TimelinessByMonth)]
    public async Task<IActionResult> TimelinessByMonth(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] string? format = null)
    {
        var asOf = _clock.GetUtcNow().UtcDateTime;
        var toDate = StandardReports.AsUtc(to ?? asOf);
        var (fromDate, _, error) = StandardReports.Period(
            from ?? StandardReports.MonthOf(toDate).AddMonths(-12), toDate, asOf, TimeSpan.Zero, TimeSpan.FromDays(400));
        if (error is not null) return BadRequest(new { error });

        var activity = await _authorizations.ListActivityBetweenAsync(fromDate, toDate);
        var rows = UmReportBuilder.ByMonth(UmReportBuilder.Received(activity, fromDate, toDate, lineOfBusiness, null), fromDate, toDate, asOf);
        var run = ReportRun<UmMonthRow>.Of(UmReportCatalog.Find(UmReportCatalog.TimelinessByMonth), _actor.UserId, asOf, rows,
            int.MaxValue,
            new Dictionary<string, string?>
            {
                ["from"] = fromDate.ToString("O"), ["to"] = toDate.ToString("O"), ["lineOfBusiness"] = lineOfBusiness?.ToString(),
            },
            [
                "Drug requests are not included.",
                "PercentOnTime is of decisions with a usable decision time (DecidedOnTime + DecidedLate).",
            ]);
        return ReportResults.Respond(this, run, format);
    }

    /// <summary>Every open request, soonest deadline first.</summary>
    [HttpGet(UmReportCatalog.PendingByDeadline)]
    [AuditRecordAccess(RecordType, ReportAction, ResultIdProperty = "AuthorizationId")]
    public async Task<IActionResult> PendingByDeadline(
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] string? requestType = null,
        [FromQuery] string? format = null)
    {
        if (BadRequestType(requestType) is { } bad) return bad;
        var asOf = _clock.GetUtcNow().UtcDateTime;
        var open = await _authorizations.GetOpenAuthorizationsAsync(_actor.TenantId);
        var rows = UmReportBuilder.Open(open, lineOfBusiness, requestType).Select(a => UmReportBuilder.Pending(a, asOf)).ToList();
        var run = ReportRun<UmPendingRow>.Of(UmReportCatalog.Find(UmReportCatalog.PendingByDeadline), _actor.UserId, asOf, rows,
            StandardReports.MaxRows,
            new Dictionary<string, string?> { ["lineOfBusiness"] = lineOfBusiness?.ToString(), ["requestType"] = requestType },
            ["Drug requests are not included."]);
        return ReportResults.Respond(this, run, format);
    }

    private ActionResult? BadRequestType(string? requestType) =>
        requestType is null
        || string.Equals(requestType, UmReportBuilder.Standard, StringComparison.OrdinalIgnoreCase)
        || string.Equals(requestType, UmReportBuilder.Expedited, StringComparison.OrdinalIgnoreCase)
            ? null
            : BadRequest(new { error = "requestType is Standard or Expedited." });
}
