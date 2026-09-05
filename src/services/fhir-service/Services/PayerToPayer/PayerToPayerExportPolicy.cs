using System.Globalization;
using FhirService.Models;

namespace FhirService.Services.PayerToPayer;

/// <summary>
/// The locked CMS-0057-F Payer-to-Payer data rules applied to an export:
///  * a date-of-service lookback window (5 years by default), and
///  * exclusion of remittances, enrollee cost-sharing, and drugs.
///
/// The lookback is enforced here against the payment's date of service. The
/// remittance / cost-sharing / drug exclusions are represented as explicit
/// predicates so the policy is complete and extensible, but the current
/// CHO payment model (<see cref="ChoPaymentDocument"/>) carries no marker that
/// classifies a record as any of those, so nothing is excluded on that basis
/// yet. When those markers are added, the predicate is the single place to
/// enforce them — this is not a silent omission.
/// </summary>
public static class PayerToPayerExportPolicy
{
    /// <summary>True when a payment is within the exchange's lookback window and not an excluded category.</summary>
    public static bool IncludePayment(ChoPaymentDocument payment, DateTime exchangeDateUtc, int lookbackYears)
        => WithinLookback(payment, exchangeDateUtc, lookbackYears) && !IsExcludedCategory(payment);

    private static bool WithinLookback(ChoPaymentDocument payment, DateTime exchangeDateUtc, int lookbackYears)
    {
        // A payment with no parseable date of service is conservatively excluded
        // from the export rather than assumed to be in-window.
        if (!DateTime.TryParse(payment.PaymentDate, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var serviced))
            return false;

        var cutoff = exchangeDateUtc.AddYears(-Math.Abs(lookbackYears));
        return serviced.Date >= cutoff.Date;
    }

    /// <summary>
    /// Remittance / enrollee cost-sharing / drug exclusion. The payment model has
    /// no category marker today, so this returns false (nothing excluded on this
    /// basis) — the hook exists so the exclusion lands here, not in the mapper.
    /// </summary>
    private static bool IsExcludedCategory(ChoPaymentDocument payment) => false;
}
