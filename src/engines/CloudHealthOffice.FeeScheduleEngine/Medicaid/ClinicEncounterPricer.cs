using System.Globalization;
using CloudHealthOffice.Infrastructure.Money;

namespace CloudHealthOffice.FeeScheduleEngine.Medicaid;

// ═══════════════════════════════════════════════════════════════════
// FQHC / RHC ENCOUNTER PRICING (Medicaid managed care)
//
// Federal law (SSA §1902(bb)) entitles a federally qualified health center
// or rural health clinic to its prospective payment system (PPS) rate, or a
// state alternative (Texas APPS), per qualifying visit. When the clinic
// sees a managed care member there are two ways the money gets there:
//
//   ContractedRateStateWraps  the MCO pays its contracted rate and the
//                             state pays the clinic the difference up to
//                             PPS (a "wrap"). Texas (SPA TX-21-0009: HHSC
//                             makes up any shortfall at least quarterly)
//                             and Medi-Cal (DHCS wrap: interim per visit,
//                             then annual reconciliation) both work this way.
//
//   PlanPaysPps               the MCO pays the full per-visit rate itself,
//                             as some state contracts and some MCO-clinic
//                             agreements require.
//
// Either way the MCO must know the expected wrap per encounter: the state
// reconciles wraps from the MCO's encounter data, and a plan paying less
// than its other providers get breaks the "no less than" rule.
// ═══════════════════════════════════════════════════════════════════

/// <summary>FQHC or RHC.</summary>
public enum ClinicKind
{
    Fqhc,
    Rhc,
}

/// <summary>Who pays the clinic up to its per-visit rate.</summary>
public enum ClinicPaymentMode
{
    /// <summary>The MCO pays its contracted rate; the state wraps the difference to PPS.</summary>
    ContractedRateStateWraps,

    /// <summary>The MCO pays the full per-visit rate; there is no state wrap.</summary>
    PlanPaysPps,
}

/// <summary>A clinic's per-visit rate for a period, from the state rate letter.</summary>
public sealed record ClinicEncounterRate
{
    /// <summary>The clinic's billing NPI.</summary>
    public required string Npi { get; init; }

    /// <summary>FQHC or RHC.</summary>
    public required ClinicKind Kind { get; init; }

    /// <summary>The per-visit rate in dollars.</summary>
    public required decimal PerVisitRate { get; init; }

    /// <summary>The rate's method as the state names it: "PPS", or Texas "APPS".</summary>
    public string Method { get; init; } = "PPS";

    /// <summary>
    /// The visit category the rate is for ("medical", "dental", "behavioral"),
    /// when the state sets separate rates. Null: one rate for every category.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>First service date the rate applies to.</summary>
    public required DateOnly EffectiveFrom { get; init; }

    /// <summary>Last service date the rate applies to; null while current.</summary>
    public DateOnly? EffectiveTo { get; init; }
}

/// <summary>One state's rules for recognizing and paying clinic encounters.</summary>
public sealed record ClinicEncounterPolicy
{
    /// <summary>State code, for the record.</summary>
    public required string State { get; init; }

    /// <summary>Who pays the clinic up to its rate.</summary>
    public ClinicPaymentMode PaymentMode { get; init; } = ClinicPaymentMode.ContractedRateStateWraps;

    /// <summary>
    /// Procedure codes that mark a line as the encounter (e.g. T1015,
    /// "clinic visit/encounter, all-inclusive"). A service date with no such
    /// line has no encounter and its lines price by the contract alone.
    /// </summary>
    public IReadOnlySet<string> EncounterCodes { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "T1015" };

