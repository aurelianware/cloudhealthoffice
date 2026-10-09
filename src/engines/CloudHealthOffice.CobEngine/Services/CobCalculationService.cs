using CloudHealthOffice.CobEngine.Domain;

namespace CloudHealthOffice.CobEngine.Services;

/// <summary>
/// Applies the two COB calculation models this engine supports to one unit
/// (a claim line, or a whole DRG stay) when this plan is secondary, tertiary
/// or later — any number of prior payers.
///
/// <para><b>Source.</b> NAIC Coordination of Benefits Model Regulation
/// (MDL-120, 2013 revision):</para>
/// <list type="bullet">
///   <item><description>§7: the secondary plan "shall calculate the benefits
///     it would have paid on the claim in the absence of other health care
///     coverage and apply that calculated amount to any allowable expense
///     under its plan that is unpaid by the primary plan. The secondary plan
///     may reduce its payment by the amount so that, when combined with the
///     amount paid by the primary plan, the total benefits paid or provided
///     by all plans for the claim do not exceed 100 percent of the total
///     allowable expense for that claim."</description></item>
///   <item><description>§6.A(4): "If a person is covered by more than one
///     secondary plan ... Each secondary plan shall take into consideration
///     the benefits of the primary plan or plans and the benefits of any
///     other plan, which, under the rules of this regulation, has its
///     benefits determined before those of that secondary plan." So for a
///     tertiary (or later) plan, "unpaid by the primary plan" is unpaid by
///     every earlier payer: prior paid = the sum of all earlier payers'
///     payments.</description></item>
///   <item><description>§3.A: allowable expense is an expense covered at
///     least in part by a plan, and "any expense that a provider by law or in
///     accordance with a contractual agreement is prohibited from charging a
///     covered person is not an allowable expense". This plan measures
///     against its own allowed amount (billed − allowed is the CO-45
///     contractual write-off).</description></item>
/// </list>
///
/// <para><b>Definitions</b> (per unit):</para>
/// <code>
///   priorPaid   = Σ PaidAmount over every prior payer
///   lastPriorPR = PatientResponsibility of the prior payer with the highest
///                 sequence (null when the claim does not report its CAS)
///   balance     = max(0, allowed − priorPaid), and when lastPriorPR is known
///                 min(that, max(0, lastPriorPR))
///   normal      = this plan's payment had it been the only plan (allowed −
///                 its own cost share, after the OOP cap)
/// </code>
/// <para><c>balance</c> is the allowable expense no earlier payer paid and
/// the member still owes after them. The last prior payer's patient
/// responsibility is, by construction of its 835, what the member owes after
/// every payer up to and including it (each payer's PR already reflects the
/// payers before it), so when it is reported it bounds the balance: the
/// provider cannot collect more from the member than that, and no plan
/// should pay more than the member owes.</para>
///
/// <para><b>Standard COB (NAIC §7; <see cref="CobModel.Complementary"/>).</b>
/// <c>paid = min(normal, balance)</c>. Never more than allowed − priorPaid,
/// so all plans together never exceed the allowable expense. The member owes
/// what is left of the last prior payer's balance after our payment:
/// <c>member = min(costShareBeforeCob, balance − paid)</c>.</para>
///
/// <para><b>Non-duplication (<see cref="CobModel.NonDuplication"/>).</b>
/// <c>paid = min(max(0, normal − priorPaid), balance)</c>: this plan pays
/// only the part of its normal benefit the earlier payers did not already
/// pay. The member keeps this plan's cost share, never more than the balance
/// left after our payment: <c>member = min(costShareBeforeCob, balance −
/// paid)</c>.</para>
///
/// <para>With one prior payer and its patient responsibility unknown both
/// models reduce to the secondary-only rules of #1266 (except that
/// standard COB now caps at allowed − prior paid instead of billed − prior
/// paid, per the allowable-expense limit above).</para>
///
/// <para><b>835 (caller).</b> <see cref="CobLineResult.CobReduction"/> is
/// this plan's COB savings (normal − paid). The caller reduces its PR
/// entries to <see cref="CobLineResult.MemberResponsibility"/> and reports a
/// single positive OA-23 ("impact of prior payer(s) adjudication including
/// payments and/or adjustments") for allowed − member − paid: the total
/// prior-payer reduction across all prior payers, so charge − ΣCAS = paid
/// on every line and claim. Both parts are non-negative (paid ≤ normal and
/// member ≤ cost share), so OA-23 is never negative.</para>
/// </summary>
public class CobCalculationService : ICobCalculationService
{
    public CobLineResult Calculate(CobLineInput input)
    {
        var priorPayers = input.EffectivePriorPayers;
        var priorPaid = priorPayers.Sum(p => Math.Max(0, p.PaidAmount));
        var lastPriorPr = priorPayers[^1].PatientResponsibility;

        var balance = Math.Max(0, input.SecondaryAllowedAmount - priorPaid);
        if (lastPriorPr is { } pr)
            balance = Math.Min(balance, Math.Max(0, pr));

        var normal = Math.Max(0, input.SecondaryPlanPaymentBeforeCob);
        var costShare = Math.Max(0, input.SecondaryMemberResponsibilityBeforeCob);

        var paid = input.Model switch
        {
            CobModel.NonDuplication => Math.Min(Math.Max(0, NonDuplicationBenefit(input) - priorPaid), balance),
            _ => Math.Min(normal, balance),
        };

        var member = Math.Min(costShare, Math.Max(0, balance - paid));
        var cobReduction = input.SecondaryPlanPaymentBeforeCob - paid;

        return new CobLineResult
        {
            LineNumber = input.LineNumber,
            PrimaryPayerPayment = priorPayers[0].PaidAmount,
            TotalPriorPaid = priorPaid,
            SecondaryPlanPayment = paid,
            MemberResponsibility = member,
            CobReduction = cobReduction,
            CobApplied = cobReduction != 0
                         || member != input.SecondaryMemberResponsibilityBeforeCob
        };
    }

    public IReadOnlyList<CobLineResult> CalculateAll(IEnumerable<CobLineInput> lines) =>
        lines.Select(Calculate).ToList();

    /// <summary>
    /// What this plan would have paid as the only plan, for non-duplication:
    /// allowed − its own cost share (equal to the pre-COB payment the
    /// benefit engine passes).
    /// </summary>
    private static decimal NonDuplicationBenefit(CobLineInput i) =>
        Math.Max(0, i.SecondaryAllowedAmount - i.SecondaryMemberResponsibilityBeforeCob);
}
