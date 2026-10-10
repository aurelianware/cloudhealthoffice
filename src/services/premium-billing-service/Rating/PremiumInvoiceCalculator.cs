using PremiumBillingService.Models;

namespace PremiumBillingService.Rating;

/// <summary>
/// What earlier invoices of a group already billed, per coverage and
/// coverage month. Built from the group's invoices; voided invoices and
/// drafts (not issued, still being regenerated) count for nothing.
/// </summary>
public sealed class BilledLedger
{
    private readonly Dictionary<(string Key, DateTime Month), decimal> _billed = new();
    private readonly HashSet<DateTime> _invoicedMonths = new();

    public static BilledLedger Empty => new();

    /// <summary>Months that have an issued (non-void, non-draft) invoice; only those are reconciled.</summary>
    public IReadOnlyCollection<DateTime> InvoicedMonths => _invoicedMonths;

    /// <summary>Whether an invoice counts as billing: issued, and not voided.</summary>
    public static bool Counts(PremiumInvoice invoice) =>
        invoice.Status is not (InvoiceStatus.Voided or InvoiceStatus.Draft);

    public static BilledLedger FromInvoices(IEnumerable<PremiumInvoice> invoices)
    {
        var ledger = new BilledLedger();
        foreach (var invoice in invoices.Where(Counts))
        {
            var month = MonthStart(invoice.BillingPeriodStart);
            ledger.MarkInvoiced(month);
            foreach (var line in invoice.LineItems)
            {
                // A composite (per-tier) line bills several coverages: its components say how much each.
                if (line.Components is { Count: > 0 } components)
                {
                    foreach (var component in components)
                        ledger.Add(KeyFor(component.CoverageId, component.MemberId), month, component.Amount);
                }
                else
                {
                    ledger.Add(KeyFor(line.CoverageId, line.MemberId), month, line.TotalPremium);
                }
            }
            // Only the calculator's own retro adjustments count as billing for a month;
            // a manual adjustment that happens to carry a service period is not reversed.
            foreach (var adjustment in invoice.Adjustments.Where(a => a.IsRatingRetro && a.ServicePeriodStart.HasValue))
                ledger.Add(KeyFor(adjustment.CoverageId, adjustment.RelatedMemberId), MonthStart(adjustment.ServicePeriodStart!.Value), adjustment.Amount);
        }
        return ledger;
    }

    public void MarkInvoiced(DateTime month) => _invoicedMonths.Add(MonthStart(month));

    public void Add(string coverageKey, DateTime month, decimal amount)
    {
        var key = (coverageKey, MonthStart(month));
        _billed[key] = _billed.GetValueOrDefault(key) + amount;
    }

    public decimal Billed(string coverageKey, DateTime month) => _billed.GetValueOrDefault((coverageKey, MonthStart(month)));

    public bool WasInvoiced(DateTime month) => _invoicedMonths.Contains(MonthStart(month));

    public IEnumerable<string> CoveragesBilledIn(DateTime month)
    {
        var m = MonthStart(month);
        return _billed.Keys.Where(k => k.Month == m).Select(k => k.Key).Distinct();
    }

    internal static string KeyFor(string? coverageId, string? memberId) =>
        !string.IsNullOrEmpty(coverageId) ? coverageId : $"member:{memberId}";

    internal static DateTime MonthStart(DateTime date) => new(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc);
}

public class InvoiceCalculationRequest
{
    public DateTime BillingPeriodStart { get; set; }

    /// <summary>
    /// The day the invoice is computed. <see cref="Enrollments"/> is the
    /// coverage as recorded on this day, including coverage terminated since
    /// the last invoice, so retro terms can be credited.
    /// </summary>
    public DateTime BillingDate { get; set; }

    public List<RatingEnrollment> Enrollments { get; set; } = new();

    public BilledLedger PriorBilling { get; set; } = BilledLedger.Empty;

    /// <summary>How many months before the billing period retro changes reach back.</summary>
    public int MaxRetroMonths { get; set; } = 3;

    /// <summary>Employer share of each line (0–100); the rest is the subscriber's.</summary>
    public decimal EmployerContributionPercent { get; set; }

