using System.Globalization;
using System.Text;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using PremiumBillingService.Edi;
using PremiumBillingService.StateCapitation;

namespace PremiumBillingService.Controllers;

/// <summary>
/// State Medicaid capitation revenue: load the state's rate sheet and the
/// plan's expected member-months, record the state's 820 payments, and
/// reconcile expected against received by member, month and rate cell.
///
/// <para>
/// <c>{state}</c> is a state with a capitation profile (TX, CA). Loading
/// needs finance:write; reading the reconciliation needs finance:read. State
/// 820s posted here are kept apart from group premium remittances
/// (<c>/api/v1/remittances/820</c>): they are not cash-applied to invoices.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/state-capitation/{state}")]
[Produces("application/json")]
public sealed class StateCapitationController : ControllerBase
{
    /// <summary>Largest file accepted (20 MB: a large plan's monthly roster).</summary>
    public const int MaxFileBytes = 20 * 1024 * 1024;

    /// <summary>Longest reconciliation window, in months.</summary>
    public const int MaxMonths = 36;

    /// <summary>Most member-month exceptions returned inline; the CSV has them all.</summary>
    public const int MaxInlineExceptions = 1000;

    private readonly IStateCapitationStore _store;
    private readonly ICurrentActor _actor;
    private readonly ILogger<StateCapitationController> _logger;

    public StateCapitationController(IStateCapitationStore store, ICurrentActor actor, ILogger<StateCapitationController> logger)
    {
        _store = store;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>Load (add or replace) state PMPM rates from a CSV: rate_cell, effective_from, effective_to, pmpm.</summary>
    [HttpPost("rates")]
    [RequirePermission("finance:write")]
    [Consumes("text/csv", "text/plain", "application/octet-stream")]
    public async Task<ActionResult> LoadRates(string state, [FromQuery] string? fileName = null)
    {
        if (Profile(state) is not { } profile) return UnknownState(state);
        var content = await ReadBodyAsync();
        if (content == null) return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = $"File exceeds {MaxFileBytes} bytes" });

