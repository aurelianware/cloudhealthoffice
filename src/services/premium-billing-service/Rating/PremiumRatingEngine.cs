namespace PremiumBillingService.Rating;

/// <summary>A covered person as the rating engine needs them.</summary>
public class RatedMember
{
    public string MemberId { get; set; } = string.Empty;
    public string? MemberName { get; set; }
    public MemberRelationship Relationship { get; set; }
    public DateTime DateOfBirth { get; set; }
    public bool TobaccoUser { get; set; }

    /// <summary>
    /// First day this member is covered; null means from the coverage's
    /// effective date. A dependent added later has their own date, so earlier
    /// months are not re-rated with them.
    /// </summary>
    public DateTime? EffectiveDate { get; set; }

    /// <summary>Last day this member is covered (inclusive); null means until the coverage ends.</summary>
    public DateTime? TerminationDate { get; set; }
}

/// <summary>One subscriber's coverage under one plan: the unit that is rated and billed.</summary>
public class RatingEnrollment
{
    /// <summary>Coverage record id; the key prior billing is reconciled against.</summary>
    public string CoverageId { get; set; } = string.Empty;

    public string PlanId { get; set; } = string.Empty;

    /// <summary>Explicit tier; when null it is derived from the members.</summary>
    public CoverageTier? Tier { get; set; }

    public List<RatedMember> Members { get; set; } = new();

    public DateTime EffectiveDate { get; set; }

    /// <summary>Last covered day (inclusive); null while active.</summary>
    public DateTime? TerminationDate { get; set; }

    /// <summary>Insurance line code (HLT, DEN, VIS) carried onto the invoice line.</summary>
    public string? InsuranceLineCode { get; set; }

    public RatedMember Subscriber =>
        Members.FirstOrDefault(m => m.Relationship == MemberRelationship.Subscriber)
        ?? throw new InvalidOperationException($"Coverage {CoverageId} has no subscriber");

    public DateTime MemberStart(RatedMember member) => (member.EffectiveDate ?? EffectiveDate).Date;

    public DateTime? MemberEnd(RatedMember member)
    {
        var own = member.TerminationDate?.Date;
        var coverage = TerminationDate?.Date;
        if (own == null) return coverage;
        if (coverage == null) return own;
        return own < coverage ? own : coverage;
    }

    public bool IsCovered(RatedMember member, DateTime date) =>
        date.Date >= MemberStart(member) && (MemberEnd(member) is not { } end || date.Date <= end);

    /// <summary>The same coverage with only the members covered on <paramref name="date"/>.</summary>
    public RatingEnrollment CoveredOn(DateTime date) => new()
    {
        CoverageId = CoverageId,
        PlanId = PlanId,
        Tier = Tier,
        EffectiveDate = EffectiveDate,
        TerminationDate = TerminationDate,
        InsuranceLineCode = InsuranceLineCode,
        Members = Members.Where(m => IsCovered(m, date)).ToList()
    };
}

public class MemberRating
{
    public string MemberId { get; set; } = string.Empty;
    public MemberRelationship Relationship { get; set; }
    public int Age { get; set; }

    /// <summary>Age factor used (age-band only).</summary>
    public decimal? AgeFactor { get; set; }

    /// <summary>False for a child beyond the three oldest under 21 (or not rated individually).</summary>
    public bool Rated { get; set; }

    public decimal BasePremium { get; set; }
    public decimal TobaccoSurcharge { get; set; }
    public decimal Premium => BasePremium + TobaccoSurcharge;
    public string? Note { get; set; }
}

/// <summary>The monthly premium of one household under one rate table.</summary>
public class RatingResult
{
    public string RateTableId { get; set; } = string.Empty;
    public int RateTableVersion { get; set; }
    public string? RateTableHash { get; set; }
    public RatingMethod Method { get; set; }
    public CoverageTier Tier { get; set; }
    public decimal BasePremium { get; set; }
    public decimal TobaccoSurcharge { get; set; }
    public decimal MonthlyPremium => BasePremium + TobaccoSurcharge;
    public List<MemberRating> Members { get; set; } = new();
}

