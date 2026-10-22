using CloudHealthOffice.FeeScheduleEngine.Configuration;
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
/// One line's parity, as the dual-read window records it. Carries no member
/// identity: a provider NPI, a plan, a procedure code and two amounts are what
/// a delta has to be chased down with.
///
/// <para>
/// The two amounts are named by the store they came from, not by which one paid,
/// so a record reads the same before and after the cutover. <see cref="Mode"/>
/// is which of them the claim was actually paid.
/// </para>
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

    /// <summary>Which contract the claim was paid on.</summary>
    public required ContractPricingMode Mode { get; init; }

    /// <summary>What the legacy contract allowed.</summary>
    public required decimal LegacyAmount { get; init; }

    /// <summary>What the canonical contract allowed.</summary>
    public required decimal CanonicalAmount { get; init; }

    /// <summary>The amount the claim was actually paid, out of those two.</summary>
    public decimal PaidAmount
        => Mode == ContractPricingMode.Canonical ? CanonicalAmount : LegacyAmount;

    /// <summary>
    /// Canonical minus legacy: positive means the canonical contract pays more.
    /// Its sign does not change at the cutover, so a dashboard built during the
    /// window still reads correctly after it.
    /// </summary>
    public decimal Delta => CanonicalAmount - LegacyAmount;

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
            new KeyValuePair<string, object?>("cho.mode", parity.Mode == ContractPricingMode.Canonical
                ? "canonical"
                : "shadow"),
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
            "plan {PlanId} on {ServiceDate:yyyy-MM-dd}: legacy {LegacyAmount:0.00}, canonical " +
            "{CanonicalAmount:0.00} (delta {Delta:0.00}); paid on {Mode}. {Reason}",
            parity.Outcome, parity.LineNumber, parity.ProcedureCode, parity.ProviderNpi, parity.PlanId,
            parity.ServiceDate, parity.LegacyAmount, parity.CanonicalAmount, parity.Delta, parity.Mode,
            parity.Reason);
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
        PricingResult shadow,
        ContractPricingMode mode,
        bool canonicalContractExists)
    {
        // The paid pass is the canonical one only after the cutover; before it,
        // the shadow pass is. Sorting that out here is what lets every outcome
        // below be named by the store it is about.
        var (legacy, canonical) = mode == ContractPricingMode.Canonical
            ? (shadow, paid)
            : (paid, shadow);

        var (outcome, reason) = Classify(legacy, canonical, mode, canonicalContractExists);

        return new LineParity
        {
            TenantId = request.TenantId,
            ProviderNpi = request.ProviderNpi,
            PlanId = request.PlanId,
            ServiceDate = request.ServiceDate,
            LineNumber = request.LineNumber,
            ProcedureCode = request.ProcedureCode,
            Outcome = outcome,
            Mode = mode,
            LegacyAmount = legacy.AllowedAmount,
            CanonicalAmount = canonical.AllowedAmount,
            Reason = reason,
        };
    }

    private static (ParityOutcome Outcome, string? Reason) Classify(
        PricingResult legacy,
        PricingResult canonical,
        ContractPricingMode mode,
        bool canonicalContractExists)
    {
        // A cut-over tenant whose provider has no canonical contract is paid on
        // legacy by the fallback, so both passes price identically and the line
        // would otherwise read as a match. It is the one gap the cutover cannot
        // afford to hide: the tenant is meant to be paying on canonical.
        if (mode == ContractPricingMode.Canonical && !canonicalContractExists)
        {
            return (ParityOutcome.NoCanonicalContract,
                "this tenant is cut over, and the backfill has written no canonical contract for this provider " +
                "and plan: the line was paid on the legacy contract by the fallback");
        }

        var legacyPends = legacy.RateSource == RateSource.Unresolved;
        var canonicalPends = canonical.RateSource == RateSource.Unresolved;

        // A pend on one side and not the other is the finding, whatever the
        // amounts are: one of the two contracts refuses to price this line.
        if (canonicalPends && !legacyPends)
            return (ParityOutcome.CanonicalPends, canonical.UnresolvedReason);
        if (legacyPends && !canonicalPends)
            return (ParityOutcome.LegacyPends, legacy.UnresolvedReason);

        if (!canonicalContractExists && legacy.AllowedAmount != canonical.AllowedAmount)
        {
            return (ParityOutcome.NoCanonicalContract,
                "the backfill has written no canonical contract for this provider and plan, so the canonical " +
                "side priced the line at the plan default");
        }

        if (legacy.AllowedAmount != canonical.AllowedAmount)
        {
            return (ParityOutcome.AmountDelta,
                $"legacy from {Describe(legacy)}, canonical from {Describe(canonical)}");
        }

        var differences = new List<string>();
        if (legacy.RateSource != canonical.RateSource)
            differences.Add($"rate source {legacy.RateSource} vs {canonical.RateSource}");
        if (!string.Equals(legacy.FeeScheduleId, canonical.FeeScheduleId, StringComparison.Ordinal))
            differences.Add($"schedule {legacy.FeeScheduleId ?? "(none)"} vs {canonical.FeeScheduleId ?? "(none)"}");
        if (legacy.FeeScheduleType != canonical.FeeScheduleType)
            differences.Add($"schedule type {legacy.FeeScheduleType} vs {canonical.FeeScheduleType}");
        if (legacy.IsPerStayRate != canonical.IsPerStayRate)
            differences.Add($"per-stay {legacy.IsPerStayRate} vs {canonical.IsPerStayRate}");
        if (legacy.NetworkStatus != canonical.NetworkStatus)
            differences.Add($"network status {legacy.NetworkStatus} vs {canonical.NetworkStatus}");
        if (legacy.LesserOfBilledApplied != canonical.LesserOfBilledApplied)
            differences.Add($"lesser-of-billed {legacy.LesserOfBilledApplied} vs {canonical.LesserOfBilledApplied}");

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