    /// <summary>
    /// Coverage ids whose earlier months must not be reconciled this time
    /// (their enrollment data is unusable and reported elsewhere): without
    /// them, what was billed would otherwise be credited back as a retro term.
    /// </summary>
    public ISet<string> Unreconcilable { get; set; } = new HashSet<string>(StringComparer.Ordinal);
}

public class InvoiceCalculation
{
    public List<InvoiceLineItem> LineItems { get; set; } = new();
    public List<InvoiceAdjustment> Adjustments { get; set; } = new();

    /// <summary>Coverage-months that could not be charged (e.g. no rate table); left off the invoice.</summary>
    public List<InvoiceCalculationIssue> Issues { get; set; } = new();

    public decimal Subtotal => LineItems.Sum(l => l.TotalPremium);
    public decimal TotalAdjustments => Adjustments.Sum(a => a.Amount);
    public decimal Total => Subtotal + TotalAdjustments;

    /// <summary>Puts the computed lines and adjustments on an invoice and recalculates its totals.</summary>
    public void ApplyTo(PremiumInvoice invoice)
    {
        invoice.LineItems = LineItems;
        invoice.Adjustments = Adjustments;
        invoice.RecalculateTotals();
    }
}

/// <summary>
/// Computes a group's invoice for a billing month from rated coverage:
/// the current month's charge per coverage (prorated under the plan's
/// proration rule for mid-month adds and terms), plus one adjustment per
/// coverage and earlier invoiced month whose correct charge differs from what
/// was billed (retro adds, retro terms, tier/rate changes).
/// </summary>
/// <remarks>
/// Rounding, in full (money is <see cref="decimal"/>, rounded to the cent half
/// away from zero by <see cref="PremiumRatingEngine.RoundMoney"/>):
/// <list type="number">
/// <item>The engine rounds each member's premium (age band, composite tobacco); tier rates are exact.</item>
/// <item>Each charge segment (a run of days with one household rating and one rate-table version)
/// is rounded once: monthly premium × days ÷ denominator; a segment that is the whole month is the
/// monthly premium exactly. Adjacent segments with the same rate-table version and premium are merged
/// before rounding, so splitting a month never adds a cent.</item>
/// <item>An invoice line is the sum of its (already rounded) segments; it is not rounded again.</item>
/// <item>The employer share of a line is rounded; the subscriber share is the line minus it.</item>
/// <item>A retro adjustment is the correct charge (sum of rounded segments) minus what was billed.</item>
/// <item>The invoice total is the sum of its lines and adjustments; there is no invoice-level rounding.</item>
/// </list>
/// </remarks>
public sealed class PremiumInvoiceCalculator
{
    private readonly RateTableCatalog _rates;

    public PremiumInvoiceCalculator(RateTableCatalog rates)
    {
        _rates = rates;
    }

