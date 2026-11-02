using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.Infrastructure.Money;

namespace CloudHealthOffice.FeeScheduleEngine.Services;

/// <summary>
/// Works out the state supplemental payments on one priced line from the
/// plan's <see cref="FeeScheduleType.Supplemental"/> schedules, such as
/// Medi-Cal Proposition 56 dental supplemental payments.
///
/// <para>
/// A schedule pays its line's rate × units when the line's procedure is on it,
/// the date of service falls in [EffectiveDate, TermDate) and, when the schedule
/// has a claims runout, the claim was received within that many months of the
/// date of service. Every number (the codes, the amounts, the dates, the
/// runout) is the schedule's data, imported from the state; nothing here
/// knows Prop 56.
/// </para>
///
/// <para>
/// A listed procedure on or after the schedule's TermDate pays nothing and
/// carries the schedule's <see cref="FeeSchedule.DiscontinuedNotice"/> for the
/// EOB (APL 26-008 requires it). A listed procedure received after the runout
/// pays nothing and says why. A line the base rate could not price still gets
/// its supplemental worked out: the claim pends on the base rate anyway, and
/// the examiner sees both.
/// </para>
/// </summary>
public static class SupplementalPaymentCalculator
{
    /// <summary>Applies the supplemental schedules to <paramref name="result"/>.</summary>
    public static PricingResult Apply(
        PricingResult result, PricingRequest request, IReadOnlyList<FeeSchedule> schedules)
    {
        if (schedules.Count == 0 || string.IsNullOrWhiteSpace(request.ProcedureCode))
            return result;

        var serviceDate = request.ServiceDate.Date;
        var payments = new List<SupplementalPayment>();
        var notices = new List<string>();
        var warnings = new List<string>();

        foreach (var schedule in schedules
                     .Where(s => s.Type == FeeScheduleType.Supplemental)
                     .OrderBy(s => s.EffectiveDate)
                     .ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            if (schedule.EffectiveDate.Date > serviceDate) continue;

            var line = FindLine(schedule, request);
            if (line is null) continue;

            if (schedule.TermDate is DateTime term && serviceDate >= term.Date)
            {
                notices.Add(string.IsNullOrWhiteSpace(schedule.DiscontinuedNotice)
                    ? $"{schedule.Name} supplemental payments are discontinued for dates of service on or after {term:MMMM d, yyyy}."
                    : schedule.DiscontinuedNotice.Trim());
                continue;
            }

            if (schedule.ClaimsRunoutMonths is int months and > 0)
            {
                var lastDay = serviceDate.AddMonths(months);
                if (request.ClaimReceivedDate is null)
                {
                    warnings.Add(
                        $"{schedule.Name}: claim received date unknown, so its {months}-month claims runout was not checked.");
                }
                else if (request.ClaimReceivedDate.Value.Date > lastDay)
                {
                    notices.Add(
                        $"{schedule.Name} supplemental payment not paid: the claim was received " +
                        $"{request.ClaimReceivedDate.Value:yyyy-MM-dd}, more than {months} months after the " +
                        $"date of service {serviceDate:yyyy-MM-dd} (last day {lastDay:yyyy-MM-dd}).");
                    continue;
                }
            }

            var units = request.Units > 0 ? request.Units : 1m;
            payments.Add(new SupplementalPayment
            {
                FeeScheduleId = schedule.Id,
                FeeScheduleName = schedule.Name,
                Rate = line.Rate,
                Units = units,
                Amount = MoneyRounding.ToCents(line.Rate * units),
            });
        }

        if (payments.Count == 0 && notices.Count == 0 && warnings.Count == 0)
            return result;

        return result with
        {
            SupplementalAmount = result.SupplementalAmount + payments.Sum(p => p.Amount),
            SupplementalPayments = [.. result.SupplementalPayments, .. payments],
            PaymentNotices = [.. result.PaymentNotices, .. notices.Distinct(StringComparer.Ordinal)],
            Warnings = [.. result.Warnings, .. warnings],
        };
    }

    /// <summary>
    /// The schedule line for the procedure: one naming a modifier the line
    /// carries first, then the code's line without a modifier.
    /// </summary>
    public static FeeScheduleLine? FindLine(FeeSchedule schedule, PricingRequest request)
    {
        var code = request.ProcedureCode.Trim();
        FeeScheduleLine? baseLine = null;
        foreach (var line in schedule.Lines)
        {
            if (!string.Equals(line.ProcedureCode?.Trim(), code, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(line.Modifier))
            {
                baseLine ??= line;
                continue;
            }
            if (request.Modifiers.Any(m => string.Equals(m?.Trim(), line.Modifier.Trim(), StringComparison.OrdinalIgnoreCase)))
                return line;
        }
        return baseLine;
    }

    /// <summary>The plan identities a supplemental schedule may name: the version id and the plan code.</summary>
    public static IReadOnlyCollection<string> PlanKeys(PricingRequest request)
    {
        var keys = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(request.PlanId)) keys.Add(request.PlanId.Trim());
        if (!string.IsNullOrWhiteSpace(request.PlanCode) && !keys.Contains(request.PlanCode.Trim(), StringComparer.Ordinal))
            keys.Add(request.PlanCode.Trim());
        return keys;
    }
}
