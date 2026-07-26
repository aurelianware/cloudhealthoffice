namespace ClaimsService.Services.Resolution;

/// <summary>
/// Resolves the member's own (CHO) active benefit-plan id from coverage-service
/// for claims that arrive without one — the X12 837 on-ramp (<c>ImportRaw837</c>)
/// intentionally leaves <c>AdapterClaim.BenefitPlanId</c> blank rather than
/// guessing (see <c>X12837ClaimMapper</c>'s doc comment), so an unrecognized
/// member surfaces as a real pend instead of silently defaulting. This resolver
/// is what lets a correctly-enrolled member's claim still find its plan.
///
/// <para>
/// Distinct from <see cref="ICoverageClient"/>, which resolves other-insurance
/// (COB) entries for capability 5.8 — this resolves CHO's own coverage record,
/// not third-party payers.
/// </para>
/// </summary>
public interface ICoverageResolver
{
    /// <summary>
    /// Returns the PlanId of the member's active coverage as of
    /// <paramref name="serviceDate"/>, or <c>null</c> when no active coverage
    /// is found, the member is missing, or the lookup degrades (transport
    /// failure). Non-throwing, same posture as <see cref="IMemberResolver"/>
    /// and <see cref="IBenefitPlanResolver"/> — callers treat null identically
    /// regardless of cause and let <c>BenefitCalculationStage</c>'s existing
    /// "missing BenefitPlanId" reject carry the signal forward.
    /// </summary>
    Task<string?> ResolveBenefitPlanIdAsync(
        string tenantId,
        string memberId,
        DateTime serviceDate,
        string? insuranceLineCode = null,
        CancellationToken ct = default);
}
