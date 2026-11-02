using System.Globalization;
using System.Text.Json;
using ClaimsService.Finance;
using CloudHealthOffice.Infrastructure.Reporting;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// Claims figures for the monthly finance close: the paid-claims lag
/// triangle and a chain-ladder IBNR estimate from it. Aggregates only (no
/// member or claim detail), for finance:read.
/// </summary>
[ApiController]
[Route("api/claims/finance")]
[Produces("application/json")]
public sealed class ClaimsFinanceController : ControllerBase
{
    /// <summary>Most incurred months in one triangle.</summary>
    public const int MaxIncurredMonths = 48;

    private readonly IClaimRepository _claims;

    public ClaimsFinanceController(IClaimRepository claims) => _claims = claims;

    /// <summary>
    /// Lag triangle and IBNR for incurred months <c>incurredFrom</c>..<c>incurredTo</c>
    /// (yyyy-MM) as of <c>valuation</c> (yyyy-MM; payments after it are left
    /// out, so a past close can be reproduced). Member-months and an expected
    /// PMPM are optional; with them, immature months use projected PMPM.
    /// </summary>
    [HttpPost("ibnr")]
    [RequirePermission("finance:read")]
    [ProducesResponseType(typeof(IbnrResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IbnrResponse>> Ibnr([FromBody] IbnrRequest request, CancellationToken ct)
    {
        if (!TryMonth(request.IncurredFrom, out var from) || !TryMonth(request.IncurredTo, out var to)
            || !TryMonth(request.Valuation, out var valuation))
            return BadRequest(new { error = "incurredFrom, incurredTo and valuation must be months (yyyy-MM)" });
        if (from > to || to > valuation || LagTriangle.MonthsBetween(from, to) >= MaxIncurredMonths)
            return BadRequest(new { error = $"Need incurredFrom <= incurredTo <= valuation, at most {MaxIncurredMonths} incurred months" });
        if (request.FactorMonths is < 1 or > 60 || request.MaxDevelopmentLag is < 1 or > 60)
            return BadRequest(new { error = "factorMonths and maxDevelopmentLag must be 1 to 60" });

        var memberMonths = new Dictionary<DateOnly, int>();
        foreach (var (month, count) in request.MemberMonths ?? new())
        {
            if (!TryMonth(month, out var m) || count < 0)
                return BadRequest(new { error = $"memberMonths key '{month}' must be yyyy-MM with a non-negative count" });
            memberMonths[m] = count;
        }

        var versions = await _claims.GetPaymentVersionsAsync(
            from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            to.AddMonths(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            request.LineOfBusiness, ct);
        var triangle = LagTriangle.Build(LagTriangle.Increments(versions), from, to, valuation);
        var estimate = IbnrEstimator.Estimate(triangle, new IbnrOptions
        {
            FactorMonths = request.FactorMonths ?? 12,
            MaxDevelopmentLag = request.MaxDevelopmentLag ?? 24,
            PmpmFallbackBelowCompletion = request.PmpmFallbackBelowCompletion ?? 0.60m,
            MemberMonths = memberMonths.Count > 0 ? memberMonths : null,
            ExpectedPmpm = request.ExpectedPmpm
        });

        return Ok(new IbnrResponse
        {
            LineOfBusiness = request.LineOfBusiness?.ToString(),
            Valuation = valuation.ToString("yyyy-MM"),
            ClaimVersionsRead = versions.Count,
            Triangle = triangle.Rows.Select(r => new TriangleRowDto
            {
                IncurredMonth = r.IncurredMonth.ToString("yyyy-MM"),
                CumulativePaid = r.CumulativePaid
            }).ToList(),
            Estimate = estimate.Months.Select(m => new IbnrMonthDto
            {
                IncurredMonth = m.IncurredMonth.ToString("yyyy-MM"),
                Lag = m.Lag,
                PaidToDate = m.PaidToDate,
                CompletionFactor = m.CompletionFactor,
                Ultimate = m.Ultimate,
                Ibnr = m.Ibnr,
                Method = m.Method.ToString(),
                MemberMonths = m.MemberMonths,
                UltimatePmpm = m.UltimatePmpm
            }).ToList(),
            DevelopmentFactors = estimate.DevelopmentFactors,
            CompletionFactors = estimate.CompletionFactors,
            TotalPaid = estimate.TotalPaid,
            TotalUltimate = estimate.TotalUltimate,
            TotalIbnr = estimate.TotalIbnr,
            Notes = estimate.Notes
        });
    }

    /// <summary>
    /// HHSC's STAR Claims Lag Report (UMCM 5.6.2.11) figures for an SFY
    /// quarter: Medicaid plan payments by risk group, month of service and
    /// month of payment, through the quarter's last month. Each claim's risk
    /// group is its member's rate cell in the month of service (from the
    /// expected member-months loaded for capitation), mapped by
    /// <c>rateCellRiskGroups</c>; claims that can't be placed are left out
    /// and counted under <c>excluded</c>.
    /// </summary>
    [HttpPost("texas/claims-lag")]
    [RequirePermission("finance:read")]
    public async Task<ActionResult> TexasStarClaimsLag(
        [FromBody] TexasClaimsLagRequest request,
        [FromServices] IExpectedRateCellSource rateCells,
        [FromServices] ICurrentActor actor,
        CancellationToken ct)
    {
        var (lag, error) = await BuildTexasLagAsync(request, forWorkbook: false, rateCells, actor, ct);
        return error ?? Ok(lag);
    }

    /// <summary>
    /// The STAR Claims Lag Report workbook filled in: post HHSC's blank
    /// 5.6.2.11 workbook as <c>template</c> and the request JSON as
    /// <c>request</c>. The header and the payment cells are written; the
    /// subtotals and totals stay the template's formulas. A workbook whose
    /// sections or month labels can't be read, or that lacks a month the
    /// report needs, is refused (422).
    /// </summary>
    [HttpPost("texas/claims-lag/workbook")]
    [RequirePermission("finance:read")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(XlsxTemplate.MaxBytes + 1024 * 1024)]
    public async Task<ActionResult> TexasClaimsLagWorkbook(
        [FromForm] IFormFile? template,
        [FromForm] string? request,
        [FromServices] IExpectedRateCellSource rateCells,
        [FromServices] ICurrentActor actor,
        CancellationToken ct)
    {
        if (template is null || template.Length == 0)
            return BadRequest(new { error = "Attach HHSC's blank STAR Claims Lag Report (UMCM 5.6.2.11) as 'template'" });
        if (template.Length > XlsxTemplate.MaxBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = $"The template exceeds {XlsxTemplate.MaxBytes} bytes" });

        TexasClaimsLagRequest? body = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(request))
                body = JsonSerializer.Deserialize<TexasClaimsLagRequest>(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException) { }
        if (body is null) return BadRequest(new { error = "'request' must be the claims lag request as JSON" });

        XlsxTemplate workbook;
        try
        {
            using var buffer = new MemoryStream();
            await template.CopyToAsync(buffer, ct);
            workbook = XlsxTemplate.Open(buffer.ToArray());
        }
        catch (XlsxTemplateException ex)
        {
            return UnprocessableEntity(new { error = ex.Message });
        }

        var (layout, problems) = TexasClaimsLagReport.ReadLayout(workbook);
        if (layout is null)
            return UnprocessableEntity(new { error = "This is not the STAR Claims Lag Report layout CHO fills; nothing was filled", problems });

        var (lag, error) = await BuildTexasLagAsync(body, forWorkbook: true, rateCells, actor, ct);
        if (error != null) return error;

        var missing = TexasClaimsLagReport.Missing(layout, lag!);
        if (missing.Count > 0)
            return UnprocessableEntity(new { error = "The template does not cover every month the report needs; download the current version from HHSC", problems = missing });

        var bytes = TexasClaimsLagReport.Fill(workbook, layout, lag!, body.Contractor!, body.ServiceArea!,
            body.SubmissionDate ?? TexasToday(DateTime.UtcNow));
        if (lag!.Excluded.Count > 0)
            Response.Headers["X-Report-Excluded"] = string.Join("; ", lag.Excluded.Select(e => $"{e.Claims} claim(s), {e.Amount.ToString("0.00", CultureInfo.InvariantCulture)}: {e.Reason}"));
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"STAR-Claims-Lag-SFY{lag.StateFiscalYear}-Q{lag.Quarter}.xlsx");
    }

    private async Task<(TexasClaimsLag? Lag, ActionResult? Error)> BuildTexasLagAsync(
        TexasClaimsLagRequest request, bool forWorkbook, IExpectedRateCellSource rateCells, ICurrentActor actor, CancellationToken ct)
    {
        var errors = request.Validate(forWorkbook, out var groups);
        if (errors.Count > 0) return (null, BadRequest(new { error = "The claims lag request is not valid", errors }));

        var through = TexasClaimsLagReport.QuarterEnd(request.StateFiscalYear, request.Quarter);
        var from = TexasClaimsLagReport.FiscalYearStart(request.StateFiscalYear);
        if (request.IncurredFrom != null) TexasClaimsLagReport.TryMonth(request.IncurredFrom, out from);
        if (from > through || LagTriangle.MonthsBetween(from, through) >= MaxIncurredMonths)
            return (null, BadRequest(new { error = $"incurredFrom must be on or before {through:yyyy-MM} and at most {MaxIncurredMonths} months earlier" }));

        IReadOnlyList<ExpectedRateCell> cells;
        try
        {
            cells = await rateCells.GetAsync(actor.TenantId, "TX", from, through, ct);
        }
        catch (ExpectedRateCellsUnavailableException ex)
        {
            return (null, StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message }));
        }