    public InvoiceCalculation Calculate(InvoiceCalculationRequest request)
    {
        if (request.MaxRetroMonths < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "MaxRetroMonths cannot be negative");
        if (request.EmployerContributionPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(request), "EmployerContributionPercent must be 0–100");

        var duplicate = request.Enrollments.GroupBy(e => e.CoverageId).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new ArgumentException($"Coverage {duplicate.Key} appears more than once", nameof(request));

        var period = BilledLedger.MonthStart(request.BillingPeriodStart);
        var result = new InvoiceCalculation();

        foreach (var enrollment in request.Enrollments.OrderBy(e => e.CoverageId, StringComparer.Ordinal))
        {
            if (!TryCharge(enrollment, period, result, out var month) || month == null)
                continue;
            foreach (var line in ToLines(enrollment, month, request.EmployerContributionPercent))
            {
                // Never a $0 line: a charge that rounds to nothing is a data problem to look at.
                if (line.TotalPremium <= 0m)
                {
                    result.Issues.Add(new InvoiceCalculationIssue(enrollment.CoverageId, period,
                        $"Coverage {enrollment.CoverageId} rated to {line.TotalPremium:0.00} for {period:yyyy-MM}",
                        InvoiceCalculationIssue.ZeroCharge));
                    continue;
                }
                result.LineItems.Add(line);
            }
        }

        // Retro: every earlier month that was invoiced, oldest first.
        var byKey = new Dictionary<string, RatingEnrollment>(StringComparer.Ordinal);
        foreach (var enrollment in request.Enrollments)
        {
            if (!TrySubscriberId(enrollment, period, result, out var subscriberId))
                continue;
            byKey[BilledLedger.KeyFor(enrollment.CoverageId, subscriberId)] = enrollment;
        }
        for (var back = request.MaxRetroMonths; back >= 1; back--)
        {
            var month = period.AddMonths(-back);
            if (!request.PriorBilling.WasInvoiced(month))
                continue;

            var keys = byKey.Keys.Union(request.PriorBilling.CoveragesBilledIn(month)).OrderBy(k => k, StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if (request.Unreconcilable.Contains(key))
                    continue;
                byKey.TryGetValue(key, out var enrollment);
                MonthCharge? charge = null;
                if (enrollment != null && !TryCharge(enrollment, month, result, out charge))
                    continue; // not reconciled this time: see result.Issues
                var expected = charge?.Amount ?? 0m;
                var billed = request.PriorBilling.Billed(key, month);
                var difference = expected - billed;
                if (difference == 0m)
                    continue;

                result.Adjustments.Add(new InvoiceAdjustment
                {
                    Type = AdjustmentKind(enrollment, month, expected, billed),
                    Amount = difference,
                    Description = DescribeAdjustment(enrollment, key, month, charge, expected, billed),
                    RelatedMemberId = enrollment?.Subscriber.MemberId ?? (key.StartsWith("member:") ? key[7..] : null),
                    CoverageId = enrollment?.CoverageId ?? (key.StartsWith("member:") ? null : key),
                    ServicePeriodStart = month,
                    ServicePeriodEnd = month.AddMonths(1).AddDays(-1),
                    AdjustmentDate = request.BillingDate,
                    IsRatingRetro = true,
                    RateTableVersions = charge?.Segments
                        .Select(s => RateTableReference.Of(s.Rating.RateTableId, s.Rating.RateTableVersion))
                        .Distinct().ToList()
                });
            }
        }

        return result;
    }

    private static bool TrySubscriberId(RatingEnrollment enrollment, DateTime period, InvoiceCalculation result, out string? subscriberId)
    {
        try
        {
            subscriberId = enrollment.Subscriber.MemberId;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            if (!result.Issues.Any(i => i.CoverageId == enrollment.CoverageId))
                result.Issues.Add(new InvoiceCalculationIssue(enrollment.CoverageId, period, ex.Message, InvoiceCalculationIssue.EnrollmentData));
            subscriberId = null;
            return false;
        }
    }

