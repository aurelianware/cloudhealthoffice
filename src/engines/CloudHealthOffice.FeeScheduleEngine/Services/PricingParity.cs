using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.FeeScheduleEngine.Services;

/// <summary>
/// What the dual read of ADR 016 phase 4 found for one claim line: whether the
/// canonical contract would have paid what the legacy contract did.
/// </summary>
public enum ParityOutcome
{
    /// <summary>Same allowed amount, from the same schedule, by the same route.</summary>
    Match = 0,

    /// <summary>Same allowed amount, reached differently — a different schedule, rate source or per-stay decision.</summary>
    SameAmountDifferentRoute = 1,

    /// <summary>Different allowed amount. The delta is the money at stake on this line.</summary>
    AmountDelta = 2,

    /// <summary>The canonical contract pends a line the legacy contract priced.</summary>
    CanonicalPends = 3,

    /// <summary>The legacy contract pends a line the canonical contract priced.</summary>
    LegacyPends = 4,

    /// <summary>No canonical contract exists for this provider and plan yet — the backfill has not reached it.</summary>
    NoCanonicalContract = 5,

    /// <summary>The shadow pass itself failed. Nothing is known about this line's parity.</summary>
    ShadowFailed = 6,
}

/// <summary>
/// One line's parity, as the phase 4 window records it. Carries no member
/// identity: a provider NPI, a plan, a procedure code and two amounts are what
/// a delta has to be chased down with.
/// </summary>
public sealed record LineParity
{
    public required string TenantId { get; init; }
    public required string ProviderNpi { get; init; }
    public required string PlanId { get; init; }
    public required DateTime ServiceDate { get; init; }
    public required int LineNumber { get; init; }
    public required string ProcedureCode { get; init; }

    public required ParityOutcome Outcome { get; init; }

    /// <summary>What the claim was paid: the legacy contract's allowed amount.</summary>
    public required decimal PaidAmount { get; init; }

    /// <summary>What the canonical contract would have allowed.</summary>
    public required decimal CanonicalAmount { get; init; }

    /// <summary>Canonical minus paid: positive means the canonical contract pays more.</summary>
    public decimal Delta => CanonicalAmount - PaidAmount;

    /// <summary>Why it is not a <see cref="ParityOutcome.Match"/>, in words. Null when it is.</summary>
    public string? Reason { get; init; }

    public bool IsMatch => Outcome == ParityOutcome.Match;
}

/// <summary>Where a line's parity goes. One implementation emits metrics and logs.</summary>
public interface IPricingParityRecorder
{
    void Record(LineParity parity);
}

/// <summary>
/// Emits each line's parity as a metric, and logs the ones that are not a
/// match so a delta can be chased to the contract that caused it.
///
/// <para>
/// The counter is what the phase 4 exit criterion is read off: the window closes
/// for a tenant when a full monthly claim cycle produced no outcome other than
/// <see cref="ParityOutcome.Match"/> that anyone can explain. A match is counted
/// too, because "no deltas" is only meaningful beside how many lines were
/// compared.
/// </para>
/// </summary>
public sealed class MetricPricingParityRecorder : IPricingParityRecorder
{
    private readonly ILogger<MetricPricingParityRecorder> _logger;

    public MetricPricingParityRecorder(ILogger<MetricPricingParityRecorder> logger) => _logger = logger;

    public void Record(LineParity parity)
    {
        ChoMetrics.ContractParityLines.Add(
            1,
            new KeyValuePair<string, object?>("cho.tenant", parity.TenantId),
            new KeyValuePair<string, object?>("cho.outcome", OutcomeTag(parity.Outcome)));

        if (parity.Outcome == ParityOutcome.AmountDelta)
        {
            ChoMetrics.ContractParityAmountDelta.Record(
                (double)Math.Abs(parity.Delta),
                new KeyValuePair<string, object?>("cho.tenant", parity.TenantId));
        }

        if (parity.IsMatch)
            return;

        _logger.LogWarning(
            "Contract parity {Outcome} on line {LineNumber} ({ProcedureCode}) for provider {ProviderNpi} " +
            "plan {PlanId} on {ServiceDate:yyyy-MM-dd}: paid {PaidAmount:0.00}, canonical would allow " +
            "{CanonicalAmount:0.00} (delta {Delta:0.00}). {Reason}",
            parity.Outcome, parity.LineNumber, parity.ProcedureCode, parity.ProviderNpi, parity.PlanId,
            parity.ServiceDate, parity.PaidAmount, parity.CanonicalAmount, parity.Delta, parity.Reason);
    }

