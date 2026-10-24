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
    /// Returns the member's active coverage as of <paramref name="serviceDate"/>
    /// — the benefit plan id and the sponsor hierarchy it sits under — or
    /// <c>null</c> when no active coverage is found, the member is missing, or
    /// the lookup degrades (transport failure). Non-throwing, same posture as
    /// <see cref="IMemberResolver"/> and <see cref="IBenefitPlanResolver"/> —
    /// callers treat null identically regardless of cause and let
    /// <c>BenefitCalculationStage</c>'s existing "missing BenefitPlanId"
    /// reject carry the signal forward.
    /// </summary>
    Task<CoverageResolution?> ResolveCoverageAsync(
        string tenantId,
        string memberId,
        DateTime serviceDate,
        string? insuranceLineCode = null,
        CancellationToken ct = default);
}

/// <summary>
/// What the member's active coverage says about where the claim sits in the
/// plan-configuration hierarchy (plan-configuration.md §12.16).
/// </summary>
/// <remarks>
/// This resolver used to return the plan id alone, while the same
/// coverage-service response already carried the group number and the sponsor
/// policy on it. Adjudication therefore resolved every plan setting with only
/// a line of business in scope, so a value set on a sponsor, a policy or a
/// benefit plan could never be seen — not because anyone had decided
/// adjudication should ignore it, but because the three ids never travelled
/// this far.
///
/// <para>
/// <see cref="GroupNumber"/> is the sponsor's group number, which coverage
/// marks <c>[Required]</c>, so a resolved coverage always names its sponsor.
/// That is what lets a scope naming a policy stay coherent —
/// <c>SettingScope.Incoherent()</c> demands the sponsor whenever a policy is
/// given.
/// </para>
/// </remarks>
public sealed record CoverageResolution(
    string PlanId,
    string? GroupNumber = null,
    string? SponsorPolicyNumber = null);
