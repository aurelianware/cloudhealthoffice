namespace PremiumBillingService.Mlr;

/// <summary>
/// One rating year's MLR inputs for a Medicaid managed care contract,
/// following 42 CFR 438.8. Amounts are for the MLR reporting year.
/// </summary>
public sealed record MlrInputs
{
    /// <summary>Incurred claims (438.8(e)(2)): paid claims plus the change in claim reserves (IBNR), net of recoveries.</summary>
    public required decimal IncurredClaims { get; init; }

    /// <summary>Activities that improve health care quality (438.8(e)(3)).</summary>
    public decimal QualityImprovement { get; init; }

    /// <summary>Fraud prevention activities (438.8(e)(4)); counted up to 0.5% of adjusted premium revenue.</summary>
    public decimal FraudPrevention { get; init; }

    /// <summary>Premium revenue (438.8(f)(2)): capitation received, including state-directed payments and risk-sharing settlements.</summary>
    public required decimal PremiumRevenue { get; init; }

    /// <summary>Federal, state and local taxes and licensing and regulatory fees (438.8(f)(3)), subtracted from premium.</summary>
    public decimal TaxesAndFees { get; init; }

    /// <summary>Member-months in the reporting year, for the credibility adjustment.</summary>
    public required int MemberMonths { get; init; }
}

/// <summary>One point of the credibility table: the adjustment at this many member-months.</summary>
public sealed record CredibilityPoint(int MemberMonths, decimal Adjustment);

/// <summary>A state's MLR requirement and the credibility table it applies.</summary>
public sealed record MlrStandard
{
    public required string State { get; init; }

    /// <summary>Minimum MLR (438.8(c): at least 0.85; a state may set higher).</summary>
    public decimal Minimum { get; init; } = 0.85m;

    /// <summary>Whether the state requires a remittance when the MLR falls short (438.8(j) leaves it to the state).</summary>
    public bool Remittance { get; init; }

    /// <summary>
    /// Below <see cref="NonCredibleBelow"/> member-months the experience is not
    /// credible (no MLR standard applies); at or above
    /// <see cref="FullyCredibleAt"/>, no adjustment; between, linear
    /// interpolation over <see cref="Table"/>.
    /// </summary>
    public int NonCredibleBelow { get; init; } = 5_400;

    public int FullyCredibleAt { get; init; } = 380_000;

    public IReadOnlyList<CredibilityPoint> Table { get; init; } = DefaultTable;

    /// <summary>Whether these values were checked against the state contract and CMS's current published credibility table.</summary>
    public bool Verified { get; init; }

    /// <summary>
    /// Base credibility adjustments as CMS published them for Medicaid
    /// managed care. Starting values: confirm against CMS's current
    /// publication before filing.
    /// </summary>
    public static IReadOnlyList<CredibilityPoint> DefaultTable { get; } =
    [
        new(5_400, 0.084m),
        new(10_000, 0.062m),
        new(20_000, 0.043m),
        new(50_000, 0.027m),
        new(100_000, 0.019m),
        new(150_000, 0.016m),
        new(200_000, 0.014m),
        new(300_000, 0.011m),
        new(380_000, 0m),
    ];

    /// <summary>Texas HHSC: 85% minimum. Texas also runs an experience rebate under the UMCC, which is separate from this. Unverified.</summary>
    public static MlrStandard Texas { get; } = new() { State = "TX", Minimum = 0.85m, Remittance = false };

    /// <summary>California DHCS Medi-Cal: 85% minimum with remittance. Unverified.</summary>
    public static MlrStandard California { get; } = new() { State = "CA", Minimum = 0.85m, Remittance = true };

    public static MlrStandard? ForState(string state) => state.ToUpperInvariant() switch
    {
        "TX" => Texas,
        "CA" => California,
        _ => null,
    };
}

public enum MlrCredibility
{
    NonCredible = 1,
    PartiallyCredible = 2,
    FullyCredible = 3,
}

