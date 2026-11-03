using System.Globalization;
using CloudHealthOffice.Infrastructure.Money;

namespace CloudHealthOffice.FeeScheduleEngine.Medicaid;

// ═══════════════════════════════════════════════════════════════════
// PSYCHIATRIC HOSPITAL PER DIEM (Medicaid managed care)
//
// Texas pays freestanding psychiatric hospitals a per diem per hospital,
// published each state fiscal year by HHSC Provider Finance
// ("SFY 2027 Psychiatric Hospital Rates"): private freestanding facilities
// and the HHSC state hospitals, one rate per TPI.
//
// These hospitals are institutions for mental diseases (IMDs). Medicaid
// covers them for clients under 21 and 65 or older. For adults 21 to 64 a
// managed care plan may cover an IMD stay "in lieu of" other services only
// for up to 15 days in a calendar month (42 CFR 438.6(e)); a longer stay
// costs the state its federal match for that month's capitation. Whether
// the plan then still pays the hospital is the plan's call, so by default
// a stay over the limit pends rather than pays or denies.
// ═══════════════════════════════════════════════════════════════════

/// <summary>What happens to an adult IMD stay over the monthly day limit.</summary>
public enum ImdOverLimitAction
{
    /// <summary>The claim pends for review (default).</summary>
    Pend,

    /// <summary>The claim pays, with a step noting the limit was exceeded.</summary>
    Pay,
}

/// <summary>A psychiatric hospital's per diem for a period.</summary>
public sealed record PsychiatricPerDiemRate
{
    public required string Npi { get; init; }
    public required decimal DailyRate { get; init; }
    public required DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }

    /// <summary>Where the rate came from, for the calculation steps.</summary>
    public string? Source { get; init; }
}

/// <summary>One state's psychiatric hospital rules.</summary>
public sealed record PsychiatricPerDiemPolicy
{
    public required string State { get; init; }

    /// <summary>Youngest age the IMD day limit applies to (age at admission).</summary>
    public int ImdLimitMinimumAge { get; init; } = 21;

    /// <summary>Oldest age the IMD day limit applies to (age at admission).</summary>
    public int ImdLimitMaximumAge { get; init; } = 64;

    /// <summary>
    /// Most IMD days in one calendar month for an adult in the limit's age
    /// range (42 CFR 438.6(e): 15). Null: no limit is checked.
    /// </summary>
    public int? ImdMaxDaysPerMonth { get; init; } = 15;

    public ImdOverLimitAction OverLimitAction { get; init; } = ImdOverLimitAction.Pend;

    /// <summary>
    /// Texas: the HHSC per diem covers the hospital's services for the day,
    /// so ancillary lines on the claim are allowed at $0. Off: an ancillary
    /// line leaves the claim unpriced.
    /// </summary>
    public bool AncillariesIncludedInPerDiem { get; init; } = true;

    /// <summary>Texas: HHSC per diem, 15-day IMD limit for adults 21-64, pend over the limit.</summary>
    public static PsychiatricPerDiemPolicy Texas() => new() { State = "TX" };
}

/// <summary>One line of a psychiatric hospital claim.</summary>
public sealed record PsychiatricStayLine
{
    public required int LineNumber { get; init; }
    public required string RevenueCode { get; init; }

    /// <summary>First day the line covers.</summary>
    public required DateOnly ServiceDate { get; init; }

    /// <summary>Days (units) on an accommodation line; ignored on ancillaries.</summary>
    public int Days { get; init; }

    public decimal ChargeAmount { get; init; }
}

/// <summary>A psychiatric hospital claim.</summary>
public sealed record PsychiatricStayClaim
{
    public required string FacilityNpi { get; init; }

    /// <summary>The patient's age on the admission date. Null when unknown.</summary>
    public int? AgeAtAdmission { get; init; }

    public required IReadOnlyList<PsychiatricStayLine> Lines { get; init; }