        var versions = await _claims.GetPaymentVersionsAsync(
            from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            through.AddMonths(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            LineOfBusiness.Medicaid, ct);
        return (TexasClaimsLagReport.Build(request.StateFiscalYear, request.Quarter, from,
            LagTriangle.Increments(versions), cells, groups), null);
    }

    /// <summary>
    /// HHSC's TPL/TPR Report (UMCM 5.3.4.1) figures for an SFY quarter: per
    /// program and plan code, each month of the SFY through the quarter, the
    /// Medicaid allowable cost avoided on denied claims, other insurance
    /// credits, recoveries from carriers and providers, and the tort
    /// recoveries entered in the request. A claim's plan code is its member's
    /// rate cell in the month of service, mapped by <c>rateCellPlanCodes</c>;
    /// amounts that can't be placed are left out and counted under
    /// <c>excluded</c>.
    /// </summary>
    [HttpPost("texas/tpr")]
    [RequirePermission("finance:read")]
    public async Task<ActionResult> TexasTprFigures(
        [FromBody] TexasTprRequest request,
        [FromServices] IExpectedRateCellSource rateCells,
        [FromServices] ClaimsService.Services.Tpl.ITplRecoveryRepository recoveries,
        [FromServices] ICurrentActor actor,
        CancellationToken ct)
    {
        var (report, error) = await BuildTexasTprAsync(request, forWorkbook: false, rateCells, recoveries, actor, ct);
        return error ?? Ok(report);
    }

