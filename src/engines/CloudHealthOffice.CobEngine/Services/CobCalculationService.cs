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
///   3. memberResp         = max(0, billedAmount - primaryPayerPayment - secondaryPlanPay)
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
///   4. memberResp = max(0, billedAmount - primaryPayment - secondaryPlanPay)
///
/// CAS segment (835 reporting):
///   The COB reduction is reported as OA-23 ("Impact of prior payer adjudication").
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

        var memberResp = Math.Max(0, i.BilledAmount - i.PrimaryPayerPayment - secondaryPay);

        return new CobLineResult
        {
            LineNumber          = i.LineNumber,
            PrimaryPayerPayment = i.PrimaryPayerPayment,
            SecondaryPlanPayment = secondaryPay,
            MemberResponsibility = memberResp,
            CobReduction        = cobReduction,
            CobApplied          = cobReduction != 0
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

        var memberResp = Math.Max(0, i.BilledAmount - i.PrimaryPayerPayment - secondaryPay);

        return new CobLineResult
        {
            LineNumber           = i.LineNumber,
            PrimaryPayerPayment  = i.PrimaryPayerPayment,
            SecondaryPlanPayment = secondaryPay,
            MemberResponsibility = memberResp,
            CobReduction         = cobReduction,
            CobApplied           = cobReduction != 0
        };
    }
}
