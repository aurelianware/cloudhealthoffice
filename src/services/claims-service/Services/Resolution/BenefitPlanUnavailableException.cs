namespace ClaimsService.Services.Resolution;

/// <summary>
/// benefit-plan-service gave no definitive answer for a plan (transport error,
/// timeout, non-404 failure status, unreadable body). Distinct from a
/// definitive 404, for which <see cref="IBenefitPlanResolver.GetPlanAsync"/>
/// returns null. The adjudication pipeline records it as
/// <c>ClaimAdjudicationContext.PlanResolutionFailed</c>.
/// </summary>
public sealed class BenefitPlanUnavailableException : Exception
{
    public BenefitPlanUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