    /// <summary>
    /// Charges one coverage-month. A missing rate table, an invalid one or
    /// unusable enrollment data does not fail the group's invoice: that
    /// coverage-month is left out and reported in
    /// <see cref="InvoiceCalculation.Issues"/> for someone to fix and re-bill.
    /// </summary>
    private bool TryCharge(RatingEnrollment enrollment, DateTime month, InvoiceCalculation result, out MonthCharge? charge)
    {
        string code;
        string message;
        try
        {
            charge = ChargeForMonth(enrollment, month);
            return true;
        }
        catch (RateTableNotFoundException ex)
        {
            (code, message) = (InvoiceCalculationIssue.RateNotFound, ex.Message);
        }
        catch (RateTableValidationException ex)
        {
            (code, message) = (InvoiceCalculationIssue.RateTableInvalid, ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Two subscribers, no subscriber, no date of birth, a birth after the age date…
            (code, message) = (InvoiceCalculationIssue.EnrollmentData, ex.Message);
        }
        result.Issues.Add(new InvoiceCalculationIssue(enrollment.CoverageId, BilledLedger.MonthStart(month), message, code));
        charge = null;
        return false;
    }

    /// <summary>
    /// The coverage's charge for one month, or null if nothing is charged for
    /// it. The month is divided into billing units by the plan's proration
    /// rule (see <see cref="ProrationRule"/>); each unit is rated with the
    /// household of its first day and split again wherever the rate table
    /// changes, so a rate change applies from its effective date.
    /// </summary>
    public MonthCharge? ChargeForMonth(RatingEnrollment enrollment, DateTime monthStart)
    {
        var start = BilledLedger.MonthStart(monthStart);
        var end = start.AddMonths(1).AddDays(-1);
        var daysInMonth = DateTime.DaysInMonth(start.Year, start.Month);

        var from = enrollment.EffectiveDate.Date > start ? enrollment.EffectiveDate.Date : start;
        var to = enrollment.TerminationDate.HasValue && enrollment.TerminationDate.Value.Date < end
            ? enrollment.TerminationDate.Value.Date
            : end;
        if (from > to)
            return null;

        // The rule of the table in force on the first covered day. The catalog
        // only lets a plan's rule change on the 1st, so it is the month's rule.
        var rule = _rates.Resolve(enrollment.PlanId, from).Proration;
        var charge = new MonthCharge { MonthStart = start, DaysInMonth = daysInMonth, Rule = rule };

        var pieces = new List<Piece>();
        foreach (var unit in Units(enrollment, rule, start, end, from, to, daysInMonth))
        {
            var household = enrollment.CoveredOn(unit.Snapshot);
            if (!household.Members.Any(m => m.Relationship == MemberRelationship.Subscriber))
                continue;

            // Within a unit, split again at rate-table boundaries.
            var segmentStart = unit.Start;
            while (segmentStart <= unit.End)
            {
                var table = _rates.Resolve(enrollment.PlanId, segmentStart);
                var segmentEnd = table.EffectiveTo.HasValue && table.EffectiveTo.Value.Date < unit.End ? table.EffectiveTo.Value.Date : unit.End;
                var days = (segmentEnd - segmentStart).Days + 1;
                var rating = PremiumRatingEngine.Rate(table, household);
                pieces.Add(new Piece(segmentStart, segmentEnd, days, rating, Share.Of(days, unit.Denominator)));
                segmentStart = segmentEnd.AddDays(1);
            }
        }

        foreach (var piece in Merge(pieces))
        {
            var monthly = piece.Rating.MonthlyPremium;
            var amount = piece.Share.IsWhole
                ? monthly
                : PremiumRatingEngine.RoundMoney(monthly * piece.Share.Numerator / piece.Share.Denominator);
            charge.Segments.Add(new MonthChargeSegment(piece.From, piece.To, piece.Days, piece.Rating, amount)
            {
                ShareNumerator = piece.Share.Numerator,
                ShareDenominator = piece.Share.Denominator
            });
        }
        return charge.Segments.Count == 0 ? null : charge;
    }

    private readonly record struct Unit(DateTime Start, DateTime End, DateTime Snapshot, long Denominator);

    private sealed record Piece(DateTime From, DateTime To, int Days, RatingResult Rating, Share Share);

    /// <summary>The billing units of one coverage-month under a proration rule.</summary>
    private static IEnumerable<Unit> Units(RatingEnrollment enrollment, ProrationRule rule,
        DateTime start, DateTime end, DateTime from, DateTime to, int daysInMonth)
    {
        switch (rule)
        {
            case ProrationRule.FullMonth:
                // Charged in full when in force on the 1st.
                if (from == start)
                    yield return new Unit(start, end, start, daysInMonth);
                yield break;

            case ProrationRule.HalfMonth:
                var secondHalf = start.AddDays(15);
                foreach (var (halfStart, halfEnd) in new[] { (start, secondHalf.AddDays(-1)), (secondHalf, end) })
                {
                    if (from <= halfStart && to >= halfStart)
                    {
                        var halfDays = (halfEnd - halfStart).Days + 1;
                        // Each half is half the month: days ÷ (2 × days in the half).
                        yield return new Unit(halfStart, halfEnd, halfStart, 2L * halfDays);
                    }
                }
                yield break;

            default:
                // Daily: a new unit wherever the covered household changes.
                var breaks = new SortedSet<DateTime> { from };
                foreach (var member in enrollment.Members)
                {
                    var memberStart = enrollment.MemberStart(member);
                    if (memberStart > from && memberStart <= to) breaks.Add(memberStart);
                    if (enrollment.MemberEnd(member) is { } memberEnd && memberEnd >= from && memberEnd < to) breaks.Add(memberEnd.AddDays(1));
                }
                var points = breaks.ToList();
                for (var i = 0; i < points.Count; i++)
                {
                    var unitEnd = i + 1 < points.Count ? points[i + 1].AddDays(-1) : to;
                    yield return new Unit(points[i], unitEnd, points[i], daysInMonth);
                }
                yield break;
        }
    }

    /// <summary>
    /// Joins adjacent pieces billed at the same rate-table version, tier and
    /// monthly premium (e.g. the two halves of a half-month month, or a fourth
    /// child under 21 joining), so they are rounded once together.
    /// </summary>
    private static IEnumerable<Piece> Merge(List<Piece> pieces)
    {
        Piece? current = null;
        foreach (var piece in pieces)
        {
            if (current != null
                && current.To.AddDays(1) == piece.From
                && current.Rating.RateTableId == piece.Rating.RateTableId
                && current.Rating.RateTableVersion == piece.Rating.RateTableVersion
                && current.Rating.Tier == piece.Rating.Tier
                && current.Rating.MonthlyPremium == piece.Rating.MonthlyPremium)
            {
                current = current with
                {
                    To = piece.To,
                    Days = current.Days + piece.Days,
                    Rating = piece.Rating,
                    Share = current.Share.Plus(piece.Share)
                };
                continue;
            }
            if (current != null)
                yield return current;
            current = piece;
        }
        if (current != null)
            yield return current;
    }

    /// <summary>
    /// List-bill lines of one coverage-month: one per rate-table version used
    /// (normally one; two when a rate change takes effect mid-month), each
    /// recording the version it was rated with and its segments.
    /// </summary>
    private static IEnumerable<InvoiceLineItem> ToLines(RatingEnrollment enrollment, MonthCharge charge, decimal employerPercent)
    {
        var subscriber = enrollment.Subscriber;
        var groups = new List<List<MonthChargeSegment>>();
        foreach (var segment in charge.Segments)
        {
            var last = groups.Count > 0 ? groups[^1][^1] : null;
            if (last != null && last.Rating.RateTableId == segment.Rating.RateTableId
                             && last.Rating.RateTableVersion == segment.Rating.RateTableVersion)
                groups[^1].Add(segment);
            else
                groups.Add(new List<MonthChargeSegment> { segment });
        }

        foreach (var segments in groups)
        {
            var rating = segments[^1].Rating;
            var amount = segments.Sum(s => s.Amount);
            var share = segments.Select(s => Share.Of(s.ShareNumerator, s.ShareDenominator)).Aggregate((a, b) => a.Plus(b));
            var employer = PremiumRatingEngine.RoundMoney(amount * employerPercent / 100m);
            var days = segments.Sum(s => s.Days);
            yield return new InvoiceLineItem
            {
                MemberId = subscriber.MemberId,
                MemberName = subscriber.MemberName ?? subscriber.MemberId,
                CoverageId = enrollment.CoverageId,
                PlanId = enrollment.PlanId,
                CoverageLevel = TierCode(rating.Tier),
                InsuranceLineCode = enrollment.InsuranceLineCode,
                TotalPremium = amount,
                EmployerContribution = employer,
                SubscriberPremium = amount - employer,
                EffectiveDate = enrollment.EffectiveDate,
                TerminationDate = enrollment.TerminationDate,
                ProrationFactor = Math.Round((decimal)share.Numerator / share.Denominator, 4, MidpointRounding.AwayFromZero),
                IsRetroactive = false,
                AdjustmentReason = share.IsWhole
                    ? null
                    : charge.Rule switch
                    {
                        ProrationRule.HalfMonth => "Prorated: half month",
                        _ => $"Prorated: {days}/{charge.DaysInMonth} days"
                    },
                RateTableId = rating.RateTableId,
                RateTableVersion = rating.RateTableVersion,
                RateTableHash = rating.RateTableHash,
                RatingMethod = rating.Method.ToString(),
                ProrationRule = charge.Rule.ToString(),
                ServicePeriodStart = segments[0].From,
                ServicePeriodEnd = segments[^1].To,
                RatingSegments = segments.Select(s => new InvoiceRatingSegment
                {
                    From = s.From,
                    To = s.To,
                    Days = s.Days,
                    CoverageLevel = TierCode(s.Rating.Tier),
                    MonthlyPremium = s.Rating.MonthlyPremium,
                    Amount = s.Amount,
                    Basis = s.ShareNumerator == s.ShareDenominator
                        ? "full month"
                        : $"{s.Rating.MonthlyPremium:0.00} × {s.ShareNumerator}/{s.ShareDenominator}",
                    RatedMembers = s.Rating.Members.Count(m => m.Rated)
                }).ToList()
            };
        }
    }

    private static AdjustmentType AdjustmentKind(RatingEnrollment? enrollment, DateTime month, decimal expected, decimal billed)
    {
        if (billed == 0m && expected > 0m)
            return AdjustmentType.RetroAdd;
        if (expected == 0m)
            return AdjustmentType.RetroTerm;
        var monthEnd = month.AddMonths(1).AddDays(-1);
        if (expected < billed && enrollment?.TerminationDate is { } term && term.Date >= month && term.Date < monthEnd)
            return AdjustmentType.RetroTerm;
        if (expected > billed && enrollment != null && enrollment.EffectiveDate.Date > month && enrollment.EffectiveDate.Date <= monthEnd)
            return AdjustmentType.RetroAdd;
        return AdjustmentType.RateChange;
    }

    private static string DescribeAdjustment(RatingEnrollment? enrollment, string key, DateTime month, MonthCharge? charge,
        decimal expected, decimal billed)
    {
        var who = enrollment == null
            ? $"coverage {key}"
            : $"{enrollment.Subscriber.MemberName ?? enrollment.Subscriber.MemberId} ({enrollment.CoverageId})";
        var basis = charge == null
            ? "not covered"
            : !charge.IsProrated ? "full month"
            : charge.Rule == ProrationRule.HalfMonth ? "half month"
            : $"{charge.CoveredDays}/{charge.DaysInMonth} days";
        return $"{month:yyyy-MM} {who}: due {expected:0.00} ({basis}), billed {billed:0.00}";
    }

    public static string TierCode(CoverageTier tier) => tier switch
    {
        CoverageTier.EmployeeOnly => "EMP",
        CoverageTier.EmployeeSpouse => "ESP",
        CoverageTier.EmployeeChildren => "ECH",
        CoverageTier.Family => "FAM",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null)
    };
}

