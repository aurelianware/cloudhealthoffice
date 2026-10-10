using System.Text.Json.Serialization;

namespace PremiumBillingService.Rating;

/// <summary>How a rate table turns a household into a monthly premium.</summary>
public enum RatingMethod
{
    /// <summary>One rate per coverage tier (EE / EE+Spouse / EE+Child(ren) / Family).</summary>
    Tier,

    /// <summary>
    /// ACA per-member age rating (45 CFR 147.102): base rate × age factor for
    /// each covered member, summed for the household, counting at most the
    /// three oldest children under 21.
    /// </summary>
    AgeBand,

    /// <summary>
    /// ACA small-group composite rating (45 CFR 147.102(c)(3)): a single
    /// per-enrollee rate derived from the group's age-rated census, fixed for
    /// the plan year.
    /// </summary>
    Composite
}

public enum CoverageTier
{
    EmployeeOnly,
    EmployeeSpouse,
    EmployeeChildren,
    Family
}

public enum MemberRelationship
{
    Subscriber,
    /// <summary>Spouse or domestic partner.</summary>
    Spouse,
    Child,
    /// <summary>Other dependent rated as a child (e.g. a ward or grandchild).</summary>
    OtherDependent
}

/// <summary>How a composite rate becomes a household premium.</summary>
public enum CompositeBasis
{
    /// <summary>Per-enrollee rate × number of rated members (three-child cap applied).</summary>
    PerMember,

    /// <summary>Per-employee rate × the tier factor of the household's tier.</summary>
    TierFactors
}

/// <summary>
/// How a partial month is charged. The rule belongs to the plan's rate filing,
/// so it is carried (and versioned) on the rate table.
/// </summary>
public enum ProrationRule
{
    /// <summary>
    /// By the day: monthly premium × days ÷ days in the month, with the
    /// household and the rate of each day.
    /// </summary>
    Daily,

    /// <summary>
    /// By the half month (days 1–15 and 16–end): each half costs half the
    /// monthly premium and is charged in full, with the household covered on
    /// its first day, when coverage is in force on that first day. Coverage
    /// that starts after a half's first day is first charged at the next half;
    /// coverage that ends inside a half is charged for that whole half.
    /// </summary>
    HalfMonth,

    /// <summary>
    /// By the whole month: the month is charged in full, with the household
    /// covered on the 1st, when coverage is in force on the 1st. Coverage that
    /// starts after the 1st is first charged the following month; coverage
    /// that ends during the month is charged for the whole month.
    /// </summary>
    FullMonth
}

/// <summary>An amount per coverage tier.</summary>
public class TierAmounts
{
    public decimal EmployeeOnly { get; set; }
    public decimal EmployeeSpouse { get; set; }
    public decimal EmployeeChildren { get; set; }
    public decimal Family { get; set; }

    public decimal For(CoverageTier tier) => tier switch
    {
        CoverageTier.EmployeeOnly => EmployeeOnly,
        CoverageTier.EmployeeSpouse => EmployeeSpouse,
        CoverageTier.EmployeeChildren => EmployeeChildren,
        CoverageTier.Family => Family,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null)
    };

    internal IEnumerable<(CoverageTier Tier, decimal Amount)> All() => new[]
    {
        (CoverageTier.EmployeeOnly, EmployeeOnly),
        (CoverageTier.EmployeeSpouse, EmployeeSpouse),
        (CoverageTier.EmployeeChildren, EmployeeChildren),
        (CoverageTier.Family, Family)
    };
}

/// <summary>
/// Optional tobacco rating. ACA allows up to 1.5:1 (45 CFR 147.102(a)(1)(iv)),
/// only for members who may legally use tobacco.
/// </summary>
public class TobaccoSurcharge
{
    /// <summary>Multiplier on a tobacco user's premium, 1.0 (none) to 1.5.</summary>
    public decimal Factor { get; set; } = 1.0m;

