using System.Text;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using PremiumBillingService.Clients;
using PremiumBillingService.StateCapitation;

namespace PremiumBillingService.Controllers;

/// <summary>
/// Expected capitation member-months built from the state's 834 instead of
/// loaded by hand: finance loads the state's rate cell crosswalk, then builds
/// the months from coverage-service spans and the rate cell inputs
/// enrollment-import-service kept from each 834
/// (docs/integration/MEDICAID-EXPECTED-MEMBER-MONTHS.md). A build is a dry run
/// unless <c>apply=true</c>; applying replaces the expected member-months of
/// every month in the window, as a CSV load does.
/// </summary>
[ApiController]
[Route("api/v1/state-capitation/{state}")]
[Produces("application/json")]
public sealed class ExpectedMemberMonthsController : ControllerBase
{
    /// <summary>Largest crosswalk file accepted.</summary>
    public const int MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>Most exceptions returned inline; the CSV has them all.</summary>
    public const int MaxInlineExceptions = 1000;

    private readonly IStateCapitationStore _store;
    private readonly ICoverageServiceClient _coverage;
    private readonly IRateCellInputSource _inputs;
    private readonly ICurrentActor _actor;
    private readonly ILogger<ExpectedMemberMonthsController> _logger;

    public ExpectedMemberMonthsController(
        IStateCapitationStore store, ICoverageServiceClient coverage, IRateCellInputSource inputs,
        ICurrentActor actor, ILogger<ExpectedMemberMonthsController> logger)
    {
        _store = store;
        _coverage = coverage;
        _inputs = inputs;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>
    /// Replace the state's rate cell crosswalk from a CSV: <c>rate_cell</c>
    /// and any of <c>plan_id, field, value, age_min, age_max, county,
    /// effective_from, effective_to</c>. <c>field</c> is
    /// <c>category:&lt;loop 2750 N1*75 name&gt;</c> or <c>hd04</c>. Every
    /// row must name a plan_id or the build has no plans to list.
    /// </summary>
    [HttpPost("rate-cell-crosswalk")]
    [RequirePermission("finance:write")]
    [Consumes("text/csv", "text/plain", "application/octet-stream")]
    public async Task<ActionResult> LoadCrosswalk(string state, [FromQuery] string? fileName = null)
    {
        if (StateCapitationController.Profile(state) is not { } profile) return UnknownState(state);
        var content = await ReadBodyAsync();
        if (content == null) return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = $"File exceeds {MaxFileBytes} bytes" });

        var rows = StateCapitationCsv.ReadCrosswalk(content, out var errors);
        if (errors.Count > 0) return BadRequest(new { error = "The crosswalk was not loaded", errors });
        if (rows.Count == 0) return BadRequest(new { error = "The crosswalk has no rows" });
        if (rows.Any(r => string.IsNullOrEmpty(r.PlanId)))
            return BadRequest(new { error = "Every crosswalk row must name a plan_id: the build lists coverage by plan" });

