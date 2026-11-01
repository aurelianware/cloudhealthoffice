using System.Globalization;
using CloudHealthOffice.Infrastructure.Money;

namespace CloudHealthOffice.FeeScheduleEngine.Medicaid;

// ═══════════════════════════════════════════════════════════════════
// NURSING FACILITY / LONG-TERM CARE PER DIEM (Medicaid managed care)
//
// A Medicaid nursing facility stay pays a daily rate, and the resident
// contributes part of their own monthly income first: Texas calls it
// "applied income", Medi-Cal "share of cost". The facility collects that
// amount from the resident, so the plan pays the per diem total for the
// month less whatever liability is still unmet.
//
//   Texas      STAR+PLUS MCOs pay the HHSC daily rate for the resident's
//              classification (RUG-III, moving to PDPM-LTC), billed as a
//              HIPPS code on revenue code 0022, less applied income.
//   California Medi-Cal plans pay the facility's Medi-Cal per diem (the
//              CalAIM LTC carve-in requires FFS rates through 2026), less
//              share of cost; a bed hold of up to 7 days for an acute
//              hospital stay is paid (22 CCR §51535.1).
// ═══════════════════════════════════════════════════════════════════

/// <summary>A facility's daily rate for a period.</summary>
public sealed record NursingFacilityRate
{
    public required string Npi { get; init; }

    /// <summary>
    /// The resident classification the rate is for (a RUG or PDPM HIPPS
    /// code). Null: the facility's rate for every resident, as in Medi-Cal.
    /// </summary>
    public string? Classification { get; init; }

    public required decimal DailyRate { get; init; }
    public required DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
}

/// <summary>How leave days on a revenue code are paid.</summary>
/// <param name="RevenueCodes">Leave revenue codes this rule covers, e.g. 0185 (hospital leave).</param>
/// <param name="RateFactor">Share of the daily rate paid per leave day (1 = full, 0 = unpaid).</param>
/// <param name="MaxDaysPerLine">Most leave days paid on one line (one leave occurrence); the rest are unpaid.</param>
public sealed record NursingFacilityLeaveRule(IReadOnlySet<string> RevenueCodes, decimal RateFactor, int MaxDaysPerLine);

/// <summary>One state's nursing facility rules.</summary>
public sealed record NursingFacilityPolicy
{
    public required string State { get; init; }

    /// <summary>Rates are per resident classification (Texas); the claim must carry it.</summary>
    public bool RateKeyedByClassification { get; init; }

    /// <summary>Revenue code whose HCPCS field carries the classification (HIPPS) code.</summary>
    public string ClassificationRevenueCode { get; init; } = "0022";

    /// <summary>
    /// Leave-day rules. A leave line (revenue codes 0180-0189) matching no
    /// rule leaves the claim unpriced: whether a state pays leave days is a
    /// policy choice CHO must not guess.
    /// </summary>
    public IReadOnlyList<NursingFacilityLeaveRule> LeaveRules { get; init; } = [];

    /// <summary>
    /// Texas: HHSC daily rate by classification, less applied income. Leave
    /// rules are left to the plan's contract.
    /// </summary>
    public static NursingFacilityPolicy Texas() => new() { State = "TX", RateKeyedByClassification = true };

    /// <summary>
    /// Medi-Cal: facility per diem, less share of cost; bed hold for an acute
    /// hospital stay (revenue code 0185) paid at the per diem for up to 7
    /// days (22 CCR §51535.1).
    /// </summary>
    public static NursingFacilityPolicy California() => new()
    {
        State = "CA",
        LeaveRules =
        [
            new NursingFacilityLeaveRule(new HashSet<string>(StringComparer.Ordinal) { "0185" }, 1m, 7),
        ],
    };
}

/// <summary>One line of a nursing facility claim.</summary>
public sealed record NursingFacilityLine
{
    public required int LineNumber { get; init; }
    public required string RevenueCode { get; init; }

    /// <summary>The HCPCS/HIPPS code on the line (the classification on revenue code 0022).</summary>
    public string? ProcedureCode { get; init; }

    /// <summary>First day the line covers.</summary>
    public required DateOnly ServiceDate { get; init; }

    /// <summary>Days (units) on the line.</summary>
    public required int Days { get; init; }

    public decimal ChargeAmount { get; init; }
}

/// <summary>A nursing facility claim and the resident's liability.</summary>
public sealed record NursingFacilityClaim
{
    public required string FacilityNpi { get; init; }
    public required IReadOnlyList<NursingFacilityLine> Lines { get; init; }