    /// <summary>
    /// The visit category for an encounter line, by modifier or revenue code
    /// (e.g. dental revenue code 0512). Unmatched lines are "medical".
    /// </summary>
    public IReadOnlyDictionary<string, string> CategoryByModifierOrRevenueCode { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Encounters paid per member, clinic, day and category.</summary>
    public int MaxEncountersPerDayPerCategory { get; init; } = 1;

    /// <summary>
    /// Service dates on or after which a state-only (no federal match) service
    /// gets no wrap. Medi-Cal: 1 July 2026 (AB 116, W&amp;I §14132.100(c)).
    /// </summary>
    public DateOnly? StateOnlyServicesUnwrappedFrom { get; init; }

    /// <summary>Texas defaults: MCO contracted rate, HHSC wraps to PPS/APPS (SPA TX-21-0009).</summary>
    public static ClinicEncounterPolicy Texas() => new() { State = "TX" };

    /// <summary>
    /// Medi-Cal defaults: MCO pays no less than it pays other providers (W&amp;I
    /// §14087.325(d)); DHCS wraps to PPS, except state-only services from
    /// 1 July 2026.
    /// </summary>
    public static ClinicEncounterPolicy California() => new()
    {
        State = "CA",
        StateOnlyServicesUnwrappedFrom = new DateOnly(2026, 7, 1),
    };
}

/// <summary>One line of a clinic claim.</summary>
public sealed record ClinicClaimLine
{
    public required int LineNumber { get; init; }
    public string? ProcedureCode { get; init; }
    public IReadOnlyList<string> Modifiers { get; init; } = [];
    public string? RevenueCode { get; init; }
    public required DateOnly ServiceDate { get; init; }
    public decimal ChargeAmount { get; init; }

    /// <summary>
    /// What the plan's contract (or default fee schedule) allows for this
    /// line, already resolved. Required in <see cref="ClinicPaymentMode.ContractedRateStateWraps"/>
    /// mode; null there means the contract has no rate and the claim pends.
    /// </summary>
    public decimal? ContractedAllowed { get; init; }
}

/// <summary>A clinic claim to price.</summary>
public sealed record ClinicClaim
{
    public required string ClinicNpi { get; init; }
    public required IReadOnlyList<ClinicClaimLine> Lines { get; init; }

    /// <summary>
    /// The services are state-only (no federal financial participation) for
    /// this member, e.g. by aid code. Only affects wrap eligibility.
    /// </summary>
    public bool StateOnlyServices { get; init; }
}

/// <summary>One recognized encounter and its money.</summary>
public sealed record ClinicEncounter
{
    public required DateOnly ServiceDate { get; init; }
    public required string Category { get; init; }
    public required int EncounterLineNumber { get; init; }
    public required decimal PerVisitRate { get; init; }
    public required string RateMethod { get; init; }

    /// <summary>What the plan pays for the encounter.</summary>
    public required decimal PlanPayment { get; init; }

    /// <summary>
    /// What the state is expected to pay the clinic on top (rate − plan
    /// payment, never negative). 0 when the plan pays the rate or the
    /// encounter is not wrap-eligible.
    /// </summary>
    public required decimal ExpectedStateWrap { get; init; }

    public required bool WrapEligible { get; init; }
}

/// <summary>A priced clinic claim.</summary>
public sealed record ClinicEncounterPrice
{
    public required bool Priced { get; init; }
    public string? UnpricedReason { get; init; }

    /// <summary>Allowed amount per line number.</summary>
    public IReadOnlyDictionary<int, decimal> LineAllowed { get; init; } = new Dictionary<int, decimal>();

    public IReadOnlyList<ClinicEncounter> Encounters { get; init; } = [];
    public IReadOnlyList<string> Steps { get; init; } = [];

    public decimal TotalAllowed => LineAllowed.Values.Sum();
    public decimal TotalExpectedStateWrap => Encounters.Sum(e => e.ExpectedStateWrap);