/// <summary>
/// Turns a household and a rate table into a monthly premium. Pure: no I/O,
/// no clock. Money is rounded to cents half away from zero, per member for
/// age-band rating (the household premium is the sum of member premiums).
/// </summary>
public static class PremiumRatingEngine
{
    public static decimal RoundMoney(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>EE / EE+Spouse / EE+Child(ren) / Family from who is covered.</summary>
    public static CoverageTier DeriveTier(IEnumerable<RatedMember> members)
    {
        var list = members.ToList();
        var spouse = list.Any(m => m.Relationship == MemberRelationship.Spouse);
        var children = list.Any(m => m.Relationship is MemberRelationship.Child or MemberRelationship.OtherDependent);
        return (spouse, children) switch
        {
            (false, false) => CoverageTier.EmployeeOnly,
            (true, false) => CoverageTier.EmployeeSpouse,
            (false, true) => CoverageTier.EmployeeChildren,
            _ => CoverageTier.Family
        };
    }

    /// <summary>Age in whole years on <paramref name="asOf"/>.</summary>
    public static int AgeOn(DateTime dateOfBirth, DateTime asOf)
    {
        var age = asOf.Year - dateOfBirth.Year;
        if (asOf.Month < dateOfBirth.Month || (asOf.Month == dateOfBirth.Month && asOf.Day < dateOfBirth.Day))
            age--;
        if (age < 0)
            throw new ArgumentException($"Date of birth {dateOfBirth:yyyy-MM-dd} is after {asOf:yyyy-MM-dd}");
        return age;
    }

    /// <summary>
    /// The date a member's age is measured on: the table's age date (default
    /// its effective start, i.e. the plan-year renewal), or the member's own
    /// coverage start when they joined later (a new hire, or a dependent added
    /// mid-year).
    /// </summary>
    public static DateTime AgeDate(RateTable table, RatingEnrollment enrollment, RatedMember member)
    {
        var tableDate = (table.AgeDeterminationDate ?? table.EffectiveFrom).Date;
        var memberStart = enrollment.MemberStart(member);
        return memberStart > tableDate ? memberStart : tableDate;
    }

    private static int AgeOf(RateTable table, RatingEnrollment enrollment, RatedMember member) =>
        AgeOn(member.DateOfBirth, AgeDate(table, enrollment, member));

    public static RatingResult Rate(RateTable table, RatingEnrollment enrollment)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(enrollment);
        table.Validate();
        if (!string.Equals(table.PlanId, enrollment.PlanId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Rate table is for plan {table.PlanId}, coverage {enrollment.CoverageId} is plan {enrollment.PlanId}");
        if (enrollment.Members.Count(m => m.Relationship == MemberRelationship.Subscriber) != 1)
            throw new ArgumentException($"Coverage {enrollment.CoverageId} must have exactly one subscriber");
        // An unset date of birth would rate as a 2,000-year-old (factor of the oldest band).
        var undated = enrollment.Members.FirstOrDefault(m => m.DateOfBirth == default);
        if (undated != null)
            throw new ArgumentException($"Member {undated.MemberId} of coverage {enrollment.CoverageId} has no date of birth");

        var tier = enrollment.Tier ?? DeriveTier(enrollment.Members);
        var result = new RatingResult
        {
            RateTableId = table.Id,
            RateTableVersion = table.Version,
            RateTableHash = table.ContentHash,
            Method = table.Method,
            Tier = tier
        };

        switch (table.Method)
        {
            case RatingMethod.Tier:
                RateTier(table, enrollment, tier, result);
                break;
            case RatingMethod.AgeBand:
                RateAgeBand(table, enrollment, result);
                break;
            case RatingMethod.Composite:
                RateComposite(table, enrollment, tier, result);
                break;
        }

        result.BasePremium = result.Members.Sum(m => m.BasePremium);
        result.TobaccoSurcharge = result.Members.Sum(m => m.TobaccoSurcharge);
        return result;
    }

    /// <summary>Rates the household as it was on <paramref name="date"/> (members covered that day only).</summary>
    public static RatingResult RateOn(RateTable table, RatingEnrollment enrollment, DateTime date) =>
        Rate(table, enrollment.CoveredOn(date));

    /// <summary>
    /// The composite rate for a plan year from the group's census, rated on an
    /// age-band table (tobacco excluded; it is added per user on top):
    /// PerMember = total age-rated premium ÷ rated members;
    /// TierFactors = total age-rated premium ÷ Σ tier factors of the households.
    /// </summary>
    public static decimal DeriveCompositeRate(
        RateTable ageBandTable, IEnumerable<RatingEnrollment> census, CompositeBasis basis, TierAmounts? tierFactors = null)
    {
        if (ageBandTable.Method != RatingMethod.AgeBand)
            throw new ArgumentException("A composite rate is derived from an age-band table", nameof(ageBandTable));
        if (basis == CompositeBasis.TierFactors && tierFactors == null)
            throw new ArgumentException("Tier factors are required for the TierFactors basis", nameof(tierFactors));

        var households = census.ToList();
        if (households.Count == 0)
            throw new ArgumentException("The census is empty", nameof(census));

        decimal total = 0, divisor = 0;
        foreach (var household in households)
        {
            var noTobacco = new RatingEnrollment
            {
                CoverageId = household.CoverageId,
                PlanId = ageBandTable.PlanId,
                Tier = household.Tier,
                EffectiveDate = household.EffectiveDate,
                Members = household.Members.Select(m => new RatedMember
                {
                    MemberId = m.MemberId,
                    Relationship = m.Relationship,
                    DateOfBirth = m.DateOfBirth,
                    TobaccoUser = false
                }).ToList()
            };
            var rated = Rate(ageBandTable, noTobacco);
            total += rated.BasePremium;
            divisor += basis == CompositeBasis.PerMember
                ? rated.Members.Count(m => m.Rated)
                : tierFactors!.For(rated.Tier);
        }

        return RoundMoney(total / divisor);
    }

    private static void RateTier(RateTable table, RatingEnrollment enrollment, CoverageTier tier, RatingResult result)
    {
        var tierRate = table.TierRates!.For(tier);
        var singleRate = table.TierRates.EmployeeOnly;
        foreach (var member in enrollment.Members)
        {
            var isSubscriber = member.Relationship == MemberRelationship.Subscriber;
            var age = AgeOf(table, enrollment, member);
            result.Members.Add(new MemberRating
            {
                MemberId = member.MemberId,
                Relationship = member.Relationship,
                Age = age,
                Rated = isSubscriber,
                // The tier rate covers the household; it is carried on the subscriber.
                BasePremium = isSubscriber ? tierRate : 0m,
                TobaccoSurcharge = TierTobacco(table, member, age, singleRate),
                Note = isSubscriber ? $"{tier} tier rate" : "included in tier rate"
            });
        }
    }

    private static void RateComposite(RateTable table, RatingEnrollment enrollment, CoverageTier tier, RatingResult result)
    {
        var rate = table.CompositeRate!.Value;
        if (table.CompositeBasis == CompositeBasis.TierFactors)
        {
            var factor = table.CompositeTierFactors!.For(tier);
            foreach (var member in enrollment.Members)
            {
                var isSubscriber = member.Relationship == MemberRelationship.Subscriber;
                var age = AgeOf(table, enrollment, member);
                result.Members.Add(new MemberRating
                {
                    MemberId = member.MemberId,
                    Relationship = member.Relationship,
                    Age = age,
                    Rated = isSubscriber,
                    BasePremium = isSubscriber ? RoundMoney(rate * factor) : 0m,
                    TobaccoSurcharge = CompositeTobacco(table, member, age),
                    Note = isSubscriber ? $"composite {rate:0.00} × {tier} factor {factor}" : "included in tier factor"
                });
            }
            return;
        }

        // PerMember: every rated member pays the composite rate; the three-child cap still applies.
        var capped = UncountedChildren(table, enrollment);
        foreach (var member in enrollment.Members)
        {
            var age = AgeOf(table, enrollment, member);
            var rated = !capped.Contains(member);
            result.Members.Add(new MemberRating
            {
                MemberId = member.MemberId,
                Relationship = member.Relationship,
                Age = age,
                Rated = rated,
                BasePremium = rated ? rate : 0m,
                TobaccoSurcharge = rated ? CompositeTobacco(table, member, age) : 0m,
                Note = rated ? "composite per-member rate" : ChildCapNote(table)
            });
        }
    }

    private static void RateAgeBand(RateTable table, RatingEnrollment enrollment, RatingResult result)
    {
        var curve = table.AgeCurve ?? AgeCurve.FederalDefault;
        var baseRate = table.AgeBandBaseRate!.Value;
        var capped = UncountedChildren(table, enrollment);

        foreach (var member in enrollment.Members)
        {
            var age = AgeOf(table, enrollment, member);
            var factor = curve.FactorFor(age);
            if (capped.Contains(member))
            {
                result.Members.Add(new MemberRating
                {
                    MemberId = member.MemberId,
                    Relationship = member.Relationship,
                    Age = age,
                    AgeFactor = factor,
                    Rated = false,
                    Note = ChildCapNote(table)
                });
                continue;
            }

            var premium = RoundMoney(baseRate * factor);
            var tobacco = IsTobaccoRated(table, member, age)
                ? RoundMoney(premium * (table.Tobacco!.Factor - 1m))
                : 0m;
            result.Members.Add(new MemberRating
            {
                MemberId = member.MemberId,
                Relationship = member.Relationship,
                Age = age,
                AgeFactor = factor,
                Rated = true,
                BasePremium = premium,
                TobaccoSurcharge = tobacco,
                Note = $"{baseRate:0.00} × {factor}"
            });
        }
    }

    /// <summary>Children under the cap age beyond the oldest N (default 3) are not rated.</summary>
    private static HashSet<RatedMember> UncountedChildren(RateTable table, RatingEnrollment enrollment)
    {
        return enrollment.Members
            .Where(m => m.Relationship is MemberRelationship.Child or MemberRelationship.OtherDependent)
            .Where(m => AgeOf(table, enrollment, m) < table.ChildCapAge)
            .OrderBy(m => m.DateOfBirth)           // oldest first
            .ThenBy(m => m.MemberId, StringComparer.Ordinal)
            .Skip(table.MaxRatedChildrenUnderCapAge)
            .ToHashSet();
    }

    private static string ChildCapNote(RateTable table) =>
        $"not rated: only the {table.MaxRatedChildrenUnderCapAge} oldest children under {table.ChildCapAge} are rated";

    private static bool IsTobaccoRated(RateTable table, RatedMember member, int age) =>
        member.TobaccoUser && table.Tobacco != null && age >= table.Tobacco.MinimumAge;

    /// <summary>
    /// Tier tobacco, per tobacco user: a flat amount, or (factor − 1) × the
    /// single-member (EE) rate. A flat amount above half the single rate would
    /// exceed the 1.5:1 limit and is refused.
    /// </summary>
    private static decimal TierTobacco(RateTable table, RatedMember member, int age, decimal singleRate)
    {
        if (!IsTobaccoRated(table, member, age))
            return 0m;
        if (table.Tobacco!.FlatMonthlyAmount is { } flat)
        {
            if (flat > RoundMoney(singleRate * 0.5m))
                throw new RateTableValidationException(table.PlanId, new[]
                {
                    $"tobacco flat amount {flat:0.00} exceeds half the single rate {singleRate:0.00} (1.5:1 limit)"
                });
            return flat;
        }
        return RoundMoney(singleRate * (table.Tobacco.Factor - 1m));
    }

    /// <summary>
    /// Composite tobacco is rated per member: the tobacco user's age-rated
    /// premium (the table's AgeBandBaseRate × their age factor) × (factor − 1),
    /// added to the composite premium. Composite premiums are built from
    /// per-member rating under 45 CFR 147.102(c)(3), and the tobacco factor
    /// applies to the individual's rate (45 CFR 147.102(a)(1)(iv)); CMS's 2013
    /// market reform rule (78 FR 13406) describes applying tobacco per member
    /// on top of a composite premium. A flat amount is not allowed here.
    /// </summary>
    private static decimal CompositeTobacco(RateTable table, RatedMember member, int age)
    {
        if (!IsTobaccoRated(table, member, age))
            return 0m;
        var curve = table.AgeCurve ?? AgeCurve.FederalDefault;
        var ageRated = RoundMoney(table.AgeBandBaseRate!.Value * curve.FactorFor(age));
        return RoundMoney(ageRated * (table.Tobacco!.Factor - 1m));
    }
}
