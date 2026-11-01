using System.Text;
using ClaimsService.Audit;
using ClaimsService.Models;
using ClaimsService.Reports;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// The claims module's standard reports (docs/architecture/portal-reporting.md):
/// the portal Reports page lists the catalog and runs a report as JSON or
/// CSV. Each needs <c>reports:claims</c> (claims supervisors; tenant admins
/// through <c>*:*</c>). A run that returns claims is written to the claim
/// access log with every claim it returned; an interactive run returns at
/// most <see cref="ClaimReportLimits.MaxRows"/> rows and says when it was cut.
/// </summary>
[ApiController]
[Route("api/claims/reports")]
[Produces("application/json")]
[RequirePermission(ClaimReportCatalog.Permission)]
public class ClaimReportsController : ControllerBase
{
    private const string CsvContentType = "text/csv";

    private readonly IClaimRepository _claims;
    private readonly ICurrentActor _actor;
    private readonly TimeProvider _clock;

    public ClaimReportsController(IClaimRepository claims, ICurrentActor actor, TimeProvider? clock = null)
    {
        _claims = claims;
        _actor = actor;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The reports this module offers: id, parameters, columns.</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<ClaimReportDefinition>> Catalog() => Ok(ClaimReportCatalog.All);

    /// <summary>Every claim pended now, oldest receipt first.</summary>
    [HttpGet(ClaimReportCatalog.PendedInventory)]
    [AuditClaimAccess(ClaimAccessActions.Report)]
    public async Task<IActionResult> PendedInventory(
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] string? pendCode = null,
        [FromQuery] string? format = null,
        CancellationToken ct = default)
    {
        var asOf = _clock.GetUtcNow().UtcDateTime;
        var criteria = new ClaimCriteria
        {
            Status = ClaimStatus.Pended,
            PendCode = string.IsNullOrWhiteSpace(pendCode) ? null : pendCode.Trim(),
            LineOfBusiness = lineOfBusiness,
        };
        var (rows, total) = await _claims.GetReportRowsAsync(criteria, ClaimReportLimits.MaxRows, ct);
        var run = Run(ClaimReportCatalog.PendedInventory, asOf, total,
            rows.Select(c => ClaimReportBuilder.Inventory(c, asOf)).ToList(),
            new() { ["lineOfBusiness"] = lineOfBusiness?.ToString(), ["pendCode"] = criteria.PendCode });
        return Respond(run, format);
    }