    /// <summary>
    /// The resident's monthly liability (applied income or share of cost) by
    /// the first day of the month it applies to. A month absent here has no
    /// liability on file and pends, because paying the full per diem when
    /// the resident owes part of it overpays.
    /// </summary>
    public required IReadOnlyDictionary<DateOnly, decimal> MonthlyLiability { get; init; }

    /// <summary>
    /// Liability already applied to other claims for the same resident and
    /// month, by first day of the month. Liability is met once a month, not
    /// once a claim.
    /// </summary>
    public IReadOnlyDictionary<DateOnly, decimal> LiabilityAlreadyApplied { get; init; } = new Dictionary<DateOnly, decimal>();
}

/// <summary>The result for one line.</summary>
public sealed record NursingFacilityLinePrice(
    int LineNumber, int PaidDays, decimal DailyRate, decimal Gross, decimal LiabilityApplied, decimal Allowed);

/// <summary>A priced nursing facility claim.</summary>
public sealed record NursingFacilityPrice
{
    public required bool Priced { get; init; }
    public string? UnpricedReason { get; init; }
    public IReadOnlyList<NursingFacilityLinePrice> Lines { get; init; } = [];

    /// <summary>Liability applied by month, to add to the running total for later claims.</summary>
    public IReadOnlyDictionary<DateOnly, decimal> LiabilityAppliedByMonth { get; init; } = new Dictionary<DateOnly, decimal>();

    public IReadOnlyList<string> Steps { get; init; } = [];

    public decimal TotalGross => Lines.Sum(l => l.Gross);
    public decimal TotalLiabilityApplied => Lines.Sum(l => l.LiabilityApplied);
    public decimal TotalAllowed => Lines.Sum(l => l.Allowed);

    internal static NursingFacilityPrice Unpriced(string reason, List<string> steps)
    {
        steps.Add(reason);
        return new NursingFacilityPrice { Priced = false, UnpricedReason = reason, Steps = steps };
    }
}