    /// <summary>
    /// The TPL/TPR Report workbook filled in: post HHSC's blank 5.3.4.1
    /// workbook as <c>template</c> and the request JSON as <c>request</c>.
    /// Each program sheet with plan codes gets its header, plan codes and the
    /// five lines for every month through the quarter (zero where there is
    /// nothing); totals stay HHSC's formulas. A workbook whose program sheets
    /// can't be read is refused (422).
    /// </summary>
    [HttpPost("texas/tpr/workbook")]
    [RequirePermission("finance:read")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(XlsxTemplate.MaxBytes + 1024 * 1024)]
    public async Task<ActionResult> TexasTprWorkbook(
        [FromForm] IFormFile? template,
        [FromForm] string? request,
        [FromServices] IExpectedRateCellSource rateCells,
        [FromServices] ClaimsService.Services.Tpl.ITplRecoveryRepository recoveries,
        [FromServices] ICurrentActor actor,
        CancellationToken ct)
    {
        if (template is null || template.Length == 0)
            return BadRequest(new { error = "Attach HHSC's blank TPL/TPR Report (UMCM 5.3.4.1) as 'template'" });
        if (template.Length > XlsxTemplate.MaxBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = $"The template exceeds {XlsxTemplate.MaxBytes} bytes" });

        TexasTprRequest? body = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(request))
                body = JsonSerializer.Deserialize<TexasTprRequest>(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException) { }
        if (body is null) return BadRequest(new { error = "'request' must be the TPL/TPR request as JSON" });

        XlsxTemplate workbook;
        try
        {
            using var buffer = new MemoryStream();
            await template.CopyToAsync(buffer, ct);
            workbook = XlsxTemplate.Open(buffer.ToArray());
        }
        catch (XlsxTemplateException ex)
        {
            return UnprocessableEntity(new { error = ex.Message });
        }

        var (report, error) = await BuildTexasTprAsync(body, forWorkbook: true, rateCells, recoveries, actor, ct);
        if (error != null) return error;

        var (layouts, problems) = TexasTprReport.ReadLayouts(workbook, report!);
        if (problems.Count > 0)
            return UnprocessableEntity(new { error = "This is not the TPL/TPR Report layout CHO fills; nothing was filled", problems });

        var bytes = TexasTprReport.Fill(workbook, layouts, report!, body.Contractor!,
            body.SubmissionDate ?? TexasTprReport.TexasToday(DateTime.UtcNow));
        if (report!.Excluded.Count > 0)
            Response.Headers["X-Report-Excluded"] = string.Join("; ", report.Excluded.Select(e => $"{e.Items} item(s), {e.Amount.ToString("0.00", CultureInfo.InvariantCulture)}: {e.Reason}"));
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            TexasTprReport.FileName(report, body.McoCode));
    }

    private const int MaxTprRecoveryCases = 100_000;

    private async Task<(TexasTpr? Report, ActionResult? Error)> BuildTexasTprAsync(
        TexasTprRequest request, bool forWorkbook, IExpectedRateCellSource rateCells,
        ClaimsService.Services.Tpl.ITplRecoveryRepository recoveries, ICurrentActor actor, CancellationToken ct)
    {
        var errors = request.Validate(forWorkbook, out var planCodes);
        if (errors.Count > 0) return (null, BadRequest(new { error = "The TPL/TPR request is not valid", errors }));

        var (fromUtc, beforeUtc) = TexasTprReport.Window(request.StateFiscalYear, request.Quarter);
        var claims = await _claims.GetTplReportClaimsAsync(fromUtc, beforeUtc, ct);
        var cases = await recoveries.ListAsync(actor.TenantId,
            new ClaimsService.Services.Tpl.TplRecoveryQuery(State: "TX", Limit: MaxTprRecoveryCases), ct);
        if (cases.Count >= MaxTprRecoveryCases)
            return (null, StatusCode(StatusCodes.Status422UnprocessableEntity,
                new { error = $"More than {MaxTprRecoveryCases} Texas recovery cases; the report can't read them all in one request" }));

        // Rate cells are needed for each item's month of service, which can
        // be well before the SFY: read from the earliest one, at most
        // MaxIncurredMonths back from the quarter's last month.
        var through = TexasTprReport.QuarterEnd(request.StateFiscalYear, request.Quarter);
        var serviceDates = claims.Select(c => c.ServiceDateFrom)
            .Concat(cases.Where(c => c.Events.Any(e => e.At >= fromUtc && e.At < beforeUtc)).Select(c => c.ServiceDateFrom));
        var earliest = TexasTprReport.FiscalYearStart(request.StateFiscalYear);
        foreach (var d in serviceDates)
        {
            var month = new DateOnly(d.Year, d.Month, 1);
            if (month < earliest) earliest = month;
        }
        var floor = through.AddMonths(-(MaxIncurredMonths - 1));
        if (earliest < floor) earliest = floor;

        IReadOnlyList<ExpectedRateCell> cells;
        try
        {
            cells = await rateCells.GetAsync(actor.TenantId, "TX", earliest, through, ct);
        }
        catch (ExpectedRateCellsUnavailableException ex)
        {
            return (null, StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message }));
        }

        return (TexasTprReport.Build(request.StateFiscalYear, request.Quarter, claims, cases, cells, planCodes, request.TortRecoveries), null);
    }

    private static DateOnly TexasToday(DateTime utcNow) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
            TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")));

    private static bool TryMonth(string? value, out DateOnly month) =>
        DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out month);
}

