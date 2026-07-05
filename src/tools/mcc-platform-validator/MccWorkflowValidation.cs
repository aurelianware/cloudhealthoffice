using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

public enum MccValidationOutcome
{
    Paid,
    BusinessDenial,
    PlatformFailure
}

public sealed record ExpectedValidation(
    string? Scenario,
    string? ExpectedOutcome,
    string? ExpectedBusinessDenialCode)
{
    public static ExpectedValidation Unspecified { get; } = new(null, null, null);
}

public static class MccWorkflowValidation
{
    public const string CleanProfessionalPaidScenario = "CleanProfessionalPaid";
    public const string CleanProfessionalPaidPlanId = "MCC_VALIDATION_CLEAN_PAID";
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
                MccValidationOutcome.Paid.ToString(),
                null);
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
                MccValidationOutcome.BusinessDenial.ToString(),
                PriorAuthRequiredCode)
            : ExpectedValidation.Unspecified;
    }

    public static string ValidationStatus(
        ExpectedValidation expected,
        string actualOutcome,
        string? actualBusinessDenialCode)
    {
        if (expected.ExpectedOutcome is null)
        {
            return "Unspecified";
        }

        if (!string.Equals(expected.ExpectedOutcome, actualOutcome, StringComparison.Ordinal))
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