        var now = DateTime.UtcNow;
        var stored = rows.Select((r, i) =>
        {
            var s = StoredCrosswalkRow.From(r, i + 1);
            s.SourceFile = fileName;
            s.LoadedBy = _actor.UserId;
            s.LoadedAt = now;
            return s;
        }).ToList();
        var count = await _store.ReplaceCrosswalkAsync(profile.State, stored);
        _logger.LogInformation("Loaded a {Count}-row {State} rate cell crosswalk", count, profile.State);
        return Ok(new
        {
            state = profile.State,
            rowsLoaded = count,
            rateCells = rows.Select(r => r.RateCell).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            plans = rows.Select(r => r.PlanId!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal),
        });
    }

    /// <summary>The state's rate cell crosswalk as loaded, as CSV.</summary>
    [HttpGet("rate-cell-crosswalk.csv")]
    [RequirePermission("finance:read")]
    [Produces("text/csv")]
    public async Task<ActionResult> CrosswalkCsv(string state)
    {
        if (StateCapitationController.Profile(state) is not { } profile) return UnknownState(state);
        var rows = await _store.GetCrosswalkAsync(profile.State);
        var csv = new StringBuilder("rate_cell,plan_id,field,value,age_min,age_max,county,effective_from,effective_to\n");
        foreach (var r in rows)
            csv.Append(Csv(r.RateCell)).Append(',').Append(Csv(r.PlanId)).Append(',').Append(Csv(r.Field)).Append(',')
               .Append(Csv(r.Value)).Append(',').Append(r.AgeMin).Append(',').Append(r.AgeMax).Append(',')
               .Append(Csv(r.County)).Append(',').Append(r.EffectiveFrom?.ToString("yyyy-MM")).Append(',')
               .Append(r.EffectiveTo?.ToString("yyyy-MM")).Append('\n');
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"{profile.State.ToLowerInvariant()}-rate-cell-crosswalk.csv");
    }

    /// <summary>
    /// Build the expected member-months for <paramref name="from"/> to
    /// <paramref name="to"/> (yyyy-MM, inclusive). A dry run (the default)
    /// reports counts by rate cell and the exceptions; <c>apply=true</c> also
    /// replaces the expected member-months of every month in the window.
    /// </summary>
    [HttpPost("expected/build")]
    [RequirePermission("finance:write")]
    public async Task<ActionResult> Build(string state, [FromQuery] string from, [FromQuery] string to, [FromQuery] bool apply = false, CancellationToken ct = default)
    {
        if (StateCapitationController.Profile(state) is not { } profile) return UnknownState(state);
        if (StateCapitationController.Window(from, to) is not { } window) return BadWindow();

        var (build, error) = await RunAsync(profile.State, window.From, window.To, ct);
        if (error is not null) return error;

        var loaded = 0;
        if (apply)
        {
            var now = DateTime.UtcNow;
            var rows = build!.MemberMonths.Select(m => new StoredExpectedMonth
            {
                MemberId = m.MemberId,
                Month = m.Month.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                RateCell = m.RateCell,
                DaysEnrolled = m.DaysEnrolled,
                SourceFile = "834-build",
                LoadedBy = _actor.UserId,
                LoadedAt = now,
            }).ToList();
            loaded = await _store.ReplaceExpectedAsync(profile.State, Months(window.From, window.To), rows);
            _logger.LogInformation("Built and loaded {Count} expected {State} member-months for {From:yyyy-MM} to {To:yyyy-MM} ({Exceptions} exceptions)",
                loaded, profile.State, window.From, window.To, build.Exceptions.Count);
        }

        return Ok(new
        {
            state = profile.State,
            from = window.From.ToString("yyyy-MM"),
            to = window.To.ToString("yyyy-MM"),
            applied = apply,
            memberMonths = build!.MemberMonths.Count,
            memberMonthsLoaded = loaded,
            byMonthAndRateCell = build.MemberMonths
                .GroupBy(m => (m.Month, m.RateCell))
                .OrderBy(g => g.Key.Month).ThenBy(g => g.Key.RateCell, StringComparer.Ordinal)
                .Select(g => new { month = g.Key.Month.ToString("yyyy-MM"), rateCell = g.Key.RateCell, members = g.Count() }),
            exceptionCount = build.Exceptions.Count,
            exceptionsByIssue = build.Exceptions.GroupBy(e => e.Issue).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            exceptions = build.Exceptions.Take(MaxInlineExceptions).Select(e => new
            {
                e.MemberId, month = e.Month.ToString("yyyy-MM"), e.PlanId, issue = e.Issue.ToString(), e.Detail
            }),
        });
    }

    /// <summary>Every exception of a build for the window, as CSV (the build is not applied).</summary>
    [HttpGet("expected/build/exceptions.csv")]
    [RequirePermission("finance:read")]
    [Produces("text/csv")]
    public async Task<ActionResult> ExceptionsCsv(string state, [FromQuery] string from, [FromQuery] string to, CancellationToken ct = default)
    {
        if (StateCapitationController.Profile(state) is not { } profile) return UnknownState(state);
        if (StateCapitationController.Window(from, to) is not { } window) return BadWindow();

        var (build, error) = await RunAsync(profile.State, window.From, window.To, ct);
        if (error is not null) return error;
        var csv = new StringBuilder("member_id,month,plan_id,issue,detail\n");
        foreach (var e in build!.Exceptions)
            csv.Append(Csv(e.MemberId)).Append(',').Append(e.Month.ToString("yyyy-MM")).Append(',').Append(Csv(e.PlanId)).Append(',')
               .Append(e.Issue).Append(',').Append(Csv(e.Detail)).Append('\n');
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv",
            $"{profile.State.ToLowerInvariant()}-member-month-exceptions-{window.From:yyyy-MM}-{window.To:yyyy-MM}.csv");
    }

    private async Task<(ExpectedMemberMonthBuild? Build, ActionResult? Error)> RunAsync(string state, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var crosswalk = await _store.GetCrosswalkAsync(state);
        if (crosswalk.Count == 0)
            return (null, Conflict(new { error = $"No rate cell crosswalk is loaded for {state}; POST rate-cell-crosswalk first" }));

        var tenant = _actor.TenantId;
        var spans = new List<PlanCoverageSpan>();
        IReadOnlyDictionary<string, IReadOnlyList<RateCellInputSnapshot>> inputs;
        try
        {
            foreach (var plan in crosswalk.Select(r => r.PlanId!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var c in await _coverage.GetAllCoveragesByPlanAsync(tenant, plan, ct))
                {
                    if (string.IsNullOrEmpty(c.MemberId)) continue;
                    var spanFrom = DateOnly.FromDateTime(c.EffectiveDate.Date);
                    DateOnly? spanTo = c.TerminationDate is { } t ? DateOnly.FromDateTime(t.Date) : null;
                    if (spanTo < spanFrom) continue; // voided (terminated before it began)
                    spans.Add(new PlanCoverageSpan(c.MemberId, c.PlanId ?? plan, spanFrom, spanTo));
                }
            }
            inputs = await _inputs.GetAllAsync(tenant, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Expected member-month build for {State} could not read coverage or 834 inputs", state);
            return (null, StatusCode(StatusCodes.Status502BadGateway, new { error = "Coverage or 834 rate cell inputs could not be read; nothing was built", detail = ex.Message }));
        }

        return (ExpectedMemberMonthBuilder.Build(from, to, spans, inputs, crosswalk), null);
    }

    private static List<DateOnly> Months(DateOnly from, DateOnly to)
    {
        var months = new List<DateOnly>();
        for (var m = from; m <= to; m = m.AddMonths(1)) months.Add(m);
        return months;
    }

    private static string Csv(string? value) => StateCapitationController.Csv(value);

    private NotFoundObjectResult UnknownState(string state) =>
        NotFound(new { error = $"No state capitation profile for '{state}'. Supported: TX, CA." });

    private BadRequestObjectResult BadWindow() =>
        BadRequest(new { error = $"from and to must be months (yyyy-MM), from not after to, at most {StateCapitationController.MaxMonths} months" });

    private async Task<string?> ReadBodyAsync()
    {
        if (Request.ContentLength > MaxFileBytes) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, HttpContext.RequestAborted)) > 0)
        {
            if (buffer.Length + read > MaxFileBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
