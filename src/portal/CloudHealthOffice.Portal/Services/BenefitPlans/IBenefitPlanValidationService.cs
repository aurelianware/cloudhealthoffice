namespace CloudHealthOffice.Portal.Services;

public interface IBenefitPlanValidationService
{
    bool SyntheticClaimsEnabled { get; }
    Task<BenefitPlanValidationResult> ValidateAsync(
        BenefitPlanDetails plan,
        DateTime serviceDate,
        CancellationToken cancellationToken = default);
    Task<SyntheticClaimValidationResult> RunSynthetic837Async(
        BenefitPlanDetails plan,
        SyntheticClaimValidationRequest request,
        CancellationToken cancellationToken = default);
}