/// <summary>
/// Prices a Medicaid nursing facility claim: daily rate × days for room and
/// board (revenue codes 0100-0179, 0190-0199) and paid leave, then the
/// resident's unmet monthly liability, applied to lines in date order.
/// Pure; never throws for missing data.
/// </summary>
public sealed class NursingFacilityPricer(NursingFacilityPolicy policy)
{
    private readonly NursingFacilityPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));

    public NursingFacilityPrice Price(NursingFacilityClaim claim, IReadOnlyList<NursingFacilityRate> rates)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(rates);

        var steps = new List<string>();

        if (claim.Lines.Select(l => l.LineNumber).Distinct().Count() != claim.Lines.Count)
            return NursingFacilityPrice.Unpriced("The claim has duplicate line numbers.", steps);

        string? classification = null;
        if (_policy.RateKeyedByClassification)
        {
            var codes = claim.Lines
                .Where(l => l.RevenueCode.Trim() == _policy.ClassificationRevenueCode && !string.IsNullOrWhiteSpace(l.ProcedureCode))
                .Select(l => l.ProcedureCode!.Trim().ToUpperInvariant())
                .Distinct()
                .ToList();
            if (codes.Count != 1)
            {
                return NursingFacilityPrice.Unpriced(codes.Count == 0
                    ? $"{_policy.State} rates are by resident classification and the claim has no HIPPS code on revenue code {_policy.ClassificationRevenueCode}."
                    : $"The claim carries more than one classification ({string.Join(", ", codes)}); bill each period separately.", steps);
            }
            classification = codes[0];
            steps.Add($"Resident classification {classification}.");
        }

        // Gross per line, in date order.
        var priced = new List<(NursingFacilityLine Line, DateOnly Month, int PaidDays, decimal Rate, decimal Gross)>();
        foreach (var line in claim.Lines.OrderBy(l => l.ServiceDate).ThenBy(l => l.LineNumber))
        {
            var rev = line.RevenueCode.Trim().PadLeft(4, '0');
            if (rev == _policy.ClassificationRevenueCode)
            {
                priced.Add((line, MonthOf(line.ServiceDate), 0, 0m, 0m));
                continue;
            }

            if (!IsAccommodation(rev))
                return NursingFacilityPrice.Unpriced($"Line {line.LineNumber} (revenue code {rev}) is not a nursing facility day; price ancillaries separately.", steps);

            if (line.Days <= 0)
                return NursingFacilityPrice.Unpriced($"Line {line.LineNumber} has no days.", steps);

            var lastDay = line.ServiceDate.AddDays(line.Days - 1);
            if (MonthOf(lastDay) != MonthOf(line.ServiceDate))
                return NursingFacilityPrice.Unpriced($"Line {line.LineNumber} spans two months; liability is monthly, so bill each month on its own line.", steps);

            var rate = FindRate(rates, claim.FacilityNpi, classification, line.ServiceDate, lastDay);
            if (rate is null)
                return NursingFacilityPrice.Unpriced(
                    $"No daily rate{(classification is null ? "" : $" for {classification}")} covers {line.ServiceDate:yyyy-MM-dd} to {lastDay:yyyy-MM-dd} for facility {claim.FacilityNpi}.", steps);

            int paidDays;
            decimal factor;
            if (IsLeave(rev))
            {
                var rule = _policy.LeaveRules.FirstOrDefault(r => r.RevenueCodes.Contains(rev));
                if (rule is null)
                    return NursingFacilityPrice.Unpriced($"Line {line.LineNumber} is leave (revenue code {rev}) and {_policy.State} has no leave rule configured.", steps);
                paidDays = Math.Min(line.Days, rule.MaxDaysPerLine);
                factor = rule.RateFactor;
                steps.Add($"Line {line.LineNumber}: leave {rev}, {paidDays} of {line.Days} day(s) paid at {factor:0.##} × {Money(rate.DailyRate)}.");
            }
            else
            {
                paidDays = line.Days;
                factor = 1m;
                steps.Add($"Line {line.LineNumber}: {paidDays} day(s) × {Money(rate.DailyRate)}.");
            }

            priced.Add((line, MonthOf(line.ServiceDate), paidDays, rate.DailyRate, paidDays * rate.DailyRate * factor));
        }

        // Liability, month by month, against lines in date order.
        var remaining = new Dictionary<DateOnly, decimal>();
        foreach (var month in priced.Where(p => p.Gross > 0).Select(p => p.Month).Distinct())
        {
            if (!claim.MonthlyLiability.TryGetValue(month, out var liability))
                return NursingFacilityPrice.Unpriced($"No patient liability (applied income / share of cost) is on file for {month:yyyy-MM}; enter 0 if the resident owes none.", steps);
            var applied = claim.LiabilityAlreadyApplied.TryGetValue(month, out var a) ? a : 0m;
            remaining[month] = Math.Max(0m, liability - applied);
            steps.Add($"{month:yyyy-MM}: liability {Money(liability)}, already met {Money(applied)}, remaining {Money(remaining[month])}.");
        }

        var results = new List<NursingFacilityLinePrice>();
        var appliedByMonth = new Dictionary<DateOnly, decimal>();
        foreach (var (line, month, days, rate, gross) in priced)
        {
            var take = 0m;
            if (gross > 0 && remaining.TryGetValue(month, out var left) && left > 0)
            {
                take = Math.Min(left, gross);
                remaining[month] = left - take;
                appliedByMonth[month] = (appliedByMonth.TryGetValue(month, out var sum) ? sum : 0m) + take;
                steps.Add($"Line {line.LineNumber}: {Money(take)} of liability applied.");
            }

            results.Add(new NursingFacilityLinePrice(
                line.LineNumber, days, rate, MoneyRounding.ToCents(gross), MoneyRounding.ToCents(take), MoneyRounding.ToCents(gross - take)));
        }

        steps.Add($"Allowed = gross {Money(results.Sum(r => r.Gross))} − liability {Money(results.Sum(r => r.LiabilityApplied))} = {Money(results.Sum(r => r.Allowed))}.");

        return new NursingFacilityPrice
        {
            Priced = true,
            Lines = results.OrderBy(r => r.LineNumber).ToList(),
            LiabilityAppliedByMonth = appliedByMonth,
            Steps = steps,
        };
    }

    private static bool IsAccommodation(string rev) =>
        int.TryParse(rev, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code is >= 100 and <= 199;

    private static bool IsLeave(string rev) =>
        int.TryParse(rev, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code is >= 180 and <= 189;

    private static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);

    private static NursingFacilityRate? FindRate(
        IReadOnlyList<NursingFacilityRate> rates, string npi, string? classification, DateOnly from, DateOnly to) =>
        rates.FirstOrDefault(r =>
            string.Equals(r.Npi, npi, StringComparison.Ordinal)
            && (classification is null
                ? r.Classification is null
                : string.Equals(r.Classification, classification, StringComparison.OrdinalIgnoreCase))
            && r.EffectiveFrom <= from
            && (r.EffectiveTo is null || r.EffectiveTo >= to));

    private static string Money(decimal amount) => MoneyRounding.ToCents(amount).ToString("C", CultureInfo.GetCultureInfo("en-US"));
}
