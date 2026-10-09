using CloudHealthOffice.CobEngine.Domain;

namespace CloudHealthOffice.CobEngine.Services;

/// <summary>
/// Implements the two standard COB calculation models used in commercial health insurance.
///
/// COMPLEMENTARY (most common — default for commercial plans):
///   The secondary payer fills the gap between the primary's payment and the
///   total billed charges, subject to its own benefit limits.
///
///   1. effectiveBalance   = max(0, billedAmount - primaryPayerPayment)
///   2. secondaryPlanPay   = min(secondaryPlanPayBeforeCob, effectiveBalance)
///
///   Effect: total paid never exceeds billed; secondary absorbs up to its own allowed
///   minus what primary already paid.
///
/// NON-DUPLICATION:
///   Secondary only pays if its own benefit (what it would have paid as primary) exceeds
///   what the primary actually paid. No windfall to the provider.
///
///   1. maxSecondaryBenefit = secondaryAllowed - secondaryMemberRespBeforeCob
///      (what secondary would have paid if it were primary)
///   2. If primaryPayment >= maxSecondaryBenefit → secondary pays nothing
///   3. Otherwise → secondary pays (maxSecondaryBenefit - primaryPayment)
///
/// MEMBER RESPONSIBILITY (both models):
///   memberResp = min(secondaryMemberRespBeforeCob,
///                    max(0, secondaryAllowed - primaryPayment - secondaryPlanPay))
///
///   The member owes the part of this plan's ALLOWED amount that neither payer
///   paid, and never more than the cost share this plan would have charged as
///   the only payer. Billed − allowed is the provider's contractual write-off
///   (CO-45), not member liability, so the balance is measured against allowed,
///   not billed. Complementary COB therefore usually shrinks the member's cost
///   share (the primary's payment covers it first); non-duplication leaves it
///   whole unless the primary paid more than this plan's own benefit.
///
/// CAS segment (835 reporting):
///   <see cref="CobLineResult.CobReduction"/> is this plan's COB savings
///   (pre-COB payment − secondary payment). The OA-23 ("Impact of prior payer
///   adjudication") a caller reports is that saving plus the cost share the
///   member no longer owes (secondaryMemberRespBeforeCob − memberResp) — i.e.
///   allowed − memberResp − secondaryPlanPay — so that with the PR entries
///   reduced to memberResp the line still balances (charge − ΣCAS = paid).
///   Both parts are non-negative, so OA-23 is never negative.
/// </summary>
public class CobCalculationService : ICobCalculationService
{
    public CobLineResult Calculate(CobLineInput input) => input.Model switch
    {
        CobModel.NonDuplication => ApplyNonDuplication(input),
        _                       => ApplyComplementary(input)
    };

    public IReadOnlyList<CobLineResult> CalculateAll(IEnumerable<CobLineInput> lines) =>
        lines.Select(Calculate).ToList();

    // ── Complementary ─────────────────────────────────────────────────────

    private static CobLineResult ApplyComplementary(CobLineInput i)
    {
        // How much is still "owed" after primary paid
        var effectiveBalance = Math.Max(0, i.BilledAmount - i.PrimaryPayerPayment);

        // Secondary can pay at most its own waterfall result, and at most the balance
        var secondaryPay = Math.Min(i.SecondaryPlanPaymentBeforeCob, effectiveBalance);

        // Reduction = what the secondary intended to pay vs. what it actually pays after COB
        var cobReduction = i.SecondaryPlanPaymentBeforeCob - secondaryPay;

        var memberResp = MemberResponsibility(i, secondaryPay);

        return new CobLineResult
        {
            LineNumber          = i.LineNumber,
            PrimaryPayerPayment = i.PrimaryPayerPayment,
            SecondaryPlanPayment = secondaryPay,
            MemberResponsibility = memberResp,
            CobReduction        = cobReduction,
            CobApplied          = cobReduction != 0
                                  || memberResp != i.SecondaryMemberResponsibilityBeforeCob
        };
    }

    // ── Non-duplication ───────────────────────────────────────────────────

    private static CobLineResult ApplyNonDuplication(CobLineInput i)
    {
        // What secondary would have paid if it were the only payer
        var maxSecondaryBenefit = Math.Max(0,
            i.SecondaryAllowedAmount - i.SecondaryMemberResponsibilityBeforeCob);

        decimal secondaryPay;
        decimal cobReduction;

        if (i.PrimaryPayerPayment >= maxSecondaryBenefit)
        {
            // Primary paid at least as much as secondary would have — secondary pays nothing
            secondaryPay = 0;
            cobReduction = i.SecondaryPlanPaymentBeforeCob;
        }
        else
        {
            // Secondary tops up to its max benefit
            secondaryPay = maxSecondaryBenefit - i.PrimaryPayerPayment;
            cobReduction = i.SecondaryPlanPaymentBeforeCob - secondaryPay;
        }

        var memberResp = MemberResponsibility(i, secondaryPay);

        return new CobLineResult
        {
            LineNumber           = i.LineNumber,
            PrimaryPayerPayment  = i.PrimaryPayerPayment,
            SecondaryPlanPayment = secondaryPay,
            MemberResponsibility = memberResp,
            CobReduction         = cobReduction,
            CobApplied           = cobReduction != 0
                                   || memberResp != i.SecondaryMemberResponsibilityBeforeCob
        };
    }

    // ── Member responsibility ─────────────────────────────────────────────

    /// <summary>
    /// What the member still owes after both payers: the part of this plan's
    /// allowed amount neither payer paid, capped at the member's pre-COB cost
    /// share (COB never makes the member owe more than this plan alone would)
    /// and floored at zero. See the class summary.
    /// </summary>
    private static decimal MemberResponsibility(CobLineInput i, decimal secondaryPay) =>
        Math.Min(
            Math.Max(0, i.SecondaryMemberResponsibilityBeforeCob),
            Math.Max(0, i.SecondaryAllowedAmount - i.PrimaryPayerPayment - secondaryPay));
}