    /// <summary>
    /// Tier tables only: a fixed monthly amount per tobacco user, at most half
    /// the EE rate (the 1.5:1 limit). When null, tier tables charge
    /// (Factor − 1) × the EE rate. ACA methods (age band, composite) rate
    /// tobacco on the member's age-rated premium and do not allow a flat amount.
    /// </summary>
    public decimal? FlatMonthlyAmount { get; set; }

    /// <summary>Youngest age that can be tobacco-rated (federal tobacco age is 21).</summary>
    public int MinimumAge { get; set; } = 21;
}

/// <summary>
/// The rates of one plan for one effective period. A plan has one table per
/// period (usually a plan year); periods of a plan may not overlap.
/// </summary>
public class RateTable
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string TenantId { get; set; } = string.Empty;

    public string PlanId { get; set; } = string.Empty;

    public string? Name { get; set; }

    /// <summary>
    /// Version of this table. Stored versions are immutable: a correction is a
    /// new version, and invoice lines record the version they were rated with.
    /// Set by the rate-table store; 0 for a table that was never stored.
    /// </summary>
    public int Version { get; set; }

    /// <summary>SHA-256 of the version's rating content as stored (set by the rate-table store).</summary>
    public string? ContentHash { get; set; }

    /// <summary>How partial months are charged under this table (see <see cref="ProrationRule"/>).</summary>
    public ProrationRule Proration { get; set; } = ProrationRule.Daily;

    /// <summary>First day the rates apply.</summary>
    public DateTime EffectiveFrom { get; set; }

    /// <summary>Last day the rates apply (inclusive); null means open-ended.</summary>
    public DateTime? EffectiveTo { get; set; }

    public RatingMethod Method { get; set; }

    /// <summary>Tier method: the monthly rate of each tier.</summary>
    public TierAmounts? TierRates { get; set; }

    /// <summary>Age-band method: the monthly rate of a 21-year-old (age factor 1.000), before tobacco.</summary>
    public decimal? AgeBandBaseRate { get; set; }

    /// <summary>Age-band method: the curve to use; null means the federal default curve.</summary>
    public AgeCurve? AgeCurve { get; set; }

    /// <summary>
    /// Date ages are measured on. ACA ages are fixed at issue or renewal, so
    /// by default it is <see cref="EffectiveFrom"/> (the plan-year start), or a
    /// later coverage start for someone who joins mid-year.
    /// </summary>
    public DateTime? AgeDeterminationDate { get; set; }

    /// <summary>Children under this age count toward the cap (45 CFR 147.102(c)(1)).</summary>
    public int ChildCapAge { get; set; } = 21;

    /// <summary>At most this many children under <see cref="ChildCapAge"/> are rated per household.</summary>
    public int MaxRatedChildrenUnderCapAge { get; set; } = 3;

    /// <summary>
    /// Composite method: the per-enrollee (PerMember) or per-employee (TierFactors) rate.
    /// A composite table with a tobacco surcharge also needs <see cref="AgeBandBaseRate"/>
    /// (and optionally <see cref="AgeCurve"/>): tobacco is rated on the member's age-rated premium.
    /// </summary>
    public decimal? CompositeRate { get; set; }

    public CompositeBasis CompositeBasis { get; set; } = CompositeBasis.PerMember;

    /// <summary>Composite method with <see cref="CompositeBasis.TierFactors"/>: the factor of each tier.</summary>
    public TierAmounts? CompositeTierFactors { get; set; }

    public TobaccoSurcharge? Tobacco { get; set; }

    public bool CoversDate(DateTime date) =>
        date.Date >= EffectiveFrom.Date && (EffectiveTo == null || date.Date <= EffectiveTo.Value.Date);

    /// <summary>Throws <see cref="RateTableValidationException"/> describing every problem found.</summary>
    public void Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(PlanId))
            errors.Add("PlanId is required");
        if (EffectiveTo.HasValue && EffectiveTo.Value.Date < EffectiveFrom.Date)
            errors.Add("EffectiveTo is before EffectiveFrom");
        if (!Enum.IsDefined(Proration))
            errors.Add($"Unknown proration rule {Proration}");

        switch (Method)
        {
            case RatingMethod.Tier:
                if (TierRates == null)
                    errors.Add("Tier rating needs TierRates");
                else
                    errors.AddRange(TierRates.All().Where(t => t.Amount <= 0).Select(t => $"Tier rate {t.Tier} must be positive"));
                break;
            case RatingMethod.AgeBand:
                if (AgeBandBaseRate is not > 0)
                    errors.Add("Age-band rating needs a positive AgeBandBaseRate");
                if (AgeCurve != null)
                    errors.AddRange(AgeCurve.Problems());
                if (MaxRatedChildrenUnderCapAge < 0)
                    errors.Add("MaxRatedChildrenUnderCapAge cannot be negative");
                break;
            case RatingMethod.Composite:
                if (CompositeRate is not > 0)
                    errors.Add("Composite rating needs a positive CompositeRate");
                if (CompositeBasis == CompositeBasis.TierFactors)
                {
                    if (CompositeTierFactors == null)
                        errors.Add("Composite tier-factor rating needs CompositeTierFactors");
                    else
                        errors.AddRange(CompositeTierFactors.All().Where(t => t.Amount <= 0)
                            .Select(t => $"Composite tier factor {t.Tier} must be positive"));
                }
                break;
            default:
                errors.Add($"Unknown rating method {Method}");
                break;
        }

        if (Tobacco != null)
        {
            if (Tobacco.Factor < 1.0m || Tobacco.Factor > 1.5m)
                errors.Add("Tobacco factor must be between 1.0 and 1.5 (45 CFR 147.102(a)(1)(iv))");
            if (Tobacco.FlatMonthlyAmount is < 0)
                errors.Add("Tobacco flat amount cannot be negative");
            if (Tobacco.FlatMonthlyAmount.HasValue && Method != RatingMethod.Tier)
                errors.Add("A flat tobacco amount is allowed on tier tables only; ACA methods rate tobacco on the member's age-rated premium");
            if (Tobacco.FlatMonthlyAmount is { } flat && Method == RatingMethod.Tier && TierRates != null
                && flat > PremiumRatingEngine.RoundMoney(TierRates.EmployeeOnly * 0.5m))
                errors.Add($"Tobacco flat amount {flat:0.00} exceeds half the EE rate {TierRates.EmployeeOnly:0.00} (1.5:1 limit)");
            if (Method == RatingMethod.Composite && Tobacco.Factor > 1m && AgeBandBaseRate is not > 0)
                errors.Add("Composite tobacco rating needs AgeBandBaseRate (tobacco is rated on the member's age-rated premium)");
            if (Method == RatingMethod.Composite && AgeCurve != null)
                errors.AddRange(AgeCurve.Problems());
            if (Tobacco.MinimumAge < 0)
                errors.Add("Tobacco minimum age cannot be negative");
        }

        if (errors.Count > 0)
            throw new RateTableValidationException(PlanId, errors);
    }
}

public class RateTableValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public RateTableValidationException(string planId, IReadOnlyList<string> errors)
        : base($"Rate table for plan '{planId}' is invalid: {string.Join("; ", errors)}")
    {
        Errors = errors;
    }
}

/// <summary>A rate table version as invoice lines record it: <c>{id}@v{version}</c>.</summary>
public static class RateTableReference
{
    public static string Of(string rateTableId, int version) => $"{rateTableId}@v{version}";

    public static string Of(RateTable table) => Of(table.Id, table.Version);
}

public class RateTableNotFoundException : Exception
{
    public RateTableNotFoundException(string planId, DateTime date)
        : base($"No rate table for plan '{planId}' covers {date:yyyy-MM-dd}")
    {
    }
}
