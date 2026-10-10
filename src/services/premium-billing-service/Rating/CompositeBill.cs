using PremiumBillingService.Models;

namespace PremiumBillingService.Rating;

/// <summary>
/// Composite (per-tier) presentation of rated list-bill lines. Full-month
/// lines with the same plan, line of coverage, tier, rate-table version and
/// amount become one line: quantity × unit rate, with a component per
/// coverage so later invoices still know what each coverage was billed.
/// Prorated lines (a partial month, or a month split by a rate change) keep
/// their own line. The total never changes: each composite line is the exact
/// sum of the list-bill lines it replaces.
/// </summary>
public static class CompositeBill
{
    public static List<InvoiceLineItem> Collapse(IReadOnlyList<InvoiceLineItem> lines)
    {
        var result = new List<InvoiceLineItem>();
        var groups = new Dictionary<string, List<InvoiceLineItem>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var line in lines)
        {
            if (!IsFullMonth(line))
            {
                result.Add(line);
                continue;
            }
            var key = string.Join('|', line.PlanId, line.InsuranceLineCode, line.CoverageLevel, line.RateTableId,
                line.RateTableVersion, line.TotalPremium, line.EmployerContribution);
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = new List<InvoiceLineItem>();
                order.Add(key);
            }
            list.Add(line);
        }

        var composite = order.Select(key =>
        {
            var members = groups[key];
            var first = members[0];
            var total = members.Sum(l => l.TotalPremium);
            if (total != first.TotalPremium * members.Count)
                throw new InvalidOperationException("Composite line does not equal quantity × unit rate");
            return new InvoiceLineItem
            {
                MemberId = string.Empty,
                MemberName = $"{first.PlanId} {first.CoverageLevel} × {members.Count}",
                CoverageId = null,
                PlanId = first.PlanId,
                CoverageLevel = first.CoverageLevel,
                InsuranceLineCode = first.InsuranceLineCode,
                Quantity = members.Count,
                UnitRate = first.TotalPremium,
                TotalPremium = total,
                EmployerContribution = members.Sum(l => l.EmployerContribution),
                SubscriberPremium = members.Sum(l => l.SubscriberPremium),
                EffectiveDate = members.Min(l => l.EffectiveDate),
                ProrationFactor = 1.0m,
                RateTableId = first.RateTableId,
                RateTableVersion = first.RateTableVersion,
                RateTableHash = first.RateTableHash,
                RatingMethod = first.RatingMethod,
                ProrationRule = first.ProrationRule,
                ServicePeriodStart = first.ServicePeriodStart,
                ServicePeriodEnd = first.ServicePeriodEnd,
                Components = members.Select(l => new InvoiceLineComponent
                {
                    CoverageId = l.CoverageId ?? string.Empty,
                    MemberId = l.MemberId,
                    MemberName = l.MemberName,
                    Amount = l.TotalPremium
                }).ToList()
            };
        }).ToList();

        // Tier lines first (in the order their first coverage appeared), then the prorated lines.
        composite.AddRange(result);
        return composite;
    }

    private static bool IsFullMonth(InvoiceLineItem line) =>
        line.ProrationFactor == 1.0m && line.AdjustmentReason == null && line.RatingSegments is { Count: 1 }
        && line.RatingSegments[0].Basis == "full month";
}