    /// <summary>
    /// IMD days already paid for this member on other claims, by the first
    /// day of the month. The limit is per month, not per claim.
    /// </summary>
    public IReadOnlyDictionary<DateOnly, int> ImdDaysAlreadyPaid { get; init; } = new Dictionary<DateOnly, int>();
}

/// <summary>The result for one line.</summary>
/// <param name="DailyRate">The per diem on the line's last day (a line crossing a rate change is paid at each).</param>
public sealed record PsychiatricLinePrice(int LineNumber, int PaidDays, decimal DailyRate, decimal Allowed);

/// <summary>A priced psychiatric hospital claim.</summary>
public sealed record PsychiatricStayPrice
{
    public required bool Priced { get; init; }
    public string? UnpricedReason { get; init; }
    public IReadOnlyList<PsychiatricLinePrice> Lines { get; init; } = [];

    /// <summary>IMD days this claim adds by month, to add to the running total for later claims.</summary>
    public IReadOnlyDictionary<DateOnly, int> ImdDaysByMonth { get; init; } = new Dictionary<DateOnly, int>();

    public IReadOnlyList<string> Steps { get; init; } = [];

    public decimal TotalAllowed => Lines.Sum(l => l.Allowed);

    internal static PsychiatricStayPrice Unpriced(string reason, List<string> steps)
    {
        steps.Add(reason);
        return new PsychiatricStayPrice { Priced = false, UnpricedReason = reason, Steps = steps };
    }
}

