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

        // The bound is the PR of the last payer that actually adjudicated the
        // unit (paid more than $0 or left the member a PR). A denial — $0 paid,
        // no PR, only CO/OA adjustments — is "did not cover", not "the member
        // owes $0" (see CalculateClaim).
        var bounding = priorPayers.LastOrDefault(Adjudicated);
        var balance = Math.Max(0, input.SecondaryAllowedAmount - priorPaid);
        if (bounding?.PatientResponsibility is { } pr)
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

    private static bool Adjudicated(PriorPayerAmount p) =>
        p.PaidAmount > 0 || p.PatientResponsibility > 0;

    /// <summary>
    /// Claim-level COB. NAIC MDL-120 §7 applies its limits "for that claim":
    /// the secondary applies the benefit it would have paid on the claim to
    /// the allowable expense the earlier plans left unpaid on the claim, and
    /// all plans together pay no more than the claim's total allowable
    /// expense. So the limits are summed over the claim and only then
    /// distributed to lines — capping each line separately, with claim-level
    /// prior-payer amounts prorated to lines, loses money whenever the
    /// proration and this plan's line amounts do not line up.
    /// <code>
    ///   per line u:  priorPaid_u = Σ prior payers' paid (2430 SVD02, or the
    ///                              2320 residual prorated by charge)
    ///                room_u      = max(0, allowed_u − priorPaid_u)
    ///                B_u         = the last payer that adjudicated line u
    ///                              (paid &gt; $0 or PR &gt; 0 on it)
    ///   per bounding payer B (lines G_B with B_u = B):
    ///                balance_B = min(Σ room_u, PR of B on G_B)
    ///                (2430 PR for the lines B reported, plus B's 2320
    ///                 claim-level PR — all of it when G_B holds every line
    ///                 B reported at claim level)
    ///   lines no payer adjudicated, or whose B reported no CAS:
    ///                balance = Σ room_u (allowed − prior paid only)
    ///   balance = Σ balances
    ///   standard:  paid = min(Σ normal_u, balance)
    ///   non-dup:   paid = min(max(0, Σ normal_u − Σ priorPaid_u), balance)
    ///   member     = min(Σ costShare_u, balance − paid)
    /// </code>
    /// A prior payer that paid $0 with no PR (only CO/OA adjustments, e.g.
    /// CO-27 coverage terminated, CO-22 other coverage primary, CO-109 not
    /// covered by this payer, CO-96 non-covered, CO-204 not covered under
    /// the plan) did not adjudicate the service: its "PR = 0" is not the
    /// member's remaining liability, so it does not bound later payers.
    ///
    /// <para><b>Distribution.</b> Each group's balance is split across its
    /// lines by room (truncated to the cent, remainder on the last line with
    /// room): line balance b_u. Our payment goes to lines in proportion to
    /// their normal benefit, at most min(normal_u, b_u), then to any line up
    /// to b_u. The member's share goes to lines up to min(costShare_u, b_u −
    /// paid_u), then (only if needed) up to min(costShare_u, allowed_u −
    /// paid_u). So paid_u + member_u ≤ allowed_u and member_u ≤ costShare_u
    /// on every line: OA-23_u = allowed_u − member_u − paid_u ≥ 0.</para>
    /// </summary>
    public CobClaimResult CalculateClaim(CobClaimInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var units = input.Units;
        var n = units.Count;
        if (n == 0) return new CobClaimResult();

        var allowed = units.Select(u => Math.Max(0, u.AllowedAmount)).ToArray();
        var normal = units.Select(u => u.NormalBenefit).ToArray();
        var costShare = units.Select(u => Math.Max(0, u.CostShareBeforeCob)).ToArray();
        var priorPaid = new decimal[n];
        var firstPaid = new decimal[n];
        var lineBalance = new decimal[n];

        if (input.SingleStay || n == 1)
        {
            // One unit: every prior payer's claim totals.
            var payers = input.PriorPayers
                .Where(p => p.Sequence > 0 && p.Sequence < input.OurSequence)
                .OrderBy(p => p.Sequence)
                .Select(StayTotals)
                .ToList();
            priorPaid[0] = payers.Sum(p => Math.Max(0, p.PaidAmount));
            firstPaid[0] = payers.FirstOrDefault()?.PaidAmount ?? 0;
            var room = Math.Max(0, allowed[0] - priorPaid[0]);
            var bounding = payers.LastOrDefault(Adjudicated);
            lineBalance[0] = bounding?.PatientResponsibility is { } pr ? Math.Min(room, Math.Max(0, pr)) : room;
            if (n > 1)
                throw new ArgumentException("A single stay has one unit.", nameof(input));
        }
        else
        {
            var lines = input.ClaimLineCharges.Count > 0
                ? input.ClaimLineCharges
                    .Concat(units
                        .Where(u => input.ClaimLineCharges.All(c => c.LineNumber != u.LineNumber))
                        .Select(u => new PriorPayerAllocator.ClaimLineCharge(u.LineNumber, u.BilledAmount)))
                    .ToList()
                : units.Select(u => new PriorPayerAllocator.ClaimLineCharge(u.LineNumber, u.BilledAmount)).ToList();
            var payers = PriorPayerAllocator.AllocateDetailed(lines, input.PriorPayers, input.OurSequence);
            var room = new decimal[n];
            var bounding = new PriorPayerAllocator.PayerAllocation?[n];
            for (var i = 0; i < n; i++)
            {
                var ln = units[i].LineNumber;
                priorPaid[i] = payers.Sum(p => Math.Max(0, p.Lines[ln].Paid));
                firstPaid[i] = payers.FirstOrDefault()?.Lines[ln].Paid ?? 0;
                room[i] = Math.Max(0, allowed[i] - priorPaid[i]);
                bounding[i] = payers.LastOrDefault(p => p.AdjudicatedLine(ln));
            }

            var charge = units.GroupBy(u => u.LineNumber)
                .ToDictionary(g => g.Key, g => g.Sum(u => Math.Max(0, u.BilledAmount)));
            foreach (var group in Enumerable.Range(0, n).GroupBy(i => bounding[i]?.Sequence))
            {
                var idx = group.ToList();
                var payer = bounding[idx[0]];
                var groupRoom = idx.Sum(i => room[i]);
                var groupBalance = groupRoom;
                if (payer is { PrKnown: true })
                {
                    var bound = idx
                        .Select(i => payer.Lines[units[i].LineNumber].ReportedPr)
                        .Where(pr => pr is not null)
                        .Sum(pr => pr!.Value);
                    if (payer.ClaimPrPool > 0)
                    {
                        var targets = payer.ClaimLevelLines;
                        var inGroup = targets.Where(t => idx.Any(i => units[i].LineNumber == t)).ToList();
                        if (inGroup.Count == targets.Count)
                        {
                            bound += payer.ClaimPrPool;
                        }
                        else if (inGroup.Count > 0)
                        {
                            var all = targets.Sum(t => charge.GetValueOrDefault(t));
                            var part = inGroup.Sum(t => charge.GetValueOrDefault(t));
                            bound += all > 0
                                ? Math.Truncate(payer.ClaimPrPool * part * 100m / all) / 100m
                                : Math.Truncate(payer.ClaimPrPool * inGroup.Count * 100m / targets.Count) / 100m;
                        }
                    }
                    groupBalance = Math.Min(groupRoom, Math.Max(0, bound));
                }

                var shares = PriorPayerAllocator.Prorate(groupBalance, idx.Select(i => room[i]).ToList());
                for (var k = 0; k < idx.Count; k++)
                    lineBalance[idx[k]] = Math.Min(shares[k], room[idx[k]]);
            }
        }

        var balance = lineBalance.Sum();
        var totalNormal = normal.Sum();
        var totalPriorPaid = priorPaid.Sum();
        var paidTotal = input.Model switch
        {
            CobModel.NonDuplication => Math.Min(Math.Max(0, totalNormal - totalPriorPaid), balance),
            _ => Math.Min(totalNormal, balance),
        };
        var memberTotal = Math.Min(costShare.Sum(), Math.Max(0, balance - paidTotal));

        // Our payment: by normal benefit up to min(normal, line balance),
        // then up to the line balance.
        var paid = new decimal[n];
        var left = Fill(paidTotal, normal, Enumerable.Range(0, n).Select(i => Math.Min(normal[i], lineBalance[i])).ToArray(), paid);
        left = Fill(left, lineBalance.Select((b, i) => b - paid[i]).ToArray(), lineBalance.Select((b, i) => b - paid[i]).ToArray(), paid);

        // The member's share: within the line balance, then within allowed.
        var member = new decimal[n];
        var memberLeft = Fill(memberTotal, costShare,
            Enumerable.Range(0, n).Select(i => Math.Min(costShare[i], lineBalance[i] - paid[i])).ToArray(), member);
        Fill(memberLeft, costShare,
            Enumerable.Range(0, n).Select(i => Math.Min(costShare[i], allowed[i] - paid[i])).ToArray(), member);

        var results = new List<CobLineResult>(n);
        for (var i = 0; i < n; i++)
        {
            var reduction = normal[i] - paid[i];
            results.Add(new CobLineResult
            {
                LineNumber = units[i].LineNumber,
                PrimaryPayerPayment = firstPaid[i],
                TotalPriorPaid = priorPaid[i],
                SecondaryPlanPayment = paid[i],
                MemberResponsibility = member[i],
                CobReduction = reduction,
                CobApplied = reduction != 0 || member[i] != costShare[i],
            });
        }

        return new CobClaimResult
        {
            Units = results,
            TotalPriorPaid = totalPriorPaid,
            Balance = balance,
            PlanPayment = paid.Sum(),
            MemberResponsibility = member.Sum(),
        };
    }

    /// <summary>A prior payer's claim totals, for a single-stay unit.</summary>
    private static PriorPayerAmount StayTotals(PriorPayerAdjudication p)
    {
        var lineCas = p.Lines.SelectMany(l => l.Adjustments).ToList();
        var known = p.ClaimAdjustments.Count > 0 || lineCas.Count > 0;
        var pr = p.ClaimAdjustments.Concat(lineCas)
            .Where(a => string.Equals(a.GroupCode?.Trim(), "PR", StringComparison.OrdinalIgnoreCase))
            .Sum(a => a.Amount);
        return new PriorPayerAmount
        {
            Sequence = p.Sequence,
            PaidAmount = p.ClaimPaidAmount ?? p.Lines.Sum(l => l.PaidAmount),
            PatientResponsibility = known ? pr : null,
        };
    }

    /// <summary>
    /// Adds up to <paramref name="amount"/> to <paramref name="into"/>: first
    /// in proportion to <paramref name="weights"/> (truncated to the cent),
    /// never past <paramref name="caps"/> (the room each entry has left in
    /// this pass), then the rest to the last entries with a positive weight,
    /// then to any entry with room. Returns what could not be placed.
    /// </summary>
    private static decimal Fill(decimal amount, decimal[] weights, decimal[] caps, decimal[] into)
    {
        if (amount <= 0) return 0;
        var n = into.Length;
        var room = caps.Select(c => Math.Max(0, c)).ToArray();
        var w = weights.Select((x, i) => room[i] > 0 ? Math.Max(0, x) : 0).ToArray();
        var total = w.Sum();
        var remaining = amount;
        if (total > 0)
        {
            for (var i = 0; i < n; i++)
            {
                var share = Math.Min(Math.Truncate(amount * w[i] * 100m / total) / 100m, room[i]);
                into[i] += share;
                room[i] -= share;
                remaining -= share;
            }
        }
        foreach (var positiveOnly in new[] { true, false })
        {
            for (var i = n - 1; i >= 0 && remaining > 0; i--)
            {
                if (positiveOnly && w[i] <= 0) continue;
                var take = Math.Min(remaining, room[i]);
                into[i] += take;
                room[i] -= take;
                remaining -= take;
            }
        }
        return remaining;
    }

    /// <summary>
    /// What this plan would have paid as the only plan, for non-duplication:
    /// allowed − its own cost share (equal to the pre-COB payment the
    /// benefit engine passes).
    /// </summary>
    private static decimal NonDuplicationBenefit(CobLineInput i) =>
        Math.Max(0, i.SecondaryAllowedAmount - i.SecondaryMemberResponsibilityBeforeCob);
}
