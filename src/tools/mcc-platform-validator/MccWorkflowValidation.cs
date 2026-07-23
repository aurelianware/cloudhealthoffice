using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

public sealed record ExpectedValidation(
    string? Scenario,
    ClaimValidationOutcome? ExpectedOutcome,
    string? ExpectedBusinessDenialCode,
    bool IsUnsupported = false)
{
    public static ExpectedValidation Unspecified { get; } = new(null, null, null);

    public static ExpectedValidation Unsupported(string scenario, string? expectedBusinessDenialCode)
        => new(scenario, null, expectedBusinessDenialCode, IsUnsupported: true);
}

public sealed record MccWorkflowValidationCapabilities(
    bool ScorePriorAuthValidationEvidence = false,
    bool ScorePriorAuthProviderValidationEvidence = false)
{
    public static MccWorkflowValidationCapabilities Default { get; } = new();
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
    public const string UnsupportedStatus = "Unsupported";
    public const string UnspecifiedStatus = "Unspecified";
    public const string MismatchedStatus = "Mismatched";
    public const string MatchedStatus = "Matched";
    public const string ObservationTimeoutStatus = "ObservationTimeout";

    public static ExpectedValidation ExpectedValidationFor(
        SyntheticClaim claim,
        MccWorkflowValidationCapabilities? capabilities = null)
    {
        var effectiveCapabilities = capabilities ?? MccWorkflowValidationCapabilities.Default;

        if (claim.EdgeCase is not null && claim.ExpectedOutcome is not null)
        {
            var scenario = $"EdgeCase:{claim.EdgeCase}";
            var expectedOutcome = OutcomeFromDisposition(claim.ExpectedOutcome.Disposition);
            var expectedCode = expectedOutcome is ClaimValidationOutcome.BusinessDenial
                && IsPriorAuthEdgeCase(claim.EdgeCase.Value)
                    ? PriorAuthRequiredCode
                    : NormalizeExpectedCode(claim.ExpectedOutcome.DenialReasonCode);

            if (expectedOutcome is ClaimValidationOutcome.BusinessDenial
                && IsUnsupportedPriorAuthValidationEdgeCase(claim.EdgeCase.Value, effectiveCapabilities))
            {
                return ExpectedValidation.Unsupported(scenario, expectedCode);
            }

            return expectedOutcome is null
                && claim.ExpectedOutcome.Disposition.Equals("Pended", StringComparison.OrdinalIgnoreCase)
                    ? new ExpectedValidation(scenario, ClaimValidationOutcome.Pended, expectedCode)
                    : new ExpectedValidation(scenario, expectedOutcome, expectedCode);
        }

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
        if (actualOutcome is ClaimValidationOutcome.ObservationTimeout)
        {
            return ObservationTimeoutStatus;
        }

        if (expected.ExpectedOutcome is null)
        {
            return expected.IsUnsupported ? UnsupportedStatus : UnspecifiedStatus;
        }

        if (expected.ExpectedOutcome != actualOutcome)
        {
            return MismatchedStatus;
        }

        if (expected.ExpectedOutcome is not ClaimValidationOutcome.Pended
            && !string.IsNullOrWhiteSpace(expected.ExpectedBusinessDenialCode)
            && !string.Equals(expected.ExpectedBusinessDenialCode, actualBusinessDenialCode, StringComparison.OrdinalIgnoreCase))
        {
            return MismatchedStatus;
        }

        return MatchedStatus;
    }

    public static string? NormalizeBusinessDenialCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();
        if (trimmed.Equals("197", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("CARC_197", StringComparison.OrdinalIgnoreCase))
        {
            return PriorAuthRequiredCode;
        }

        if (trimmed.Equals("B7", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("CARC_B7", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderExcludedCode;
        }

        return trimmed.All(char.IsDigit) ? $"CARC_{trimmed}" : trimmed;
    }

    private static ClaimValidationOutcome? OutcomeFromDisposition(string? disposition)
    {
        return disposition?.Trim() switch
        {
            { } value when value.Equals("Paid", StringComparison.OrdinalIgnoreCase) => ClaimValidationOutcome.Paid,
            { } value when value.Equals("Denied", StringComparison.OrdinalIgnoreCase) => ClaimValidationOutcome.BusinessDenial,
            { } value when value.Equals("Pended", StringComparison.OrdinalIgnoreCase) => ClaimValidationOutcome.Pended,
            _ => null
        };
    }

    private static bool IsUnsupportedPriorAuthValidationEdgeCase(
        EdgeCaseScenario scenario,
        MccWorkflowValidationCapabilities capabilities)
    {
        if (scenario is EdgeCaseScenario.PriorAuthRequired_WrongProvider)
        {
            return !capabilities.ScorePriorAuthProviderValidationEvidence;
        }

        if (scenario is
            EdgeCaseScenario.PriorAuthRequired_ExpiredAuth or
            EdgeCaseScenario.PriorAuthRequired_WrongProcedure)
        {
            return !capabilities.ScorePriorAuthValidationEvidence;
        }

        return false;
    }

    private static bool IsPriorAuthEdgeCase(EdgeCaseScenario scenario)
    {
        return scenario is
            EdgeCaseScenario.PriorAuthRequired_AuthOnFile or
            EdgeCaseScenario.PriorAuthRequired_NoAuth or
            EdgeCaseScenario.PriorAuthRequired_ExpiredAuth or
            EdgeCaseScenario.PriorAuthRequired_WrongProvider or
            EdgeCaseScenario.PriorAuthRequired_WrongProcedure;
    }

    private static string? NormalizeExpectedCode(string? code)
        => NormalizeBusinessDenialCode(code);
}

public sealed class MccAnswerKey
{
    private readonly IReadOnlyDictionary<string, ExpectedValidation> _entries;

    private MccAnswerKey(IReadOnlyDictionary<string, ExpectedValidation> entries)
    {
        _entries = entries;
    }

    public static MccAnswerKey FromClaims(
        IEnumerable<SyntheticClaim> claims,
        MccWorkflowValidationCapabilities? capabilities = null)
    {
        var entries = new Dictionary<string, ExpectedValidation>(StringComparer.Ordinal);
        var effectiveCapabilities = capabilities ?? MccWorkflowValidationCapabilities.Default;

        foreach (var claim in claims)
        {
            if (string.IsNullOrWhiteSpace(claim.ClaimId))
            {
                continue;
            }

            if (entries.ContainsKey(claim.ClaimId))
            {
                throw new InvalidOperationException($"Duplicate MCC answer-key claim id: {claim.ClaimId}");
            }

            entries.Add(claim.ClaimId, MccWorkflowValidation.ExpectedValidationFor(claim, effectiveCapabilities));
        }

        return new MccAnswerKey(entries);
    }

    public ExpectedValidation ExpectedValidationFor(SyntheticClaim claim)
        => !string.IsNullOrWhiteSpace(claim.ClaimId) && _entries.TryGetValue(claim.ClaimId, out var expected)
            ? expected
            : ExpectedValidation.Unspecified;
}