    internal static ClinicEncounterPrice Unpriced(string reason, List<string> steps)
    {
        steps.Add(reason);
        return new ClinicEncounterPrice { Priced = false, UnpricedReason = reason, Steps = steps };
    }
}

/// <summary>
/// Prices a Medicaid managed care claim from an FQHC or RHC. Pure: rates,
/// contract amounts and the state policy are inputs.
/// </summary>
public sealed class ClinicEncounterPricer(ClinicEncounterPolicy policy)
{
    private readonly ClinicEncounterPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));

    /// <summary>
    /// Prices the claim. Lines on a date with a recognized encounter are
    /// priced as one all-inclusive visit per category; lines on other dates
    /// price by the contract. Never throws for missing data.
    /// </summary>
    public ClinicEncounterPrice Price(ClinicClaim claim, IReadOnlyList<ClinicEncounterRate> rates)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(rates);

        var steps = new List<string>();
        var allowed = new Dictionary<int, decimal>();
        var encounters = new List<ClinicEncounter>();

        if (claim.Lines.Select(l => l.LineNumber).Distinct().Count() != claim.Lines.Count)
            return ClinicEncounterPrice.Unpriced("The claim has duplicate line numbers.", steps);

        foreach (var day in claim.Lines.GroupBy(l => l.ServiceDate).OrderBy(g => g.Key))
        {
            var encounterLines = day.Where(IsEncounterLine).OrderBy(l => l.LineNumber).ToList();
            if (encounterLines.Count == 0)
            {
                steps.Add($"{day.Key:yyyy-MM-dd}: no encounter line ({string.Join("/", _policy.EncounterCodes)}); lines price by the contract.");
                foreach (var line in day)
                {
                    if (line.ContractedAllowed is not { } amount)
                        return ClinicEncounterPrice.Unpriced($"Line {line.LineNumber} has no contracted rate.", steps);
                    allowed[line.LineNumber] = amount;
                }
                continue;
            }

            // An encounter is all-inclusive for its day and category: the
            // category's other lines belong to it, and lines whose category
            // has no encounter that day fold into the day's first encounter.
            var byCategory = encounterLines
                .GroupBy(Category, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var group in byCategory)
            {
                var category = group.Key;
                var paid = group.Take(_policy.MaxEncountersPerDayPerCategory).ToList();
                var extra = group.Skip(_policy.MaxEncountersPerDayPerCategory).ToList();

                var rate = FindRate(rates, claim.ClinicNpi, day.Key, category);
                if (rate is null)
                    return ClinicEncounterPrice.Unpriced(
                        $"No {category} per-visit rate is on file for clinic {claim.ClinicNpi} on {day.Key:yyyy-MM-dd}.", steps);

                var visitLines = day.Where(l => !IsEncounterLine(l) && (
                        string.Equals(Category(l), category, StringComparison.OrdinalIgnoreCase)
                        || (ReferenceEquals(group, byCategory[0])
                            && !byCategory.Any(c => string.Equals(c.Key, Category(l), StringComparison.OrdinalIgnoreCase)))))
                    .Concat(extra)
                    .ToList();

                var wrapEligible = _policy.PaymentMode == ClinicPaymentMode.ContractedRateStateWraps
                    && !(claim.StateOnlyServices
                         && _policy.StateOnlyServicesUnwrappedFrom is { } from && day.Key >= from);

                for (var i = 0; i < paid.Count; i++)
                {
                    var encounterLine = paid[i];
                    decimal planPayment;
                    if (_policy.PaymentMode == ClinicPaymentMode.PlanPaysPps)
                    {
                        planPayment = rate.PerVisitRate;
                        allowed[encounterLine.LineNumber] = planPayment;
                        steps.Add($"{day.Key:yyyy-MM-dd} {category}: plan pays the {rate.Method} rate {Money(rate.PerVisitRate)} on line {encounterLine.LineNumber}.");
                    }
                    else
                    {
                        // The first encounter's contracted amount is the whole
                        // visit, so the wrap tops up the visit, not one code.
                        IReadOnlyList<ClinicClaimLine> lines = i == 0 ? [encounterLine, .. visitLines] : [encounterLine];
                        var missing = lines.FirstOrDefault(l => l.ContractedAllowed is null);
                        if (missing is not null)
                            return ClinicEncounterPrice.Unpriced($"Line {missing.LineNumber} has no contracted rate.", steps);

                        planPayment = lines.Sum(l => l.ContractedAllowed!.Value);
                        foreach (var l in lines) allowed[l.LineNumber] = l.ContractedAllowed!.Value;
                        steps.Add($"{day.Key:yyyy-MM-dd} {category}: plan pays its contracted {Money(planPayment)} for line(s) "
                            + $"{string.Join(", ", lines.Select(l => l.LineNumber))}.");
                    }

                    var wrap = wrapEligible ? Math.Max(0m, rate.PerVisitRate - planPayment) : 0m;
                    if (_policy.PaymentMode == ClinicPaymentMode.ContractedRateStateWraps)
                    {
                        steps.Add(wrapEligible
                            ? $"Expected state wrap = {rate.Method} {Money(rate.PerVisitRate)} − plan {Money(planPayment)} = {Money(wrap)}."
                            : $"No state wrap: state-only services on or after {_policy.StateOnlyServicesUnwrappedFrom:yyyy-MM-dd}.");
                    }

                    encounters.Add(new ClinicEncounter
                    {
                        ServiceDate = day.Key,
                        Category = category,
                        EncounterLineNumber = encounterLine.LineNumber,
                        PerVisitRate = rate.PerVisitRate,
                        RateMethod = rate.Method,
                        PlanPayment = MoneyRounding.ToCents(planPayment),
                        ExpectedStateWrap = MoneyRounding.ToCents(wrap),
                        WrapEligible = wrapEligible,
                    });
                }

                if (_policy.PaymentMode == ClinicPaymentMode.PlanPaysPps)
                {
                    // All-inclusive: everything else in the visit is $0.
                    foreach (var line in visitLines) allowed[line.LineNumber] = 0m;
                }

                if (extra.Count > 0)
                {
                    steps.Add($"{day.Key:yyyy-MM-dd} {category}: {extra.Count} encounter line(s) beyond "
                        + $"{_policy.MaxEncountersPerDayPerCategory} per day are part of the visit and earn no further encounter.");
                }
            }
        }

        return new ClinicEncounterPrice
        {
            Priced = true,
            LineAllowed = allowed.ToDictionary(kv => kv.Key, kv => MoneyRounding.ToCents(kv.Value)),
            Encounters = encounters,
            Steps = steps,
        };
    }

    private bool IsEncounterLine(ClinicClaimLine line) =>
        line.ProcedureCode is { } code && _policy.EncounterCodes.Contains(code.Trim());

    private string Category(ClinicClaimLine line)
    {
        foreach (var modifier in line.Modifiers)
        {
            if (_policy.CategoryByModifierOrRevenueCode.TryGetValue(modifier.Trim(), out var byModifier))
                return byModifier;
        }

        if (line.RevenueCode is { } rev
            && _policy.CategoryByModifierOrRevenueCode.TryGetValue(rev.Trim(), out var byRevenue))
            return byRevenue;

        return "medical";
    }

    private static ClinicEncounterRate? FindRate(
        IReadOnlyList<ClinicEncounterRate> rates, string npi, DateOnly date, string category)
    {
        var candidates = rates.Where(r =>
                string.Equals(r.Npi, npi, StringComparison.Ordinal)
                && r.EffectiveFrom <= date
                && (r.EffectiveTo is null || r.EffectiveTo >= date))
            .ToList();

        // A category-specific rate wins over a general one.
        return candidates.FirstOrDefault(r => string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault(r => r.Category is null);
    }

    private static string Money(decimal amount) => MoneyRounding.ToCents(amount).ToString("C", CultureInfo.GetCultureInfo("en-US"));
}
