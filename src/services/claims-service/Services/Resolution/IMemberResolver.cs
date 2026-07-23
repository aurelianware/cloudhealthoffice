namespace ClaimsService.Services.Resolution;

/// <summary>
/// Resolves a member summary + eligibility for the adjudication pipeline
/// (capability 5.5). Uses a typed HTTP client against member-service
/// (<c>GET /api/v1/members/{memberId}</c> for demographics, plus the
/// <c>/eligibility</c> sub-resource for service-date eligibility).
/// Wrapped by <see cref="CachingMemberResolver"/> in production with the
/// same 5-minute per-tenant TTL as <see cref="IBenefitPlanResolver"/>.
/// </summary>
public interface IMemberResolver
{
    /// <summary>
    /// Returns the member summary, or <c>null</c> when the member is
    /// missing or the call fails. Failure is non-throwing — adjudication
    /// degrades cleanly.
    /// </summary>
    Task<ResolvedMember?> GetMemberAsync(string tenantId, string memberId, CancellationToken ct = default);
}

/// <summary>
/// Pipeline-local view of a member. Carries only what the adjudication
/// pipeline needs; full member documents live in member-service.
/// Decoupled from <c>MemberService.Models.Member</c> so claims-service
/// does not take a project reference on member-service.
/// </summary>
public class ResolvedMember
{
    public required string MemberId { get; init; }
    public string? SubscriberMemberId { get; init; }
    public bool IsSubscriber { get; init; }

    public DateTime? DateOfBirth { get; init; }
    public string? Gender { get; init; }

    /// <summary>
    /// Active enrollment status from member-service (<c>"Active"</c>,
    /// <c>"Terminated"</c>, ...). Null when the field was missing on the
    /// resolved document.
    /// </summary>
    public string? EnrollmentStatus { get; init; }

    public DateTime? EffectiveDate { get; init; }
    public DateTime? TerminationDate { get; init; }

    /// <summary>
    /// Retroactive effective date of a benefit-plan/coverage change recorded
    /// after the fact. Null unless member-service has a pending retroactive
    /// correction on file for this member.
    /// </summary>
    public DateTime? PlanChangeEffectiveDate { get; init; }

    /// <summary>
    /// Medicaid spend-down liability for the member's current budget period.
    /// Null for members not enrolled under a spend-down eligibility category.
    /// </summary>
    public decimal? MedicaidSpendDownLiabilityAmount { get; init; }

    /// <summary>Amount incurred so far toward <see cref="MedicaidSpendDownLiabilityAmount"/>.</summary>
    public decimal MedicaidSpendDownAmountMet { get; init; }
}