        var rates = StateCapitationCsv.ReadRates(content, out var errors);
        if (errors.Count > 0) return BadRequest(new { error = "No rates were loaded", errors });
        var now = DateTime.UtcNow;
        foreach (var r in rates)
        {
            r.SourceFile = fileName;
            r.LoadedBy = _actor.UserId;
            r.LoadedAt = now;
        }
        var count = await _store.UpsertRatesAsync(profile.State, rates);
        _logger.LogInformation("Loaded {Count} {State} capitation rates", count, profile.State);
        return Ok(new { state = profile.State, ratesLoaded = count });
    }

    /// <summary>
    /// Load the plan's expected member-months from a CSV: member_id, month,
    /// rate_cell, optional days_enrolled. Every month named in the file is
    /// replaced whole.
    /// </summary>
    [HttpPost("expected")]
    [RequirePermission("finance:write")]
    [Consumes("text/csv", "text/plain", "application/octet-stream")]
    public async Task<ActionResult> LoadExpected(string state, [FromQuery] string? fileName = null)
    {
        if (Profile(state) is not { } profile) return UnknownState(state);
        var content = await ReadBodyAsync();
        if (content == null) return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = $"File exceeds {MaxFileBytes} bytes" });

        var rows = StateCapitationCsv.ReadExpected(content, out var errors);
        if (errors.Count > 0) return BadRequest(new { error = "No member-months were loaded", errors });
        var now = DateTime.UtcNow;
        foreach (var r in rows)
        {
            r.SourceFile = fileName;
            r.LoadedBy = _actor.UserId;
            r.LoadedAt = now;
        }
        var months = rows.Select(r => DateOnly.FromDateTime(r.Month)).Distinct().OrderBy(m => m).ToList();
        var count = await _store.ReplaceExpectedAsync(profile.State, months, rows);
        _logger.LogInformation("Loaded {Count} expected {State} member-months for {Months} month(s)", count, profile.State, months.Count);
        return Ok(new { state = profile.State, memberMonthsLoaded = count, months = months.Select(m => m.ToString("yyyy-MM")) });
    }

    /// <summary>Record the state's X12 820 payment file. A trace number already recorded is reported, not counted twice.</summary>
    [HttpPost("820")]
    [RequirePermission("finance:write")]
    [Consumes("text/plain", "application/edi-x12", "application/octet-stream")]
    public async Task<ActionResult> Record820(string state, [FromQuery] string? fileName = null)
    {
        if (Profile(state) is not { } profile) return UnknownState(state);
        var content = await ReadBodyAsync();
        if (content == null) return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = $"File exceeds {MaxFileBytes} bytes" });

        IReadOnlyList<Models.RemittanceAdvice> advices;
        try
        {
            advices = Edi820Parser.Parse(content, fileName);
        }
        catch (FormatException ex)
        {
            return BadRequest(new { error = "The 820 could not be read; nothing was recorded", detail = ex.Message });
        }

        var recorded = new List<string>();
        var duplicates = new List<string>();
        var now = DateTime.UtcNow;
        foreach (var advice in advices)
        {
            var added = await _store.TryAddPaymentAsync(new StoredStatePayment
            {
                State = profile.State,
                TraceNumber = advice.TraceNumber,
                PaymentDate = advice.PaymentDate,
                PaymentAmount = advice.PaymentAmount,
                SourceFileName = fileName,
                ReceivedBy = _actor.UserId,
                ReceivedAt = now,
                Advice = advice
            });
            (added ? recorded : duplicates).Add(advice.TraceNumber);
        }
        _logger.LogInformation("{State} capitation 820: {Recorded} payment(s) recorded, {Duplicates} duplicate(s)",
            profile.State, recorded.Count, duplicates.Count);
        return Ok(new
        {
            state = profile.State,
            recorded,
            duplicates,
            memberLines = advices.Sum(a => a.Items.Count(i => i.Level == Models.RemittanceLevel.Individual)),
            warnings = advices.SelectMany(a => a.Warnings)
        });
    }

    /// <summary>Expected against received for the months <paramref name="from"/> to <paramref name="to"/> (yyyy-MM, inclusive).</summary>
    [HttpGet("reconciliation")]
    [RequirePermission("finance:read")]
    public async Task<ActionResult> Reconciliation(string state, [FromQuery] string from, [FromQuery] string to)
    {
        if (Profile(state) is not { } profile) return UnknownState(state);
        if (Window(from, to) is not { } window) return BadWindow();

        var result = await RunAsync(profile, window.From, window.To);
        var exceptions = result.Exceptions.ToList();
        return Ok(new
        {
            state = profile.State,
            program = profile.Program,
            profileVerified = profile.Verified,
            from = window.From.ToString("yyyy-MM"),
            to = window.To.ToString("yyyy-MM"),
            totalExpected = result.TotalExpected,
            totalReceived = result.TotalReceived,
            variance = result.TotalReceived - result.TotalExpected,
            memberMonths = result.MemberMonths.Count,
            outcomes = result.MemberMonths.GroupBy(m => m.Outcome).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            summary = result.Summary,
            exceptionCount = exceptions.Count,
            exceptions = exceptions.Take(MaxInlineExceptions).Select(Row),
            warnings = result.Warnings
        });
    }

    /// <summary>Every member-month of the window as CSV, for the finance workpaper and state disputes.</summary>
    [HttpGet("reconciliation.csv")]
    [RequirePermission("finance:read")]
    [Produces("text/csv")]
    public async Task<ActionResult> ReconciliationCsv(string state, [FromQuery] string from, [FromQuery] string to)
    {
        if (Profile(state) is not { } profile) return UnknownState(state);
        if (Window(from, to) is not { } window) return BadWindow();

        var result = await RunAsync(profile, window.From, window.To);
        var csv = new StringBuilder("member_id,month,expected_rate_cell,received_rate_cell,expected,received,variance,outcome,trace_numbers,adjustment_reasons\n");
        foreach (var m in result.MemberMonths)
        {
            csv.Append(Csv(m.MemberId)).Append(',')
               .Append(m.Month.ToString("yyyy-MM")).Append(',')
               .Append(Csv(m.ExpectedRateCell)).Append(',')
               .Append(Csv(m.ReceivedRateCell)).Append(',')
               .Append(m.Expected?.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
               .Append(m.Received.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
               .Append(m.Variance?.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
               .Append(m.Outcome).Append(',')
               .Append(Csv(string.Join(' ', m.TraceNumbers))).Append(',')
               .Append(Csv(string.Join(' ', m.AdjustmentReasons))).Append('\n');
        }
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv",
            $"{profile.State.ToLowerInvariant()}-capitation-{window.From:yyyy-MM}-{window.To:yyyy-MM}.csv");
    }

    private async Task<StateCapitationReconciliation> RunAsync(StateCapitationProfile profile, DateOnly from, DateOnly to)
    {
        var expected = await _store.GetExpectedAsync(profile.State, from, to);
        var rates = await _store.GetRatesAsync(profile.State);
        var warnings = new List<string>();
        var received = StateCapitationReconciler.ReadReceived(profile, await _store.GetPaymentsAsync(profile.State), warnings)
            .Where(r => r.Month >= from && r.Month <= to)
            .ToList();
        var result = StateCapitationReconciler.Reconcile(profile, expected, rates, received);
        return result with { Warnings = warnings.Concat(result.Warnings).ToList() };
    }

    private static object Row(MemberMonthReconciliation m) => new
    {
        m.MemberId,
        month = m.Month.ToString("yyyy-MM"),
        m.ExpectedRateCell,
        m.ReceivedRateCell,
        m.Expected,
        m.Received,
        m.Variance,
        outcome = m.Outcome.ToString(),
        m.TraceNumbers,
        m.AdjustmentReasons
    };

    /// <summary>A CSV field: formula-leading characters are neutralized and commas or quotes are quoted.</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static StateCapitationProfile? Profile(string state) => StateCapitationProfile.ForState(state);

    private NotFoundObjectResult UnknownState(string state) =>
        NotFound(new { error = $"No state capitation profile for '{state}'. Supported: TX, CA." });

    private BadRequestObjectResult BadWindow() =>
        BadRequest(new { error = $"from and to must be months (yyyy-MM), from not after to, at most {MaxMonths} months" });

    private static (DateOnly From, DateOnly To)? Window(string? from, string? to)
    {
        if (!DateOnly.TryParseExact(from + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)
            || !DateOnly.TryParseExact(to + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            || f > t
            || (t.Year - f.Year) * 12 + t.Month - f.Month >= MaxMonths)
            return null;
        return (f, t);
    }

    private async Task<string?> ReadBodyAsync()
    {
        if (Request.ContentLength > MaxFileBytes)
            return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, HttpContext.RequestAborted)) > 0)
        {
            if (buffer.Length + read > MaxFileBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