    private static string OutcomeTag(ParityOutcome outcome) => outcome switch
    {
        ParityOutcome.Match => "match",
        ParityOutcome.SameAmountDifferentRoute => "same_amount_different_route",
        ParityOutcome.AmountDelta => "amount_delta",
        ParityOutcome.CanonicalPends => "canonical_pends",
        ParityOutcome.LegacyPends => "legacy_pends",
        ParityOutcome.NoCanonicalContract => "no_canonical_contract",
        ParityOutcome.ShadowFailed => "shadow_failed",
        _ => "unknown",
    };
}

/// <summary>
/// Compares one line priced two ways. Separate from the decorator that runs the
/// two passes so that what counts as a difference is one readable rule.
/// </summary>
public static class PricingParityComparer
{
    public static LineParity Compare(
        PricingRequest request,
        PricingResult paid,
        PricingResult canonical,
        bool canonicalContractExists)
    {
        var (outcome, reason) = Classify(paid, canonical, canonicalContractExists);

        return new LineParity
        {
            TenantId = request.TenantId,
            ProviderNpi = request.ProviderNpi,
            PlanId = request.PlanId,
            ServiceDate = request.ServiceDate,
            LineNumber = request.LineNumber,
            ProcedureCode = request.ProcedureCode,
            Outcome = outcome,
            PaidAmount = paid.AllowedAmount,
            CanonicalAmount = canonical.AllowedAmount,
            Reason = reason,
        };
    }

    private static (ParityOutcome Outcome, string? Reason) Classify(
        PricingResult paid, PricingResult canonical, bool canonicalContractExists)
    {
        var paidPends = paid.RateSource == RateSource.Unresolved;
        var canonicalPends = canonical.RateSource == RateSource.Unresolved;

        // A pend on one side and not the other is the finding, whatever the
        // amounts are: one of the two contracts refuses to price this line.
        if (canonicalPends && !paidPends)
            return (ParityOutcome.CanonicalPends, canonical.UnresolvedReason);
        if (paidPends && !canonicalPends)
            return (ParityOutcome.LegacyPends, paid.UnresolvedReason);

        if (!canonicalContractExists && paid.AllowedAmount != canonical.AllowedAmount)
        {
            return (ParityOutcome.NoCanonicalContract,
                "the backfill has written no canonical contract for this provider and plan, so the shadow " +
                "pass priced the line at the plan default");
        }

        if (paid.AllowedAmount != canonical.AllowedAmount)
        {
            return (ParityOutcome.AmountDelta,
                $"paid from {Describe(paid)}, canonical from {Describe(canonical)}");
        }

        var differences = new List<string>();
        if (paid.RateSource != canonical.RateSource)
            differences.Add($"rate source {paid.RateSource} vs {canonical.RateSource}");
        if (!string.Equals(paid.FeeScheduleId, canonical.FeeScheduleId, StringComparison.Ordinal))
            differences.Add($"schedule {paid.FeeScheduleId ?? "(none)"} vs {canonical.FeeScheduleId ?? "(none)"}");
        if (paid.FeeScheduleType != canonical.FeeScheduleType)
            differences.Add($"schedule type {paid.FeeScheduleType} vs {canonical.FeeScheduleType}");
        if (paid.IsPerStayRate != canonical.IsPerStayRate)
            differences.Add($"per-stay {paid.IsPerStayRate} vs {canonical.IsPerStayRate}");
        if (paid.NetworkStatus != canonical.NetworkStatus)
            differences.Add($"network status {paid.NetworkStatus} vs {canonical.NetworkStatus}");
        if (paid.LesserOfBilledApplied != canonical.LesserOfBilledApplied)
            differences.Add($"lesser-of-billed {paid.LesserOfBilledApplied} vs {canonical.LesserOfBilledApplied}");

        // The same amount reached another way is not a payment difference, but
        // it is a contract that does not say what the one paying says, and the
        // next claim may be the one where it matters.
        return differences.Count == 0
            ? (ParityOutcome.Match, null)
            : (ParityOutcome.SameAmountDifferentRoute, string.Join("; ", differences));
    }

    private static string Describe(PricingResult result)
        => $"{result.RateSource} {result.FeeScheduleId ?? "(no schedule)"} at {result.AllowedAmount:0.00}";
}
