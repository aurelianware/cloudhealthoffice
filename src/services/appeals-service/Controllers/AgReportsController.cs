using AppealsService.Middleware;
using AppealsService.Models;
using AppealsService.Reports;
using AppealsService.Repositories;
using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Reporting;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace AppealsService.Controllers;

/// <summary>
/// Appeals and grievances standard reports for the portal Reports page
/// (portal-reporting.md §4.7). Each needs <c>reports:um</c> or
/// <c>reports:compliance</c>. A run that returns cases is written to the
/// record access log with every case (and member) it returned, CSV included;
/// an interactive run returns at most <see cref="StandardReports.MaxRows"/>
/// rows and says when it was cut.
/// </summary>
[ApiController]
[Route("api/appeals/reports")]
[Produces("application/json")]
[RequirePermission(AgReportCatalog.Permission)]
public class AgReportsController : ControllerBase
{
    private const string RecordType = "Appeal";
    private const string ReportAction = "Report";

    private readonly IAppealRepository _appeals;
    private readonly ICurrentActor _actor;
    private readonly TimeProvider _clock;

    public AgReportsController(IAppealRepository appeals, ICurrentActor actor, TimeProvider? clock = null)
    {
        _appeals = appeals;
        _actor = actor;
        _clock = clock ?? TimeProvider.System;
    }

    private string TenantId => HttpContext.GetTenantId();

    /// <summary>The appeals and grievances reports: id, parameters, columns.</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<ReportDefinition>> Catalog() => Ok(AgReportCatalog.All);

