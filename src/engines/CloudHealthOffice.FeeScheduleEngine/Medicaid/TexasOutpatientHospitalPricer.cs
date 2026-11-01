using System.Globalization;
using CloudHealthOffice.Infrastructure.Money;

namespace CloudHealthOffice.FeeScheduleEngine.Medicaid;

// ═══════════════════════════════════════════════════════════════════
// TEXAS MEDICAID OUTPATIENT HOSPITAL (1 TAC §355.8061)
//
// Texas has two outpatient methods and the rule switches between them on
// an event, not a date: "once the modernized MMIS is operational" every
// hospital moves to an outpatient prospective payment system using the 3M
// EAPG calculator. Until then:
//
//   payment = allowable charges × class percentage × hospital interim rate
//
// where the class percentage is 76.03% / 72.27% (children's and
// state-owned, high / other volume), 100% (rural) or 72.00% / 68.44% (all
// other), and the interim rate is the hospital's Medicaid outpatient
// cost-to-charge ratio. Imaging, outpatient surgery (ASC method) and
// non-emergent ER visits are paid by fee schedule, which the existing fee
// schedule engine already does; this pricer handles the rest.
//
// After the switch:
//
//   line payment = EAPG weight × conversion factor × discount factor
//
// for lines the licensed grouper marks payable or discounted, and nothing
// separately for packaged or consolidated lines.
//
// Medi-Cal is not here on purpose: it pays outpatient hospital services
// from the Medi-Cal fee schedule (the Medi-Cal rates file, importable since
// #1711), not EAPG or APC.
// ═══════════════════════════════════════════════════════════════════

/// <summary>Which Texas outpatient method is in force.</summary>
public enum TexasOutpatientMethod
{
    /// <summary>Pre-MMIS: allowable charges × class percentage × interim rate (§355.8061(b)).</summary>
    PercentOfChargesInterim,

    /// <summary>Post-MMIS: EAPG weight × conversion factor × discount factor (§355.8061(a)(1)).</summary>
    Eapg,
}

/// <summary>A hospital's Texas outpatient facts.</summary>
public sealed record TexasOutpatientHospital
{
    public required string Npi { get; init; }
    public required TexasHospitalClass Class { get; init; }

    /// <summary>State-owned hospitals share the children's percentages (§355.8061(b)(1)).</summary>
    public bool StateOwned { get; init; }

    /// <summary>High-volume provider: at least $200,000 of 2004 Medicaid outpatient payments.</summary>
    public bool HighVolume { get; init; }

    /// <summary>Medicaid outpatient interim rate (cost-to-charge ratio). Rural interim rates may not exceed 100%.</summary>
    public decimal? InterimRate { get; init; }

    /// <summary>EAPG conversion factor in dollars, for the EAPG method.</summary>
    public decimal? EapgConversionFactor { get; init; }
}

/// <summary>A line to price.</summary>
public sealed record TexasOutpatientLine
{
    public required int LineNumber { get; init; }

    /// <summary>Allowable (covered) charges on the line.</summary>
    public required decimal AllowableCharges { get; init; }

    /// <summary>EAPG method only: the EAPG the licensed grouper assigned.</summary>
    public string? Eapg { get; init; }

    /// <summary>EAPG method only: the line is paid (payable or discounted) rather than packaged, consolidated or not payable.</summary>
    public bool EapgPayable { get; init; }

    /// <summary>EAPG method only: share of the weight paid (1, or less when discounted).</summary>
    public decimal EapgDiscountFactor { get; init; } = 1m;
}

/// <summary>The class percentages in §355.8061(b)(1). Override when HHSC changes them.</summary>
public sealed record TexasOutpatientPercentages
{
    public decimal ChildrensHighVolume { get; init; } = 0.7603m;
    public decimal ChildrensOther { get; init; } = 0.7227m;
    public decimal Rural { get; init; } = 1.00m;
    public decimal OtherHighVolume { get; init; } = 0.7200m;
    public decimal OtherOther { get; init; } = 0.6844m;
}

/// <summary>A priced outpatient claim.</summary>
public sealed record TexasOutpatientPrice
{
    public required bool Priced { get; init; }
    public string? UnpricedReason { get; init; }
    public IReadOnlyDictionary<int, decimal> LineAllowed { get; init; } = new Dictionary<int, decimal>();
    public IReadOnlyList<string> Steps { get; init; } = [];
    public decimal TotalAllowed => LineAllowed.Values.Sum();

    internal static TexasOutpatientPrice Unpriced(string reason, List<string> steps)
    {
        steps.Add(reason);
        return new TexasOutpatientPrice { Priced = false, UnpricedReason = reason, Steps = steps };
    }
}

