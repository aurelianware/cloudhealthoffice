using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Formatters;

/// <summary>
/// Formats a <see cref="NetworkTierBenefit"/> into the short string rendered
/// in the Benefits tab. Split out of MemberDetailsDialog.razor so the logic
/// — specifically the zero-dollar handling — can be unit-tested directly.
///
/// $0 values are meaningful (ACA preventive, tier-1 generics, plan-level
/// waiver). They must be displayed, not hidden. Null values mean the field
/// is absent; those are suppressed.
/// </summary>
public static class BenefitCostShareFormatter
{
    public static string Format(NetworkTierBenefit tier)
    {
        if (tier == null)
        {
            return "—";
        }

        var parts = new List<string>();

        if (tier.Copay.HasValue)
        {
            parts.Add(tier.Copay.Value == 0m
                ? "No copay"
                : $"${tier.Copay.Value:F0} copay");
        }

        if (tier.Coinsurance.HasValue)
        {
            var pct = tier.Coinsurance.Value <= 1m
                ? tier.Coinsurance.Value * 100m
                : tier.Coinsurance.Value;
            parts.Add(pct == 0m
                ? "No coinsurance"
                : $"{pct:F0}% coinsurance");
        }

        if (parts.Count == 0)
        {
            return "—";
        }

        // Both explicit zeros → single "No charge" label (matches ACA
        // preventive benefit language).
        if (tier.Copay is 0m && tier.Coinsurance is 0m)
        {
            return "No charge";
        }

        return string.Join(" · ", parts);
    }
}
