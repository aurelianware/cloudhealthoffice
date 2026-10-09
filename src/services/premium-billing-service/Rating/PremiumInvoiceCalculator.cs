using PremiumBillingService.Models;

namespace PremiumBillingService.Rating;

/// <summary>
/// What earlier invoices of a group already billed, per coverage and
/// coverage month. Built from the group's invoices; voided invoices count
/// for nothing.
/// </summary>
public sealed class BilledLedger
{
    private readonly Dictionary<(string Key, DateTime Month), decimal> _billed = new();
    private readonly HashSet<DateTime> _invoicedMonths = new();

    public static BilledLedger Empty => new();

    /// <summary>Months that have a (non-void) invoice; only those are reconciled.</summary>
    public IReadOnlyCollection<DateTime> InvoicedMonths => _invoicedMonths;

    public static BilledLedger FromInvoices(IEnumerable<PremiumInvoice> invoices)
    {
        var ledger = new BilledLedger();
        foreach (var invoice in invoices.Where(i => i.Status != InvoiceStatus.Voided))
        {
            var month = MonthStart(invoice.BillingPeriodStart);
            ledger.MarkInvoiced(month);
            foreach (var line in invoice.LineItems)
                ledger.Add(KeyFor(line.CoverageId, line.MemberId), month, line.TotalPremium);
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
/// the current month's charge per coverage (prorated daily for mid-month
/// adds and terms), plus one adjustment per coverage and earlier invoiced
/// month whose correct charge differs from what was billed (retro adds,
/// retro terms, tier/rate changes).
/// </summary>
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
            result.LineItems.Add(ToLine(enrollment, month, request.EmployerContributionPercent, isRetro: false));
        }

        // Retro: every earlier month that was invoiced, oldest first.
        var byKey = request.Enrollments.ToDictionary(e => BilledLedger.KeyFor(e.CoverageId, e.Subscriber.MemberId));
        for (var back = request.MaxRetroMonths; back >= 1; back--)
        {
            var month = period.AddMonths(-back);
            if (!request.PriorBilling.WasInvoiced(month))
                continue;

            var keys = byKey.Keys.Union(request.PriorBilling.CoveragesBilledIn(month)).OrderBy(k => k, StringComparer.Ordinal);
            foreach (var key in keys)
            {
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
                    IsRatingRetro = true
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Charges one coverage-month. A missing rate table does not fail the
    /// group's invoice: that coverage-month is left out and reported in
    /// <see cref="InvoiceCalculation.Issues"/> for someone to fix and re-bill.
    /// </summary>
    private bool TryCharge(RatingEnrollment enrollment, DateTime month, InvoiceCalculation result, out MonthCharge? charge)
    {
        try
        {
            charge = ChargeForMonth(enrollment, month);
            return true;
        }
        catch (RateTableNotFoundException ex)
        {
            result.Issues.Add(new InvoiceCalculationIssue(enrollment.CoverageId, BilledLedger.MonthStart(month), ex.Message));
            charge = null;
            return false;
        }
    }

    /// <summary>
    /// The coverage's charge for one month, or null if it covers no day of it.
    /// The month is split wherever the rated household or the rate table
    /// changes (a member added or ended mid-month, a new rate period), and each
    /// piece is rated with only the members covered then, prorated by day.
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

        // Days on which the household changes.
        var breaks = new SortedSet<DateTime> { from };
        foreach (var member in enrollment.Members)
        {
            var memberStart = enrollment.MemberStart(member);
            if (memberStart > from && memberStart <= to) breaks.Add(memberStart);
            if (enrollment.MemberEnd(member) is { } memberEnd && memberEnd >= from && memberEnd < to) breaks.Add(memberEnd.AddDays(1));
        }

        var charge = new MonthCharge { MonthStart = start, DaysInMonth = daysInMonth };
        var points = breaks.ToList();
        for (var i = 0; i < points.Count; i++)
        {
            var pieceEnd = i + 1 < points.Count ? points[i + 1].AddDays(-1) : to;
            // Within a piece, split again at rate-table boundaries.
            var segmentStart = points[i];
            while (segmentStart <= pieceEnd)
            {
                var table = _rates.Resolve(enrollment.PlanId, segmentStart);
                var segmentEnd = table.EffectiveTo.HasValue && table.EffectiveTo.Value.Date < pieceEnd ? table.EffectiveTo.Value.Date : pieceEnd;
                var household = enrollment.CoveredOn(segmentStart);
                if (household.Members.Any(m => m.Relationship == MemberRelationship.Subscriber))
                {
                    var days = (segmentEnd - segmentStart).Days + 1;
                    var rating = PremiumRatingEngine.Rate(table, household);
                    var amount = days == daysInMonth
                        ? rating.MonthlyPremium
                        : PremiumRatingEngine.RoundMoney(rating.MonthlyPremium * days / daysInMonth);
                    charge.Segments.Add(new MonthChargeSegment(segmentStart, segmentEnd, days, rating, amount));
                }
                segmentStart = segmentEnd.AddDays(1);
            }
        }
        return charge.Segments.Count == 0 ? null : charge;
    }

    private static InvoiceLineItem ToLine(RatingEnrollment enrollment, MonthCharge charge, decimal employerPercent, bool isRetro)
    {
        var subscriber = enrollment.Subscriber;
        var rating = charge.Segments[^1].Rating;
        var employer = PremiumRatingEngine.RoundMoney(charge.Amount * employerPercent / 100m);
        return new InvoiceLineItem
        {
            MemberId = subscriber.MemberId,
            MemberName = subscriber.MemberName ?? subscriber.MemberId,
            CoverageId = enrollment.CoverageId,
            PlanId = enrollment.PlanId,
            CoverageLevel = TierCode(rating.Tier),
            InsuranceLineCode = enrollment.InsuranceLineCode,
            TotalPremium = charge.Amount,
            EmployerContribution = employer,
            SubscriberPremium = charge.Amount - employer,
            EffectiveDate = enrollment.EffectiveDate,
            TerminationDate = enrollment.TerminationDate,
            ProrationFactor = Math.Round((decimal)charge.CoveredDays / charge.DaysInMonth, 4),
            IsRetroactive = isRetro,
            AdjustmentReason = charge.IsProrated
                ? $"Prorated: {charge.CoveredDays}/{charge.DaysInMonth} days"
                : null
        };
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
            : charge.IsProrated ? $"{charge.CoveredDays}/{charge.DaysInMonth} days" : "full month";
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

public sealed record InvoiceCalculationIssue(string CoverageId, DateTime Month, string Message);

public sealed class MonthCharge
{
    public DateTime MonthStart { get; init; }
    public int DaysInMonth { get; init; }
    public List<MonthChargeSegment> Segments { get; } = new();
    public int CoveredDays => Segments.Sum(s => s.Days);
    public bool IsProrated => CoveredDays < DaysInMonth;
    public decimal Amount => Segments.Sum(s => s.Amount);
}

public sealed record MonthChargeSegment(DateTime From, DateTime To, int Days, RatingResult Rating, decimal Amount);