    /// <summary>The 42 CFR 438.416 log: every case received in [from, to).</summary>
    [HttpGet(AgReportCatalog.CaseLog)]
    [AuditRecordAccess(RecordType, ReportAction, ResultIdProperty = "AppealId")]
    public async Task<IActionResult> CaseLog(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] string? kind = null,
        [FromQuery] string? format = null,
        CancellationToken ct = default)
    {
        if (BadKind(kind) is { } bad) return bad;
        var asOf = _clock.GetUtcNow().UtcDateTime;
        var (fromDate, toDate, error) = StandardReports.Period(from, to, asOf, TimeSpan.FromDays(30), TimeSpan.FromDays(366));
        if (error is not null) return BadRequest(new { error });

        var activity = await _appeals.ListInsightActivityAsync(TenantId, fromDate, toDate, ct);
        var rows = AgReportBuilder.Received(activity, fromDate, toDate, lineOfBusiness, kind).Select(a => AgReportBuilder.Case(a, asOf)).ToList();
        var run = ReportRun<AgCaseRow>.Of(AgReportCatalog.Find(AgReportCatalog.CaseLog), _actor.UserId, asOf, rows, StandardReports.MaxRows,
            new Dictionary<string, string?>
            {
                ["from"] = fromDate.ToString("O"), ["to"] = toDate.ToString("O"),
                ["lineOfBusiness"] = lineOfBusiness?.ToString(), ["kind"] = kind,
            },
            [
                "The enrollee is identified by member id. The reason for the appeal or grievance is not included (it is held encrypted); open the case for it.",
                "OnTime is blank for a case still open and not yet due, or closed without a decision (withdrawn, expired, administrative).",
                "Acknowledgment dates are not recorded yet. GrievanceCategory is UNCATEGORIZED for a grievance recorded without one.",
            ]);
        return ReportResults.Respond(this, run, format);
    }

    /// <summary>Per receipt month and level: on time, late, open, overturned. Counts only, so not a record read.</summary>
    [HttpGet(AgReportCatalog.TimelinessByMonth)]
    public async Task<IActionResult> TimelinessByMonth(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] string? format = null,
        CancellationToken ct = default)
    {
        var asOf = _clock.GetUtcNow().UtcDateTime;
        var toDate = (to ?? asOf).ToUniversalTime();
        var (fromDate, _, error) = StandardReports.Period(
            from ?? StandardReports.MonthOf(toDate).AddMonths(-12), toDate, asOf, TimeSpan.Zero, TimeSpan.FromDays(400));
        if (error is not null) return BadRequest(new { error });

        var activity = await _appeals.ListInsightActivityAsync(TenantId, fromDate, toDate, ct);
        var rows = AgReportBuilder.ByMonth(AgReportBuilder.Received(activity, fromDate, toDate, lineOfBusiness, null), fromDate, toDate, asOf);
        var run = ReportRun<AgMonthRow>.Of(AgReportCatalog.Find(AgReportCatalog.TimelinessByMonth), _actor.UserId, asOf, rows, int.MaxValue,
            new Dictionary<string, string?>
            {
                ["from"] = fromDate.ToString("O"), ["to"] = toDate.ToString("O"), ["lineOfBusiness"] = lineOfBusiness?.ToString(),
            },
            [
                "On time is judged against each case's own deadline, including a lawful extension. A decided case with no deadline counts as late.",
                "PercentOnTime and PercentOverturned are of decided cases; overturns are not counted for grievances.",
            ]);
        return ReportResults.Respond(this, run, format);
    }

    /// <summary>Grievances received in [from, to) by category. Counts only, so not a record read.</summary>
    [HttpGet(AgReportCatalog.GrievancesByCategory)]
    public async Task<IActionResult> GrievancesByCategory(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] string? format = null,
        CancellationToken ct = default)
    {
        var asOf = _clock.GetUtcNow().UtcDateTime;
        var (fromDate, toDate, error) = StandardReports.Period(from, to, asOf, TimeSpan.FromDays(90), TimeSpan.FromDays(400));
        if (error is not null) return BadRequest(new { error });

        var activity = await _appeals.ListInsightActivityAsync(TenantId, fromDate, toDate, ct);
        var rows = AgReportBuilder.ByCategory(AgReportBuilder.Received(activity, fromDate, toDate, lineOfBusiness, AgReportBuilder.GrievanceKind));
        var run = ReportRun<AgCategoryRow>.Of(AgReportCatalog.Find(AgReportCatalog.GrievancesByCategory), _actor.UserId, asOf, rows, int.MaxValue,
            new Dictionary<string, string?>
            {
                ["from"] = fromDate.ToString("O"), ["to"] = toDate.ToString("O"), ["lineOfBusiness"] = lineOfBusiness?.ToString(),
            },
            ["Grievances recorded without a category are counted under UNCATEGORIZED."]);
        return ReportResults.Respond(this, run, format);
    }

    /// <summary>Every open case, soonest deadline first.</summary>
    [HttpGet(AgReportCatalog.OpenByDeadline)]
    [AuditRecordAccess(RecordType, ReportAction, ResultIdProperty = "AppealId")]
    public async Task<IActionResult> OpenByDeadline(
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] string? kind = null,
        [FromQuery] string? format = null,
        CancellationToken ct = default)
    {
        if (BadKind(kind) is { } bad) return bad;
        var asOf = _clock.GetUtcNow().UtcDateTime;
        var open = await _appeals.ListOpenAsync(TenantId, ct);
        var rows = AgReportBuilder.Open(open, lineOfBusiness, kind).Select(a => AgReportBuilder.OpenCase(a, asOf)).ToList();
        var run = ReportRun<AgOpenRow>.Of(AgReportCatalog.Find(AgReportCatalog.OpenByDeadline), _actor.UserId, asOf, rows, StandardReports.MaxRows,
            new Dictionary<string, string?> { ["lineOfBusiness"] = lineOfBusiness?.ToString(), ["kind"] = kind },
            ["A case with no deadline is listed last."]);
        return ReportResults.Respond(this, run, format);
    }

    private ActionResult? BadKind(string? kind) =>
        kind is null
        || string.Equals(kind, AgReportBuilder.AppealKind, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, AgReportBuilder.GrievanceKind, StringComparison.OrdinalIgnoreCase)
            ? null
            : BadRequest(new { error = "kind is Appeal or Grievance." });
}