public sealed record InvoiceCalculationIssue(string CoverageId, DateTime Month, string Message, string Code = InvoiceCalculationIssue.RateNotFound)
{
    /// <summary>No rate table of the plan covers a day that must be charged (missing or expired rates).</summary>
    public const string RateNotFound = "RATE_NOT_FOUND";

    /// <summary>The plan's rate tables are invalid or conflict (overlapping periods, bad proration change).</summary>
    public const string RateTableInvalid = "RATE_TABLE_INVALID";

    /// <summary>The coverage cannot be rated from the enrollment data (no subscriber, no date of birth…).</summary>
    public const string EnrollmentData = "ENROLLMENT_DATA";

    /// <summary>The coverage rated to $0 for the month.</summary>
    public const string ZeroCharge = "ZERO_CHARGE";
}

public sealed class MonthCharge
{
    public DateTime MonthStart { get; init; }
    public int DaysInMonth { get; init; }
    public ProrationRule Rule { get; init; }
    public List<MonthChargeSegment> Segments { get; } = new();
    public int CoveredDays => Segments.Sum(s => s.Days);

    /// <summary>Less than the whole month is charged (by share of the month, not by days).</summary>
    public bool IsProrated => !Segments.Select(s => Share.Of(s.ShareNumerator, s.ShareDenominator))
        .Aggregate(Share.Zero, (a, b) => a.Plus(b)).IsWhole;

    public decimal Amount => Segments.Sum(s => s.Amount);
}

public sealed record MonthChargeSegment(DateTime From, DateTime To, int Days, RatingResult Rating, decimal Amount)
{
    /// <summary>Share of the month charged: <see cref="ShareNumerator"/> ÷ <see cref="ShareDenominator"/>.</summary>
    public long ShareNumerator { get; init; }

    public long ShareDenominator { get; init; } = 1;
}

/// <summary>An exact fraction of a month (no decimal division until the amount is rounded).</summary>
internal readonly record struct Share(long Numerator, long Denominator)
{
    public static Share Zero => new(0, 1);

    public bool IsWhole => Numerator == Denominator;

    public static Share Of(long numerator, long denominator)
    {
        if (denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(denominator));
        var gcd = Gcd(Math.Abs(numerator), denominator);
        return gcd == 0 ? new Share(0, 1) : new Share(numerator / gcd, denominator / gcd);
    }

    public Share Plus(Share other) =>
        Of(Numerator * other.Denominator + other.Numerator * Denominator, Denominator * other.Denominator);

    private static long Gcd(long a, long b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }
}
