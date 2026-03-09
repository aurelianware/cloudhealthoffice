using CloudHealthOffice.FeeScheduleEngine.Models;

namespace CloudHealthOffice.FeeScheduleEngine.Services;

/// <summary>
/// Resolves the allowed rate for one or more claim lines.
///
/// Resolution order for a single claim line:
///   1. Look up provider contract (NPI → GroupTin fallback).
///   2. Identify the applicable fee schedule (contract line override → contract default → plan default).
///   3. Fetch the rate line (exact modifier match → base rate fallback → UCR).
///   4. Calculate the base allowed amount (flat rate, RVU, percent-of-billed, per-diem, DRG, capitation).
///   5. Apply payment modifier adjustments (26/TC, 50, 51, 52, 53, 62, 80, AS, 22).
///   6. Return PricingResult with full adjustment audit trail.
/// </summary>
public interface IRateResolutionService
{
    /// <summary>
    /// Resolve the allowed rate for a single claim line.
    /// </summary>
    Task<PricingResult> ResolveAsync(PricingRequest request, CancellationToken ct = default);

    /// <summary>
    /// Resolve allowed rates for all lines in a claim batch.
    /// Lines are processed in order; line rank drives multiple-procedure reduction.
    /// </summary>
    Task<PricingResultSet> ResolveBatchAsync(
        IReadOnlyList<PricingRequest> requests, CancellationToken ct = default);
}
