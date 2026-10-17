using BenefitPlanService.Middleware;
using BenefitPlanService.Repositories;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using CloudHealthOffice.Infrastructure.Logging;

namespace BenefitPlanService.Controllers;

/// <summary>
/// A benefit plan's default fee schedule: the flat-rate schedule the Fee
/// Schedule Engine prices a line from when the provider has no contracted
/// schedule (<c>RateResolutionService</c> → <c>GetDefaultForPlanAsync</c>).
/// Without one, an uncontracted line falls back to billed charges and the
/// claims pipeline pends it NOCONTRACT.
///
/// <para>
/// One default schedule per tenant + plan: <c>PUT</c> replaces it (an
/// idempotent upsert keyed by <c>{tenant}:plan-default:{planId}</c>).
/// Lines are flat rates per unit, keyed by procedure code, or by revenue
/// code with a blank procedure code for revenue-code-only 837I lines.
/// Percent-of-Medicare, RVU, DRG and per-diem schedules are out of scope
/// for this endpoint.
/// </para>
///
/// <para>
/// Tenant comes from the token (shared TenantMiddleware); the plan must
/// exist in that tenant. Writing is tenant configuration, so it requires
/// <c>settings:manage</c> (the service's default write permission, held by
/// TenantAdmin); reading requires <c>benefits:read</c>.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/plans/{planId:guid}/default-fee-schedule")]
public sealed class PlanDefaultFeeScheduleController : ControllerBase
{
    /// <summary>Upper bound on lines per schedule (keeps one document well under store limits).</summary>
    public const int MaxLines = 5_000;

    private readonly IFeeScheduleRepository _schedules;
    private readonly IBenefitPlanRepository _plans;
    private readonly ILogger<PlanDefaultFeeScheduleController> _logger;

    public PlanDefaultFeeScheduleController(
        IFeeScheduleRepository schedules,
        IBenefitPlanRepository plans,
        ILogger<PlanDefaultFeeScheduleController> logger)
    {
        _schedules = schedules;
        _plans = plans;
        _logger = logger;
    }

    /// <summary>Deterministic id of a plan's default schedule.</summary>
    public static string ScheduleId(string tenantId, Guid planId) => $"{tenantId}:plan-default:{planId:D}";

