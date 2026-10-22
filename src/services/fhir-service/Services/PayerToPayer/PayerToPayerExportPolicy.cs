using System.Globalization;
using System.Text.Json.Nodes;
using FhirService.Models;

namespace FhirService.Services.PayerToPayer;

/// <summary>
/// The locked CMS-0057-F Payer-to-Payer data rules applied to an export:
///  * a lookback window (5 years by default), and
///  * exclusion of remittances, enrollee cost-sharing, and drugs.
///
/// The lookback is enforced against the payment's date
/// (<see cref="ChoPaymentDocument.PaymentDate"/>) — the date the current CHO
/// payment model carries. The CMS rule anchors the window on the date of
/// service; where a distinct service-date field is added to the model, that is
/// the value this policy should read. The remittance / cost-sharing / drug
/// exclusions are represented as explicit predicates so the policy is complete
/// and extensible, but the payment model carries no marker classifying a record
/// as any of those, so nothing is excluded on that basis yet. When those markers
/// are added, the predicate is the single place to enforce them — not a silent
/// omission.
/// </summary>
public static class PayerToPayerExportPolicy
{
    /// <summary>True when a payment is within the exchange's lookback window and not an excluded category.</summary>
    public static bool IncludePayment(ChoPaymentDocument payment, DateTime exchangeDateUtc, int lookbackYears)
        => WithinLookback(payment, exchangeDateUtc, lookbackYears) && !IsExcludedCategory(payment);

    /// <summary>
    /// Whether a record dated <paramref name="date"/> (ISO 8601) is inside the
    /// lookback window ending at <paramref name="exchangeDateUtc"/>. A missing
    /// or unreadable date is outside it: never assumed to be in the window.
    /// </summary>
    public static bool WithinLookback(string? date, DateTime exchangeDateUtc, int lookbackYears)
    {
        if (!DateTime.TryParse(date, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return false;
        return parsed.Date >= exchangeDateUtc.AddYears(-Math.Abs(lookbackYears)).Date;
    }

    private static bool WithinLookback(ChoPaymentDocument payment, DateTime exchangeDateUtc, int lookbackYears)
    {
        // A payment with no parseable date is conservatively excluded from the
        // export rather than assumed to be in-window.
        if (!DateTime.TryParse(payment.PaymentDate, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var paymentDate))
            return false;

        var cutoff = exchangeDateUtc.AddYears(-Math.Abs(lookbackYears));
        return paymentDate.Date >= cutoff.Date;
    }

    /// <summary>
    /// Remittance / enrollee cost-sharing / drug exclusion. The payment model has
    /// no category marker today, so this returns false (nothing excluded on this
    /// basis) — the hook exists so the exclusion lands here, not in the mapper.
    /// </summary>
    private static bool IsExcludedCategory(ChoPaymentDocument payment) => false;

    /// <summary>
    /// How far back a DECIDED prior authorization is still handed over. A year
    /// past the end of its approved period: long enough that a receiving payer
    /// sees the recent history behind a member's current care, short enough
    /// that it is not an archive dump. Matches the prior-auth retention anchor
    /// (life of the authorization plus one year), so nothing is offered here
    /// that CHO would already have purged.
    /// </summary>
    public const int PriorAuthorizationLookbackYears = 1;

    /// <summary>
    /// Whether a prior-authorization <c>ExplanationOfBenefit</c>
    /// (<c>use = preauthorization</c>) belongs in the export.
    ///
    /// <para>
    /// Denied authorizations are excluded from Payer-to-Payer exchange (unlike
    /// Patient and Provider Access). The remaining authorizations that matter
    /// to the member: the ACTIVE and PENDING ones, and the recently decided.
    /// So:
    /// </para>
    /// <list type="bullet">
    ///   <item>still in flight (<c>outcome</c> = <c>queued</c>) — always in,
    ///   whatever its age: the receiving payer inherits an open request.</item>
    ///   <item>decided — in while its approved period has not ended more than
    ///   <see cref="PriorAuthorizationLookbackYears"/> ago, or, with no approved
    ///   period, while the request itself is that recent.</item>
    /// </list>
    ///
    /// <para>
    /// An authorization whose dates cannot be read at all is EXCLUDED, the same
    /// conservative reading a payment with no parseable date gets: a record
    /// whose window cannot be established is not assumed to be inside it.
    /// </para>
    /// </summary>
    public static bool IncludePriorAuthorization(JsonObject priorAuthorization, DateTime exchangeDateUtc)
    {
        ArgumentNullException.ThrowIfNull(priorAuthorization);

        // CMS excludes denied PAs from Payer-to-Payer, but not Patient/Provider
        // Access. Apply this only at the export boundary, before date/open checks.
        // The owning service's PriorAuthorizationEobProjector emits the exact
        // disposition "Denied" for AuthorizationStatus.Denied. outcome=complete
        // cannot distinguish a denial from an approval. Do not infer denial from
        // line reasons: Modified/partial approvals remain transferable.
        if (string.Equals(priorAuthorization["disposition"]?.GetValue<string>()?.Trim(),
                "Denied", StringComparison.OrdinalIgnoreCase))
            return false;

        // Still in flight: in, regardless of date.
        if (string.Equals(priorAuthorization["outcome"]?.GetValue<string>(), "queued", StringComparison.Ordinal))
            return true;

        // The approved period's end is what decides a decided authorization;
        // its start, then the request date, stand in when there is no end.
        var benefit = priorAuthorization["benefitPeriod"] as JsonObject;
        var candidate = benefit?["end"]?.GetValue<string>()
                        ?? benefit?["start"]?.GetValue<string>()
                        ?? priorAuthorization["created"]?.GetValue<string>();

        return WithinLookback(candidate, exchangeDateUtc, PriorAuthorizationLookbackYears);
    }
}
