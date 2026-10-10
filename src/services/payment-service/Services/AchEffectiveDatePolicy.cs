using Microsoft.Extensions.Options;

namespace PaymentService.Services;

/// <summary>
/// The ACH effective entry date rules, in the bank's time zone
/// (<c>BankTransmission:BankTimeZone</c>):
/// <list type="bullet">
/// <item>The earliest date a file may carry is the next banking day after today,
/// or today when <c>BankTransmission:AllowSameDayEffectiveDate</c> and today is a
/// banking day.</item>
/// <item>A file's date is chosen when it is pinned: the first banking day on or
/// after the later of the run's explicitly requested payment date and the earliest
/// date. A weekend or holiday is rolled forward, never refused.</item>
/// <item>At send time only a date that is no longer acceptable (past, or today
/// without same-day) is refused; such a file can be re-dated.</item>
/// </list>
/// </summary>
public sealed class AchEffectiveDatePolicy
{
    private readonly BankTransmissionOptions _options;
    private readonly TimeProvider _clock;

    public AchEffectiveDatePolicy(IOptions<BankTransmissionOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    /// <summary>The bank's calendar date now.</summary>
    public DateTime BankToday()
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(_options.BankTimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // No tz database: Eastern Standard Time, the earlier of the two offsets
            // (never later than the bank's real date).
            zone = TimeZoneInfo.CreateCustomTimeZone("cho-bank-fallback", TimeSpan.FromHours(-5), "bank", "bank");
        }
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime, zone).Date, DateTimeKind.Utc);
    }

    /// <summary>The earliest effective entry date a file may carry if sent now.</summary>
    public DateTime Earliest()
    {
        var today = BankToday();
        return _options.AllowSameDayEffectiveDate && AchBankingCalendar.IsBankingDay(today) ? today : NextBankingDayOnOrAfter(today.AddDays(1));
    }

    /// <summary>The date a file pinned now carries: the first banking day on or after max(requested, earliest).</summary>
    public DateTime Choose(DateTime? requested)
    {
        var earliest = Earliest();
        var start = requested.HasValue && requested.Value.Date > earliest ? requested.Value.Date : earliest;
        return DateTime.SpecifyKind(NextBankingDayOnOrAfter(start), DateTimeKind.Utc);
    }

    /// <summary>Null when a file carrying <paramref name="effective"/> may be sent now; otherwise why not.</summary>
    public string? SendProblem(DateTime effective)
    {
        var today = BankToday();
        var date = effective.Date;
        if (date < today || (date == today && !_options.AllowSameDayEffectiveDate))
            return $"its effective entry date {date:yyyy-MM-dd} is not after today ({today:yyyy-MM-dd} at the bank)";
        return null;
    }

    /// <summary>Null when <paramref name="requested"/> is acceptable as a run's payment date; otherwise why not.</summary>
    public string? RequestedDateProblem(DateTime requested)
    {
        var today = BankToday();
        if (requested.Date < today)
            return $"the payment date {requested:yyyy-MM-dd} is in the past (today is {today:yyyy-MM-dd} at the bank)";
        if (requested.Date > today.AddDays(365))
            return $"the payment date {requested:yyyy-MM-dd} is more than a year ahead";
        return null;
    }

    public static DateTime NextBankingDayOnOrAfter(DateTime date)
    {
        var d = date.Date;
        while (!AchBankingCalendar.IsBankingDay(d))
            d = d.AddDays(1);
        return d;
    }
}
