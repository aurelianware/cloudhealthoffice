using FhirService.Services.ApiUsage;
using FhirService.Services.Identity;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// The figures a plan files each year for CMS-0057-F API usage (Q-20): for
/// the Patient Access API, the unique patients whose data went to an app and
/// those whose data went more than once, for one calendar year. The plan
/// submits them (HPMS, its state, or MPMS); CHO computes them from the
/// <see cref="IApiDisclosureLedger"/>.
///
/// <c>GET api/v1/interoperability/usage?year=2026&amp;api=PatientAccess</c>
///
/// Counts are for the caller's tenant only. A year is complete only once it has
/// ended and the ledger covers it from 1 January; otherwise the report says
/// from which day it counts.
/// </summary>
[ApiController]
[Route("api/v1/interoperability/usage")]
[FhirAccess(smart: false, cho: "compliance:read")]
public sealed class ApiUsageReportController : FhirControllerBase
{
    private readonly IApiDisclosureLedger _ledger;

    public ApiUsageReportController(IApiDisclosureLedger ledger) => _ledger = ledger;

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int year, [FromQuery] InteroperabilityApi api = InteroperabilityApi.PatientAccess, CancellationToken ct = default)
    {
        if (year < 2025 || year > DateTime.UtcNow.Year)
            return BadRequest(new { error = $"year must be between 2025 and {DateTime.UtcNow.Year}." });

        var from = new DateOnly(year, 1, 1);
        var to = from.AddYears(1);
        var counts = await _ledger.CountAsync(TenantId, api, from, to, ct);
        var earliest = await _ledger.EarliestDayAsync(TenantId, api, ct);
        var coversFrom = earliest is { } first && first > from ? first : from;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        return Ok(new ApiUsageReport(
            TenantId,
            api.ToString(),
            year,
            from,
            to.AddDays(-1),
            counts.UniquePatients,
            counts.PatientsMoreThanOnce,
            Complete: earliest is { } start && start <= from && to <= today,
            RecordedFrom: earliest is null ? null : coversFrom,
            Definition: "A transfer is one or more successful reads of the patient's data by one app on one UTC day. "
                        + "patientsMoreThanOnce counts patients with more than one transfer in the year."));
    }
}

public sealed record ApiUsageReport(
    string TenantId,
    string Api,
    int Year,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    long UniquePatients,
    long PatientsMoreThanOnce,
    bool Complete,
    DateOnly? RecordedFrom,
    string Definition);
