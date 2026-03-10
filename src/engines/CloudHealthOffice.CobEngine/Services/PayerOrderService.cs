using CloudHealthOffice.CobEngine.Domain;

namespace CloudHealthOffice.CobEngine.Services;

/// <summary>
/// Determines COB payer sequence using standard industry rules.
///
/// Rule priority (applied in order, first match wins):
///
///   1. Explicit record  — coverage record has IsPrimaryPayer explicitly set
///   2. MSP rules        — Medicare Secondary Payer rules take precedence over birthday rule
///      a. Active employment at LGHP (≥20 employees) → employer plan is primary, Medicare secondary
///      b. Otherwise → Medicare is primary (retired, small employer, etc.)
///   3. Active employment — actively-employed plan beats non-active (retired/COBRA) plan
///   4. Birthday rule    — earlier birthday (month/day) in the calendar year → primary
///      Tiebreaker: longer coverage duration → primary
///   5. Default          — this coverage is primary if it is the only coverage
///
/// References:
///   NAIC Model Regulation §120 (Group Health Insurance — COB)
///   CMS Medicare Secondary Payer Fact Sheet
/// </summary>
public class PayerOrderService : IPayerOrderService
{
    public PayerOrderResult DetermineOrder(
        InsuredInfo thisCoverage,
        IReadOnlyList<InsuredInfo> allCoverages)
    {
        // Only one coverage → always primary
        if (allCoverages.Count <= 1)
            return Primary(PayerOrderRule.ExplicitCoverageRecord,
                "Single coverage — no coordination needed");

        // ── Rule 1: Explicit designation on coverage record ──────────────
        // Only applies when the other payer is Medicare and the flag is set
        if (thisCoverage.IsMedicare)
        {
            if (thisCoverage.MedicareDesignatedPrimary)
                return Primary(PayerOrderRule.ExplicitCoverageRecord,
                    "Medicare designated primary per coverage record");

            // Check MSP: if any other plan is active-employer LGHP, Medicare is secondary
            var otherActiveEmployerLghp = allCoverages
                .Where(c => c.MemberId != thisCoverage.MemberId || c.PayerId != thisCoverage.PayerId)
                .Any(c => c.IsActiveEmployee && c.IsLargeGroupHealthPlan && !c.IsMedicare);

            if (otherActiveEmployerLghp)
                return Secondary(PayerOrderRule.MedicareSecondaryPayer,
                    "Medicare is secondary: member has active large-group employer coverage (MSP)");

            return Primary(PayerOrderRule.MedicarePrimary,
                "Medicare is primary: no active large-group employer coverage");
        }

        // ── Rule 2: Active-employment beats inactive ─────────────────────
        var otherCoverages = allCoverages
            .Where(c => !(c.MemberId == thisCoverage.MemberId && c.PayerId == thisCoverage.PayerId))
            .ToList();

        var anyOtherMedicareAsPrimary = otherCoverages
            .Any(c => c.IsMedicare && c.MedicareDesignatedPrimary);
        if (anyOtherMedicareAsPrimary)
            return Secondary(PayerOrderRule.MedicareSecondaryPayer,
                "Other coverage is Medicare designated primary");

        var anyOtherActiveEmployee = otherCoverages
            .Any(c => c.IsActiveEmployee && !c.IsMedicare);

        if (thisCoverage.IsActiveEmployee && !anyOtherActiveEmployee)
            return Primary(PayerOrderRule.ActiveEmployment,
                "This plan is from active employment; other plans are not");

        if (!thisCoverage.IsActiveEmployee && anyOtherActiveEmployee)
            return Secondary(PayerOrderRule.ActiveEmployment,
                "Another plan is from active employment; this plan is inactive/COBRA");

        // ── Rule 3: Birthday rule ────────────────────────────────────────
        if (thisCoverage.PolicyholderBirthDate.HasValue)
        {
            var thisMonthDay = (thisCoverage.PolicyholderBirthDate.Value.Month,
                               thisCoverage.PolicyholderBirthDate.Value.Day);

            foreach (var other in otherCoverages.Where(c => c.PolicyholderBirthDate.HasValue))
            {
                var otherMonthDay = (other.PolicyholderBirthDate!.Value.Month,
                                    other.PolicyholderBirthDate!.Value.Day);

                if (thisMonthDay == otherMonthDay)
                {
                    // Same birthday — longer duration wins
                    if (thisCoverage.CoverageEffectiveDate.HasValue &&
                        other.CoverageEffectiveDate.HasValue)
                    {
                        if (thisCoverage.CoverageEffectiveDate < other.CoverageEffectiveDate)
                            return Primary(PayerOrderRule.LongerDuration,
                                "Same birthday — this plan has been in effect longer");

                        if (thisCoverage.CoverageEffectiveDate > other.CoverageEffectiveDate)
                            return Secondary(PayerOrderRule.LongerDuration,
                                "Same birthday — other plan has been in effect longer");
                    }
                    // Cannot determine — default to primary
                    return Primary(PayerOrderRule.BirthdayRule,
                        "Same birthday — cannot determine duration; defaulting to primary");
                }

                var thisJanOrdinal = thisMonthDay.Month * 100 + thisMonthDay.Day;
                var otherJanOrdinal = otherMonthDay.Month * 100 + otherMonthDay.Day;

                if (thisJanOrdinal < otherJanOrdinal)
                    return Primary(PayerOrderRule.BirthdayRule,
                        $"Birthday rule: this policyholder birthday ({thisMonthDay.Month}/{thisMonthDay.Day}) is earlier in the year");

                if (thisJanOrdinal > otherJanOrdinal)
                    return Secondary(PayerOrderRule.BirthdayRule,
                        $"Birthday rule: other policyholder birthday ({otherMonthDay.Month}/{otherMonthDay.Day}) is earlier in the year");
            }
        }

        // ── Default: primary ─────────────────────────────────────────────
        return Primary(PayerOrderRule.ExplicitCoverageRecord,
            "Could not determine order by rule — defaulting to primary");
    }

    private static PayerOrderResult Primary(PayerOrderRule rule, string explanation) =>
        new() { PayerSequence = PayerSequenceCode.Primary, Rule = rule, Explanation = explanation };

    private static PayerOrderResult Secondary(PayerOrderRule rule, string explanation) =>
        new() { PayerSequence = PayerSequenceCode.Secondary, Rule = rule, Explanation = explanation };
}
