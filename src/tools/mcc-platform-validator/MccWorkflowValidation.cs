using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

public sealed record ExpectedValidation(
    string? Scenario,
    ClaimValidationOutcome? ExpectedOutcome,
    string? ExpectedBusinessDenialCode)
{
    public static ExpectedValidation Unspecified { get; } = new(null, null, null);
}

public static class MccWorkflowValidation
{
    public const string CleanProfessionalPaidScenario = "CleanProfessionalPaid";
    public const string CleanProfessionalPaidPlanId = "MCC_VALIDATION_CLEAN_PAID";
    public const string ExcludedProviderScenario = "ExcludedProviderDenied";
    public const string ExcludedProviderPlanId = "MCC_VALIDATION_EXCLUDED_PROVIDER";
    public const string ProviderExcludedCode = "PROVIDER_EXCLUDED";
    public const string UncoveredServiceScenario = "UncoveredServiceDenied";
    public const string UncoveredServicePlanId = "MCC_VALIDATION_UNCOVERED_SERVICE";
    public const string UncoveredServiceCode = "CARC_96";
    public const string TexasStarInpatientNoAuthScenario = "TxStarInpatientNoAuth";
    public const string PriorAuthRequiredCode = "PRIOR_AUTH_REQUIRED";

    public static ExpectedValidation ExpectedValidationFor(SyntheticClaim claim)
    {
        if (claim.ClaimType.Equals("Professional", StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.BenefitPlanId, CleanProfessionalPaidPlanId, StringComparison.Ordinal)
            && string.Equals(claim.PlaceOfService, "11", StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.PriorAuthStatus, "NotRequired", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(claim.PriorAuthNumber))
        {
            return new ExpectedValidation(
                CleanProfessionalPaidScenario,
                ClaimValidationOutcome.Paid,
                null);
        }

        if (claim.ClaimType.Equals("Professional", StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.BenefitPlanId, ExcludedProviderPlanId, StringComparison.Ordinal)
            && string.Equals(claim.PlaceOfService, "11", StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.RenderingProvider.CredentialingStatus, "Excluded", StringComparison.OrdinalIgnoreCase))
        {
            return new ExpectedValidation(
                ExcludedProviderScenario,
                ClaimValidationOutcome.BusinessDenial,
                ProviderExcludedCode);
        }

        if (claim.ClaimType.Equals("Professional", StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.BenefitPlanId, UncoveredServicePlanId, StringComparison.Ordinal)
            && string.Equals(claim.PlaceOfService, "31", StringComparison.OrdinalIgnoreCase))
        {
            return new ExpectedValidation(
                UncoveredServiceScenario,
                ClaimValidationOutcome.BusinessDenial,
                UncoveredServiceCode);
        }

        var isTxStarInpatientNoAuth =
            claim.ClaimType.Equals("Institutional", StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.PlaceOfService, "21", StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.PriorAuthStatus, "Required", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(claim.PriorAuthNumber)
            && string.Equals(claim.RenderingProvider.State, "TX", StringComparison.OrdinalIgnoreCase);

        return isTxStarInpatientNoAuth
            ? new ExpectedValidation(
                TexasStarInpatientNoAuthScenario,
                ClaimValidationOutcome.BusinessDenial,
                PriorAuthRequiredCode)
            : ExpectedValidation.Unspecified;
    }

    public static string ValidationStatus(
        ExpectedValidation expected,
        ClaimValidationOutcome actualOutcome,
        string? actualBusinessDenialCode)
    {
        if (expected.ExpectedOutcome is null)
        {
            return "Unspecified";
        }

        if (expected.ExpectedOutcome != actualOutcome)
        {
            return "Mismatched";
        }

        if (!string.IsNullOrWhiteSpace(expected.ExpectedBusinessDenialCode)
            && !string.Equals(expected.ExpectedBusinessDenialCode, actualBusinessDenialCode, StringComparison.OrdinalIgnoreCase))
        {
            return "Mismatched";
        }

        return "Matched";
    }
}
