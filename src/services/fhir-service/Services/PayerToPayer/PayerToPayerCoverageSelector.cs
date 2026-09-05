using System.Globalization;
using FhirService.Models;
using FhirService.Models.PayerToPayer;

namespace FhirService.Services.PayerToPayer;

/// <summary>How coverage selection resolved for a matched member.</summary>
public enum CoverageSelectionOutcome
{
    /// <summary>Exactly one coverage context was resolved.</summary>
    Selected,

    /// <summary>The member has no coverage record.</summary>
    None,

    /// <summary>Several equally valid coverages remained — refuse rather than guess.</summary>
    Ambiguous,
}

/// <summary>The coverage-selection decision.</summary>
public readonly record struct CoverageSelection(CoverageSelectionOutcome Outcome, ChoCoverage? Coverage)
{
    public static CoverageSelection Select(ChoCoverage coverage) => new(CoverageSelectionOutcome.Selected, coverage);
    public static readonly CoverageSelection NoCoverage = new(CoverageSelectionOutcome.None, null);
    public static readonly CoverageSelection Ambiguous = new(CoverageSelectionOutcome.Ambiguous, null);
}

/// <summary>
/// Deterministic selection of the relevant coverage context for a matched member
/// (P2P-04 concurrent coverage). A member may hold several coverages — prior,
/// current, and overlapping. Selection is, in order:
///   1. the requested payer / subscriber context, when the request pins one;
///   2. the coverage in force as of the requested date (or now);
///   3. the single coverage, when only one exists.
/// If more than one coverage remains equally valid, the result is
/// <see cref="CoverageSelectionOutcome.Ambiguous"/> — the workflow refuses to
/// guess which relationship the receiving payer meant.
/// </summary>
public static class PayerToPayerCoverageSelector
{
    public static CoverageSelection Select(IReadOnlyList<ChoCoverage> coverages, MemberMatchCriteria criteria)
    {
        if (coverages.Count == 0) return CoverageSelection.NoCoverage;

        // 1. Requested payer / subscriber discriminator.
        IReadOnlyList<ChoCoverage> candidates = coverages;
        if (criteria.RequestedPayerId is not null || criteria.RequestedSubscriberId is not null)
        {
            var matched = coverages.Where(c =>
                (criteria.RequestedPayerId is null
                    || string.Equals(MemberIdentityNormalizer.Identifier(c.PayerId), criteria.RequestedPayerId, StringComparison.Ordinal))
                && (criteria.RequestedSubscriberId is null
                    || string.Equals(MemberIdentityNormalizer.Identifier(c.SubscriberId), criteria.RequestedSubscriberId, StringComparison.Ordinal)))
                .ToList();

            if (matched.Count == 1) return CoverageSelection.Select(matched[0]);
            // >1 → narrow further by date; 0 → the discriminator did not resolve, fall back to all.
            if (matched.Count > 1) candidates = matched;
        }

        // 2. Coverage in force as of the requested date (or now).
        var asOf = ParseDate(criteria.AsOfDate) ?? DateTime.UtcNow.Date;
        var inForce = candidates.Where(c => IsInForce(c, asOf)).ToList();
        if (inForce.Count == 1) return CoverageSelection.Select(inForce[0]);
        if (inForce.Count > 1) return CoverageSelection.Ambiguous;

        // 3. Nothing in force as of the date — fall back only if there is a single coverage.
        if (candidates.Count == 1) return CoverageSelection.Select(candidates[0]);
        return CoverageSelection.Ambiguous;
    }

    private static bool IsInForce(ChoCoverage coverage, DateTime asOf)
    {
        var start = ParseDate(coverage.PeriodStart);
        var end = ParseDate(coverage.PeriodEnd);
        if (start is { } s && asOf < s) return false;
        if (end is { } e && asOf > e) return false;
        return true;
    }

    private static DateTime? ParseDate(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date.Date
            : null;
}