    /// <summary>Every claim received and not yet adjudicated, oldest first, with its age bucket.</summary>
    [HttpGet(ClaimReportCatalog.OpenAging)]
    [AuditClaimAccess(ClaimAccessActions.Report)]
    public async Task<IActionResult> OpenAging(
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] int? minAgeDays = null,
        [FromQuery] string? format = null,
        CancellationToken ct = default)
    {
        if (minAgeDays is < 0)
            return BadRequest(new { error = "minAgeDays must be 0 or more." });

        var asOf = _clock.GetUtcNow().UtcDateTime;
        var criteria = new ClaimCriteria
        {
            Open = true,
            LineOfBusiness = lineOfBusiness,
            // Full days since receipt: aged at least N days means received at or before now - N days.
            ReceivedOnOrBefore = minAgeDays is > 0 ? asOf.AddDays(-minAgeDays.Value) : null,
        };
        var (rows, total) = await _claims.GetReportRowsAsync(criteria, ClaimReportLimits.MaxRows, ct);
        var run = Run(ClaimReportCatalog.OpenAging, asOf, total,
            rows.Select(c => ClaimReportBuilder.Inventory(c, asOf)).ToList(),
            new() { ["lineOfBusiness"] = lineOfBusiness?.ToString(), ["minAgeDays"] = minAgeDays?.ToString() });
        return Respond(run, format);
    }

    /// <summary>Claims denied in [from, to), with the claim-level denial reason.</summary>
    [HttpGet(ClaimReportCatalog.Denials)]
    [AuditClaimAccess(ClaimAccessActions.Report)]
    public async Task<IActionResult> Denials(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] string? denialReasonCode = null,
        [FromQuery] string? format = null,
        CancellationToken ct = default)
    {
        var asOf = _clock.GetUtcNow().UtcDateTime;
        var (fromDate, toDate, error) = Period(from, to, asOf, TimeSpan.FromDays(30));
        if (error is not null) return BadRequest(new { error });

        var criteria = new ClaimCriteria
        {
            Denied = true,
            DenialReasonCode = string.IsNullOrWhiteSpace(denialReasonCode) ? null : denialReasonCode.Trim(),
            AdjudicatedFrom = fromDate,
            AdjudicatedBefore = toDate,
            LineOfBusiness = lineOfBusiness,
        };
        var (rows, total) = await _claims.GetReportRowsAsync(criteria, ClaimReportLimits.MaxRows, ct);
        var run = Run(ClaimReportCatalog.Denials, asOf, total,
            rows.Select(ClaimReportBuilder.Denial).ToList(),
            new()
            {
                ["from"] = fromDate.ToString("O"), ["to"] = toDate.ToString("O"),
                ["lineOfBusiness"] = lineOfBusiness?.ToString(), ["denialReasonCode"] = criteria.DenialReasonCode,
            });
        return Respond(run, format);
    }

    /// <summary>
    /// For claims received in each month of [from, to): how many were
    /// adjudicated within 30 and 90 days of receipt, against the 42 CFR
    /// 447.45(d) standards (90% and 99%). Counts only: no claim detail, so
    /// the run is not a claim read and is not in the claim access log.
    /// </summary>
    [HttpGet(ClaimReportCatalog.ProcessingTimeliness)]
    public async Task<IActionResult> ProcessingTimeliness(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] string? format = null,
        CancellationToken ct = default)
    {
        var asOf = _clock.GetUtcNow().UtcDateTime;
        var toDate = (to ?? asOf).ToUniversalTime();
        var fromDate = (from ?? new DateTime(toDate.Year, toDate.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-12)).ToUniversalTime();
        if (fromDate >= toDate)
            return BadRequest(new { error = "from must be before to." });
        if (toDate - fromDate > TimeSpan.FromDays(400))
            return BadRequest(new { error = "The receipt period can be at most 13 months." });

        var criteria = new ClaimCriteria
        {
            // ReceivedAfter is exclusive; one tick earlier makes from inclusive.
            ReceivedAfter = fromDate.AddTicks(-1),
            ReceivedOnOrBefore = toDate.AddTicks(-1),
            LineOfBusiness = lineOfBusiness,
        };
        var measured = await _claims.GetReceiptToAdjudicationAsync(criteria, ClaimReportLimits.MaxTimelinessClaims + 1, ct);
        var truncated = measured.Count > ClaimReportLimits.MaxTimelinessClaims;
        var rows = ClaimReportBuilder.Timeliness(measured.Take(ClaimReportLimits.MaxTimelinessClaims), fromDate, toDate, asOf);

        var notes = new List<string>
        {
            "Every current claim received in the month is counted, not only clean claims: CHO does not yet record whether a claim was clean, so the percentages are a floor on the clean-claim figures the regulation measures.",
            "Adjudicated means paid or denied (the claim's adjudication date). An adjusted claim is measured on its current version's adjudication date.",
            "A standard is blank while the month's window is still open (30 or 90 days after its last receipt day).",
        };
        if (truncated)
            notes.Add($"More than {ClaimReportLimits.MaxTimelinessClaims:N0} claims were received in the period; only the earliest {ClaimReportLimits.MaxTimelinessClaims:N0} were measured. Narrow the period.");

        var run = Run(ClaimReportCatalog.ProcessingTimeliness, asOf, rows.Count, rows,
            new() { ["from"] = fromDate.ToString("O"), ["to"] = toDate.ToString("O"), ["lineOfBusiness"] = lineOfBusiness?.ToString() },
            notes, truncated);
        return Respond(run, format);
    }

    private static (DateTime From, DateTime To, string? Error) Period(DateTime? from, DateTime? to, DateTime asOf, TimeSpan defaultLength)
    {
        var toDate = (to ?? asOf).ToUniversalTime();
        var fromDate = (from ?? toDate - defaultLength).ToUniversalTime();
        if (fromDate >= toDate) return (fromDate, toDate, "from must be before to.");
        if (toDate - fromDate > TimeSpan.FromDays(366)) return (fromDate, toDate, "The period can be at most a year.");
        return (fromDate, toDate, null);
    }

    private ClaimReportRun<TRow> Run<TRow>(
        string reportId, DateTime asOf, int total, IReadOnlyList<TRow> rows, Dictionary<string, string?> parameters,
        IReadOnlyList<string>? notes = null, bool? truncated = null)
    {
        var definition = ClaimReportCatalog.Find(reportId)!;
        return new ClaimReportRun<TRow>
        {
            ReportId = definition.Id,
            Name = definition.Name,
            AsOf = asOf,
            RunBy = _actor.UserId,
            Parameters = parameters,
            TotalCount = total,
            Truncated = truncated ?? total > rows.Count,
            Columns = definition.Columns,
            Items = rows,
            Notes = notes ?? [],
        };
    }

    private IActionResult Respond<TRow>(ClaimReportRun<TRow> run, string? format)
    {
        if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            return Ok(run);

        // The access log reads the claims from here (the file is not an object it can inspect).
        HttpContext.Items[ClaimSubjects.ItemsKey] = run.Items;
        if (run.Truncated)
            Response.Headers["X-Report-Truncated"] = $"{run.Items.Count} of {run.TotalCount}";
        var csv = ClaimReportBuilder.Csv(run.Columns, run.Items);
        var fileName = $"{run.ReportId}-{run.AsOf:yyyyMMdd'T'HHmm'Z'}.csv";
        return File(Encoding.UTF8.GetBytes(csv), CsvContentType, fileName);
    }
}