/// <summary>The computed MLR and what follows from it.</summary>
public sealed record MlrResult(
    decimal Numerator,
    decimal AdjustedPremiumRevenue,
    decimal FraudPreventionCounted,
    decimal UnadjustedMlr,
    MlrCredibility Credibility,
    decimal CredibilityAdjustment,
    decimal AdjustedMlr,
    decimal Minimum,
    bool MeetsMinimum,
    decimal Remittance,
    IReadOnlyList<string> Notes);

/// <summary>
/// The 42 CFR 438.8 MLR: (incurred claims + quality improvement + fraud
/// prevention up to 0.5% of adjusted premium) / (premium revenue − taxes and
/// fees), plus the credibility adjustment for partially credible experience.
/// A shortfall is remitted on adjusted premium where the state requires it.
/// </summary>
public static class MedicaidMlrCalculator
{
    public const decimal FraudPreventionCap = 0.005m;

    public static MlrResult Calculate(MlrInputs inputs, MlrStandard standard)
    {
        var notes = new List<string>();
        var adjustedPremium = inputs.PremiumRevenue - inputs.TaxesAndFees;
        if (adjustedPremium <= 0m)
            throw new ArgumentException("Premium revenue less taxes and fees must be positive.");

        var fraudCap = Math.Round(adjustedPremium * FraudPreventionCap, 2, MidpointRounding.AwayFromZero);
        var fraud = Math.Min(Math.Max(inputs.FraudPrevention, 0m), fraudCap);
        if (inputs.FraudPrevention > fraudCap)
            notes.Add($"Fraud prevention capped at 0.5% of adjusted premium ({fraudCap:N2})");

        var numerator = inputs.IncurredClaims + inputs.QualityImprovement + fraud;
        var unadjusted = Math.Round(numerator / adjustedPremium, 4, MidpointRounding.AwayFromZero);

        MlrCredibility credibility;
        decimal adjustment;
        if (inputs.MemberMonths < standard.NonCredibleBelow)
        {
            credibility = MlrCredibility.NonCredible;
            adjustment = 0m;
            notes.Add($"{inputs.MemberMonths:N0} member-months is below {standard.NonCredibleBelow:N0}: not credible; the MLR is reported but no standard applies");
        }
        else if (inputs.MemberMonths >= standard.FullyCredibleAt)
        {
            credibility = MlrCredibility.FullyCredible;
            adjustment = 0m;
        }
        else
        {
            credibility = MlrCredibility.PartiallyCredible;
            adjustment = Interpolate(standard.Table, inputs.MemberMonths);
        }

        var adjusted = unadjusted + adjustment;
        var meets = credibility == MlrCredibility.NonCredible || adjusted >= standard.Minimum;
        var remittance = 0m;
        if (!meets && standard.Remittance)
            remittance = Math.Round((standard.Minimum - adjusted) * adjustedPremium, 2, MidpointRounding.AwayFromZero);
        if (!meets && !standard.Remittance)
            notes.Add($"Below the {standard.Minimum:P0} minimum; {standard.State} sets no remittance here, but the shortfall is reportable");
        if (!standard.Verified)
            notes.Add($"The {standard.State} MLR standard and credibility table are starting values, not checked against the contract and CMS's current table");

        return new MlrResult(numerator, adjustedPremium, fraud, unadjusted, credibility, adjustment, adjusted,
            standard.Minimum, meets, remittance, notes);
    }

    private static decimal Interpolate(IReadOnlyList<CredibilityPoint> table, int memberMonths)
    {
        var points = table.OrderBy(p => p.MemberMonths).ToList();
        if (memberMonths <= points[0].MemberMonths) return points[0].Adjustment;
        for (var i = 1; i < points.Count; i++)
        {
            if (memberMonths > points[i].MemberMonths) continue;
            var lo = points[i - 1];
            var hi = points[i];
            var t = (decimal)(memberMonths - lo.MemberMonths) / (hi.MemberMonths - lo.MemberMonths);
            return Math.Round(lo.Adjustment + (hi.Adjustment - lo.Adjustment) * t, 4, MidpointRounding.AwayFromZero);
        }
        return points[^1].Adjustment;
    }
}
