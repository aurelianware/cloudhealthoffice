using System.Globalization;
using ClaimsService.Finance;
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