/// <summary>Prices Texas Medicaid outpatient hospital lines under either method. Pure.</summary>
public sealed class TexasOutpatientHospitalPricer(
    TexasOutpatientMethod method,
    TexasOutpatientPercentages? percentages = null)
{
    private readonly TexasOutpatientPercentages _pct = percentages ?? new TexasOutpatientPercentages();

    /// <summary>
    /// Prices the lines. <paramref name="eapgWeights"/> (EAPG → relative
    /// weight, from the state's table) is needed only for the EAPG method.
    /// </summary>
    public TexasOutpatientPrice Price(
        TexasOutpatientHospital hospital,
        IReadOnlyList<TexasOutpatientLine> lines,
        IReadOnlyDictionary<string, decimal>? eapgWeights = null)
    {
        ArgumentNullException.ThrowIfNull(hospital);
        ArgumentNullException.ThrowIfNull(lines);

        var steps = new List<string>();
        if (lines.Select(l => l.LineNumber).Distinct().Count() != lines.Count)
            return TexasOutpatientPrice.Unpriced("The claim has duplicate line numbers.", steps);

        return method == TexasOutpatientMethod.PercentOfChargesInterim
            ? PercentOfCharges(hospital, lines, steps)
            : ByEapg(hospital, lines, eapgWeights, steps);
    }

    private TexasOutpatientPrice PercentOfCharges(
        TexasOutpatientHospital hospital, IReadOnlyList<TexasOutpatientLine> lines, List<string> steps)
    {
        if (hospital.InterimRate is not { } interim || interim <= 0)
            return TexasOutpatientPrice.Unpriced($"No outpatient interim rate is loaded for hospital {hospital.Npi}.", steps);

        if (hospital.Class == TexasHospitalClass.Rural && interim > 1m)
        {
            steps.Add($"Rural interim rate {interim} capped at 100% (§355.8061(b)(3)).");
            interim = 1m;
        }

        var percent = (hospital.Class, hospital.StateOwned, hospital.HighVolume) switch
        {
            (TexasHospitalClass.Rural, _, _) => _pct.Rural,
            (TexasHospitalClass.Childrens, _, true) or (_, true, true) => _pct.ChildrensHighVolume,
            (TexasHospitalClass.Childrens, _, false) or (_, true, false) => _pct.ChildrensOther,
            (_, _, true) => _pct.OtherHighVolume,
            _ => _pct.OtherOther,
        };
        steps.Add($"Percent of allowable charges {Pct(percent)} × interim rate {interim} for hospital {hospital.Npi}.");

        var allowed = lines.ToDictionary(
            l => l.LineNumber,
            l => MoneyRounding.ToCents(l.AllowableCharges * percent * interim));
        steps.Add($"Allowed = {Money(lines.Sum(l => l.AllowableCharges))} × {Pct(percent)} × {interim} = {Money(allowed.Values.Sum())}.");

        return new TexasOutpatientPrice { Priced = true, LineAllowed = allowed, Steps = steps };
    }

    private static TexasOutpatientPrice ByEapg(
        TexasOutpatientHospital hospital, IReadOnlyList<TexasOutpatientLine> lines,
        IReadOnlyDictionary<string, decimal>? weights, List<string> steps)
    {
        if (hospital.EapgConversionFactor is not { } cf || cf <= 0)
            return TexasOutpatientPrice.Unpriced($"No EAPG conversion factor is loaded for hospital {hospital.Npi}.", steps);
        if (weights is null)
            return TexasOutpatientPrice.Unpriced("No EAPG weight table is loaded.", steps);

        var allowed = new Dictionary<int, decimal>();
        foreach (var line in lines.OrderBy(l => l.LineNumber))
        {
            if (!line.EapgPayable)
            {
                allowed[line.LineNumber] = 0m;
                steps.Add($"Line {line.LineNumber}: packaged, consolidated or not payable under EAPG; $0.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(line.Eapg) || !weights.TryGetValue(line.Eapg.Trim(), out var weight))
                return TexasOutpatientPrice.Unpriced($"Line {line.LineNumber}: no EAPG weight is loaded for '{line.Eapg}'.", steps);
            if (line.EapgDiscountFactor is <= 0m or > 1m)
                return TexasOutpatientPrice.Unpriced($"Line {line.LineNumber}: discount factor {line.EapgDiscountFactor} is outside (0, 1].", steps);

            var amount = weight * cf * line.EapgDiscountFactor;
            allowed[line.LineNumber] = MoneyRounding.ToCents(amount);
            steps.Add($"Line {line.LineNumber}: EAPG {line.Eapg} weight {weight} × {Money(cf)} × {line.EapgDiscountFactor} = {Money(amount)}.");
        }

        return new TexasOutpatientPrice { Priced = true, LineAllowed = allowed, Steps = steps };
    }

    private static string Money(decimal amount) => MoneyRounding.ToCents(amount).ToString("C", CultureInfo.GetCultureInfo("en-US"));

    private static string Pct(decimal fraction) => (fraction * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";
}