public sealed class IbnrRequest
{
    public string IncurredFrom { get; set; } = string.Empty;
    public string IncurredTo { get; set; } = string.Empty;
    public string Valuation { get; set; } = string.Empty;
    public LineOfBusiness? LineOfBusiness { get; set; }
    public int? FactorMonths { get; set; }
    public int? MaxDevelopmentLag { get; set; }
    public decimal? PmpmFallbackBelowCompletion { get; set; }
    public decimal? ExpectedPmpm { get; set; }

    /// <summary>Member-months by incurred month (yyyy-MM).</summary>
    public Dictionary<string, int>? MemberMonths { get; set; }
}

public sealed class IbnrResponse
{
    public string? LineOfBusiness { get; set; }
    public string Valuation { get; set; } = string.Empty;
    public int ClaimVersionsRead { get; set; }
    public List<TriangleRowDto> Triangle { get; set; } = new();
    public List<IbnrMonthDto> Estimate { get; set; } = new();
    public IReadOnlyList<decimal> DevelopmentFactors { get; set; } = [];
    public IReadOnlyList<decimal> CompletionFactors { get; set; } = [];
    public decimal TotalPaid { get; set; }
    public decimal TotalUltimate { get; set; }
    public decimal TotalIbnr { get; set; }
    public IReadOnlyList<string> Notes { get; set; } = [];
}

public sealed class TriangleRowDto
{
    public string IncurredMonth { get; set; } = string.Empty;
    public IReadOnlyList<decimal> CumulativePaid { get; set; } = [];
}

public sealed class IbnrMonthDto
{
    public string IncurredMonth { get; set; } = string.Empty;
    public int Lag { get; set; }
    public decimal PaidToDate { get; set; }
    public decimal CompletionFactor { get; set; }
    public decimal Ultimate { get; set; }
    public decimal Ibnr { get; set; }
    public string Method { get; set; } = string.Empty;
    public int? MemberMonths { get; set; }
    public decimal? UltimatePmpm { get; set; }
}