/// <summary>
/// Prices a psychiatric hospital claim: per diem × days on the accommodation
/// lines (revenue codes 0100-0219, leave 0180-0189 excluded), ancillaries
/// included in the per diem, and the adult IMD monthly day limit.
/// Pure; never throws for missing data.
/// </summary>
public sealed class PsychiatricPerDiemPricer(PsychiatricPerDiemPolicy policy)
{
    private readonly PsychiatricPerDiemPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));

    public PsychiatricStayPrice Price(PsychiatricStayClaim claim, IReadOnlyList<PsychiatricPerDiemRate> rates)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(rates);

        var steps = new List<string>();

        if (claim.Lines.Count == 0)
            return PsychiatricStayPrice.Unpriced("The claim has no lines.", steps);
        if (claim.Lines.Select(l => l.LineNumber).Distinct().Count() != claim.Lines.Count)
            return PsychiatricStayPrice.Unpriced("The claim has duplicate line numbers.", steps);

        var results = new List<PsychiatricLinePrice>();
        var daysByMonth = new Dictionary<DateOnly, int>();
        foreach (var line in claim.Lines.OrderBy(l => l.ServiceDate).ThenBy(l => l.LineNumber))
        {
            var rev = line.RevenueCode.Trim().PadLeft(4, '0');
            if (IsLeave(rev))
                return PsychiatricStayPrice.Unpriced($"Line {line.LineNumber} is leave (revenue code {rev}); {_policy.State} psychiatric per diems have no leave rule configured.", steps);

            if (!IsAccommodation(rev))
            {
                if (!_policy.AncillariesIncludedInPerDiem)
                    return PsychiatricStayPrice.Unpriced($"Line {line.LineNumber} (revenue code {rev}) is not a hospital day; price ancillaries separately.", steps);
                results.Add(new PsychiatricLinePrice(line.LineNumber, 0, 0m, 0m));
                steps.Add($"Line {line.LineNumber}: revenue code {rev} is included in the per diem, $0.00.");
                continue;
            }

            if (line.Days <= 0)
                return PsychiatricStayPrice.Unpriced($"Line {line.LineNumber} has no days.", steps);

            var lastDay = line.ServiceDate.AddDays(line.Days - 1);

            // Each day at the rate in force that day, so a line that crosses
            // a new fiscal year's rate pays each part at its own rate.
            var parts = new List<(PsychiatricPerDiemRate Rate, int Days)>();
            for (var d = line.ServiceDate; d <= lastDay; d = d.AddDays(1))
            {
                var day = d;
                var rate = rates
                    .Where(r => string.Equals(r.Npi, claim.FacilityNpi, StringComparison.Ordinal)
                        && r.EffectiveFrom <= day && (r.EffectiveTo is null || r.EffectiveTo >= day))
                    .OrderByDescending(r => r.EffectiveFrom)
                    .FirstOrDefault();
                if (rate is null)
                    return PsychiatricStayPrice.Unpriced(
                        $"No psychiatric per diem covers {day:yyyy-MM-dd} (line {line.LineNumber}) for hospital {claim.FacilityNpi}.", steps);

                if (parts.Count > 0 && ReferenceEquals(parts[^1].Rate, rate))
                    parts[^1] = (rate, parts[^1].Days + 1);
                else
                    parts.Add((rate, 1));

                var month = new DateOnly(d.Year, d.Month, 1);
                daysByMonth[month] = (daysByMonth.TryGetValue(month, out var n) ? n : 0) + 1;
            }

            var allowed = MoneyRounding.ToCents(parts.Sum(p => p.Days * p.Rate.DailyRate));
            results.Add(new PsychiatricLinePrice(line.LineNumber, line.Days, parts[^1].Rate.DailyRate, allowed));
            steps.Add($"Line {line.LineNumber}: "
                + string.Join(" + ", parts.Select(p => $"{p.Days} day(s) × per diem {Money(p.Rate.DailyRate)}{(p.Rate.Source is null ? "" : $" ({p.Rate.Source})")}"))
                + $" = {Money(allowed)}.");
        }

        if (results.All(r => r.PaidDays == 0))
            return PsychiatricStayPrice.Unpriced("The claim has no accommodation (room and board) days.", steps);

        // The adult IMD limit, month by month.
        if (_policy.ImdMaxDaysPerMonth is { } limit)
        {
            if (claim.AgeAtAdmission is not { } age)
                return PsychiatricStayPrice.Unpriced(
                    $"The patient's age at admission is unknown, so the {limit}-day monthly IMD limit for ages {_policy.ImdLimitMinimumAge}-{_policy.ImdLimitMaximumAge} cannot be checked.", steps);

            if (age >= _policy.ImdLimitMinimumAge && age <= _policy.ImdLimitMaximumAge)
            {
                foreach (var (month, days) in daysByMonth.OrderBy(kv => kv.Key))
                {
                    var prior = claim.ImdDaysAlreadyPaid.TryGetValue(month, out var p) ? p : 0;
                    var total = prior + days;
                    if (total <= limit)
                    {
                        steps.Add($"{month:yyyy-MM}: {total} IMD day(s) ({prior} on other claims), within the {limit}-day limit for age {age}.");
                        continue;
                    }

                    var over = $"{month:yyyy-MM}: {total} IMD day(s) ({prior} on other claims) exceeds the {limit}-day monthly limit for adults {_policy.ImdLimitMinimumAge}-{_policy.ImdLimitMaximumAge} (42 CFR 438.6(e)); the state loses federal match on that month's capitation.";
                    if (_policy.OverLimitAction == ImdOverLimitAction.Pend)
                        return PsychiatricStayPrice.Unpriced(over + " Pended for review.", steps);
                    steps.Add(over + " Paid under the plan's setting.");
                }
            }
            else
            {
                steps.Add($"Age {age} at admission: the IMD day limit does not apply.");
            }
        }

        steps.Add($"Allowed = {Money(results.Sum(r => r.Allowed))}.");
        return new PsychiatricStayPrice
        {
            Priced = true,
            Lines = results.OrderBy(r => r.LineNumber).ToList(),
            ImdDaysByMonth = daysByMonth,
            Steps = steps,
        };
    }

    private static bool IsAccommodation(string rev) =>
        int.TryParse(rev, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code is >= 100 and <= 219 && !(code is >= 180 and <= 189);

    private static bool IsLeave(string rev) =>
        int.TryParse(rev, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code is >= 180 and <= 189;

    private static string Money(decimal amount) => MoneyRounding.ToCents(amount).ToString("C", CultureInfo.GetCultureInfo("en-US"));
}