    /// <summary>The plan's default fee schedule, or 404 when none is configured.</summary>
    [HttpGet]
    [RequirePermission("benefits:read")]
    [ProducesResponseType(typeof(FeeSchedule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FeeSchedule>> Get(Guid planId, CancellationToken ct)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
            return BadRequest(new { error = "tenant_required" });

        var schedule = await _schedules.GetByIdAsync(tenantId, ScheduleId(tenantId, planId), ct);
        return schedule is null ? NotFound() : Ok(schedule);
    }

    /// <summary>Create or replace the plan's default fee schedule.</summary>
    [HttpPut]
    [RequirePermission("settings:manage")]
    [ProducesResponseType(typeof(FeeSchedule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FeeSchedule>> Put(
        Guid planId,
        [FromBody] PlanDefaultFeeScheduleRequest request,
        CancellationToken ct)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
            return BadRequest(new { error = "tenant_required" });

        var errors = Validate(request);
        if (errors.Count > 0)
            return BadRequest(new { error = "invalid_fee_schedule", details = errors });

        var plan = await _plans.GetByIdAsync(planId.ToString("D"), tenantId);
        if (plan is null)
            return NotFound(new { error = "plan_not_found", planId });

        var id = ScheduleId(tenantId, planId);
        var existing = await _schedules.GetByIdAsync(tenantId, id, ct);

        var schedule = new FeeSchedule
        {
            Id = id,
            TenantId = tenantId,
            Name = request.Name!.Trim(),
            Type = FeeScheduleType.Custom,
            SourceType = FeeScheduleSourceType.PayerContract,
            SourceId = "plan-default",
            DefaultForPlanId = planId.ToString("D"),
            EffectiveDate = DateTime.SpecifyKind(request.EffectiveDate!.Value.Date, DateTimeKind.Utc),
            TermDate = request.TermDate is { } term ? DateTime.SpecifyKind(term.Date, DateTimeKind.Utc) : null,
            CreatedDate = existing?.CreatedDate ?? DateTime.UtcNow,
            // Replaces the schedule as read just now (0 = create): a concurrent
            // PUT in between, or a create racing another create, answers 409
            // instead of being silently overwritten.
            Revision = existing?.Revision ?? 0,
            Lines = request.Lines!.Select(l => new FeeScheduleLine
            {
                ProcedureCode = l.ProcedureCode?.Trim() ?? string.Empty,
                RevenueCode = string.IsNullOrWhiteSpace(l.RevenueCode) ? null : l.RevenueCode.Trim().PadLeft(4, '0'),
                Modifier = string.IsNullOrWhiteSpace(l.Modifier) ? null : l.Modifier.Trim(),
                RateType = FeeScheduleRateType.FlatRate,
                Rate = l.Rate,
                FacilityRate = l.FacilityRate,
                MultipleProcedureIndicator = l.MultipleProcedureIndicator is { } mpi
                    ? (MultipleProcedureIndicator)mpi
                    : null,
            }).ToList(),
        };

        var saved = await _schedules.UpsertAsync(schedule, ct);

        _logger.LogInformation(
            "plan default fee schedule {Action} tenant={Tenant} plan={Plan} lines={Lines}",
            existing is null ? "created" : "replaced",
            LogSanitizer.Sanitize(tenantId, 256), planId, schedule.Lines.Count);

        return Ok(saved);
    }

    internal static List<string> Validate(PlanDefaultFeeScheduleRequest? request)
    {
        var errors = new List<string>();
        if (request is null)
        {
            errors.Add("body is required");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(request.Name)) errors.Add("name is required");
        if (request.EffectiveDate is null) errors.Add("effectiveDate is required");
        if (request.TermDate is { } term && request.EffectiveDate is { } eff && term.Date <= eff.Date)
            errors.Add("termDate must be after effectiveDate");

        if (request.Lines is null || request.Lines.Count == 0)
        {
            errors.Add("at least one line is required");
            return errors;
        }

        if (request.Lines.Count > MaxLines)
        {
            errors.Add($"at most {MaxLines} lines are allowed");
            return errors;
        }

        var keys = new HashSet<(string, string, string)>();
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            if (line is null)
            {
                errors.Add($"lines[{i}] is null");
                continue;
            }

            var procedure = line.ProcedureCode?.Trim() ?? string.Empty;
            var revenue = string.IsNullOrWhiteSpace(line.RevenueCode) ? string.Empty : line.RevenueCode.Trim().PadLeft(4, '0');
            if (procedure.Length == 0 && revenue.Length == 0)
                errors.Add($"lines[{i}]: procedureCode or revenueCode is required");
            if (revenue.Length > 0 && (revenue.Length != 4 || !revenue.All(char.IsDigit)))
                errors.Add($"lines[{i}]: revenueCode must be a 3- or 4-digit UB-04 revenue code");
            if (line.Rate < 0m || line.FacilityRate < 0m)
                errors.Add($"lines[{i}]: rates must not be negative");
            if (line.MultipleProcedureIndicator is { } mpi
                && (mpi is < 0 or > byte.MaxValue
                    || !Enum.IsDefined(typeof(MultipleProcedureIndicator), (MultipleProcedureIndicator)mpi)))
                errors.Add($"lines[{i}]: unknown multipleProcedureIndicator {mpi}");

            // A revenue code only distinguishes a revenue-code line; on a procedure line
            // the engine matches by procedure code + modifier alone.
            var key = (procedure.ToUpperInvariant(),
                       procedure.Length == 0 ? revenue : string.Empty,
                       line.Modifier?.Trim().ToUpperInvariant() ?? string.Empty);
            if (!keys.Add(key))
                errors.Add($"lines[{i}]: duplicate rate for {(procedure.Length > 0 ? procedure : "rev " + revenue)}" +
                           (key.Item3.Length > 0 ? $" modifier {key.Item3}" : string.Empty));
        }

        return errors;
    }
}

/// <summary>Body of <c>PUT /api/v1/plans/{planId}/default-fee-schedule</c>.</summary>
public sealed class PlanDefaultFeeScheduleRequest
{
    public string? Name { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public DateTime? TermDate { get; set; }
    public List<PlanDefaultFeeScheduleLineRequest>? Lines { get; set; }
}

/// <summary>One flat-rate line: dollars per unit.</summary>
public sealed class PlanDefaultFeeScheduleLineRequest
{
    /// <summary>CPT/HCPCS/CDT code; blank for a revenue-code line.</summary>
    public string? ProcedureCode { get; set; }

    /// <summary>UB-04 revenue code; with a blank procedure code it prices revenue-code-only 837I lines.</summary>
    public string? RevenueCode { get; set; }

    public string? Modifier { get; set; }

    /// <summary>Flat dollar rate per unit.</summary>
    public decimal Rate { get; set; }

    /// <summary>Optional facility-setting rate (see FeeScheduleLine.FacilityRate).</summary>
    public decimal? FacilityRate { get; set; }

    /// <summary>CMS MPFS multiple procedure indicator (0–9); null = unknown.</summary>
    public int? MultipleProcedureIndicator { get; set; }
}
