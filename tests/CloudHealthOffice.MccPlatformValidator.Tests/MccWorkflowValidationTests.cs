using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccWorkflowValidationTests
{
    [Fact]
    public void ExpectedValidationFor_CleanProfessionalClaim_ReturnsPaidScenario()
    {
        var claim = CreateClaim(
            claimType: "Professional",
            benefitPlanId: MccWorkflowValidation.CleanProfessionalPaidPlanId,
            placeOfService: "11",
            priorAuthStatus: "NotRequired",
            priorAuthNumber: null,
            renderingState: "AZ");

        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
        var status = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.Paid,
            actualBusinessDenialCode: null);

        Assert.Equal(MccWorkflowValidation.CleanProfessionalPaidScenario, expected.Scenario);
        Assert.Equal(ClaimValidationOutcome.Paid, expected.ExpectedOutcome);
        Assert.Null(expected.ExpectedBusinessDenialCode);
        Assert.Equal("Matched", status);
    }

    [Fact]
    public void ExpectedValidationFor_TexasInpatientWithoutPriorAuth_ReturnsBusinessDenialScenario()
    {
        var claim = CreateClaim(
            claimType: "Institutional",
            benefitPlanId: "MCC-PLAN",
            placeOfService: "21",
            priorAuthStatus: "Required",
            priorAuthNumber: null,
            renderingState: "TX");

        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
        var status = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.BusinessDenial,
            MccWorkflowValidation.PriorAuthRequiredCode);

        Assert.Equal(MccWorkflowValidation.TexasStarInpatientNoAuthScenario, expected.Scenario);
        Assert.Equal(ClaimValidationOutcome.BusinessDenial, expected.ExpectedOutcome);
        Assert.Equal(MccWorkflowValidation.PriorAuthRequiredCode, expected.ExpectedBusinessDenialCode);
        Assert.Equal("Matched", status);
    }

    [Fact]
    public void ExpectedValidationFor_ExcludedProviderClaim_ReturnsProviderExcludedScenario()
    {
        var claim = CreateClaim(
            claimType: "Professional",
            benefitPlanId: MccWorkflowValidation.ExcludedProviderPlanId,
            placeOfService: "11",
            priorAuthStatus: "NotRequired",
            priorAuthNumber: null,
            renderingState: "AZ");
        claim.RenderingProvider.CredentialingStatus = "Excluded";

        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
        var status = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.BusinessDenial,
            MccWorkflowValidation.ProviderExcludedCode);

        Assert.Equal(MccWorkflowValidation.ExcludedProviderScenario, expected.Scenario);
        Assert.Equal(ClaimValidationOutcome.BusinessDenial, expected.ExpectedOutcome);
        Assert.Equal(MccWorkflowValidation.ProviderExcludedCode, expected.ExpectedBusinessDenialCode);
        Assert.Equal("Matched", status);
    }

    [Fact]
    public void ExpectedValidationFor_UncoveredServiceClaim_ReturnsCoverageDenialScenario()
    {
        var claim = CreateClaim(
            claimType: "Professional",
            benefitPlanId: MccWorkflowValidation.UncoveredServicePlanId,
            placeOfService: "31",
            priorAuthStatus: "NotRequired",
            priorAuthNumber: null,
            renderingState: "AZ");

        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
        var status = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.BusinessDenial,
            MccWorkflowValidation.UncoveredServiceCode);

        Assert.Equal(MccWorkflowValidation.UncoveredServiceScenario, expected.Scenario);
        Assert.Equal(ClaimValidationOutcome.BusinessDenial, expected.ExpectedOutcome);
        Assert.Equal(MccWorkflowValidation.UncoveredServiceCode, expected.ExpectedBusinessDenialCode);
        Assert.Equal("Matched", status);
    }

    [Fact]
    public void ExpectedValidationFor_PaidEdgeCase_UsesGeneratedAnswerKey()
    {
        var claim = CreateClaim(
            claimType: "EdgeCase",
            benefitPlanId: "MCC-PLAN",
            placeOfService: "11",
            priorAuthStatus: "NotRequired",
            priorAuthNumber: null,
            renderingState: "AZ");
        claim.EdgeCase = EdgeCaseScenario.BehavioralHealthCarveIn;
        claim.ExpectedOutcome = new ExpectedOutcome
        {
            Disposition = "Paid",
            ExpectedPaidAmount = 100m
        };

        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
        var status = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.Paid,
            actualBusinessDenialCode: null);

        Assert.Equal("EdgeCase:BehavioralHealthCarveIn", expected.Scenario);
        Assert.Equal(ClaimValidationOutcome.Paid, expected.ExpectedOutcome);
        Assert.Null(expected.ExpectedBusinessDenialCode);
        Assert.Equal("Matched", status);
    }

    [Fact]
    public void ExpectedValidationFor_DeniedEdgeCase_NormalizesExpectedCarcCode()
    {
        var claim = CreateClaim(
            claimType: "EdgeCase",
            benefitPlanId: "MCC-PLAN",
            placeOfService: "11",
            priorAuthStatus: "Required",
            priorAuthNumber: null,
            renderingState: "AZ");
        claim.EdgeCase = EdgeCaseScenario.PriorAuthRequired_NoAuth;
        claim.ExpectedOutcome = new ExpectedOutcome
        {
            Disposition = "Denied",
            DenialReasonCode = "197"
        };

        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
        var status = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.BusinessDenial,
            actualBusinessDenialCode: "CARC_197");

        Assert.Equal("EdgeCase:PriorAuthRequired_NoAuth", expected.Scenario);
        Assert.Equal(ClaimValidationOutcome.BusinessDenial, expected.ExpectedOutcome);
        Assert.Equal("CARC_197", expected.ExpectedBusinessDenialCode);
        Assert.Equal("Matched", status);
    }

    [Fact]
    public void ExpectedValidationFor_PendedEdgeCase_RequiresPendedOutcome()
    {
        var claim = CreateClaim(
            claimType: "EdgeCase",
            benefitPlanId: "MCC-PLAN",
            placeOfService: "11",
            priorAuthStatus: "NotRequired",
            priorAuthNumber: null,
            renderingState: "AZ");
        claim.EdgeCase = EdgeCaseScenario.CobSecondaryPayer;
        claim.ExpectedOutcome = new ExpectedOutcome
        {
            Disposition = "Pended",
            DenialReasonCode = "22"
        };

        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
        var matched = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.Pended,
            actualBusinessDenialCode: "CARC_22");
        var mismatched = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.BusinessDenial,
            actualBusinessDenialCode: "CARC_22");

        Assert.Equal("EdgeCase:CobSecondaryPayer", expected.Scenario);
        Assert.Equal(ClaimValidationOutcome.Pended, expected.ExpectedOutcome);
        Assert.Equal("CARC_22", expected.ExpectedBusinessDenialCode);
        Assert.Equal("Matched", matched);
        Assert.Equal("Mismatched", mismatched);
    }

    [Fact]
    public void ValidationStatus_WhenExpectedProviderExclusionPays_ReturnsMismatched()
    {
        var claim = CreateClaim(
            claimType: "Professional",
            benefitPlanId: MccWorkflowValidation.ExcludedProviderPlanId,
            placeOfService: "11",
            priorAuthStatus: "NotRequired",
            priorAuthNumber: null,
            renderingState: "AZ");
        claim.RenderingProvider.CredentialingStatus = "Excluded";

        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
        var status = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.Paid,
            actualBusinessDenialCode: null);

        Assert.Equal("Mismatched", status);
    }

    [Fact]
    public void ValidationStatus_WhenExpectedUncoveredServicePays_ReturnsMismatched()
    {
        var claim = CreateClaim(
            claimType: "Professional",
            benefitPlanId: MccWorkflowValidation.UncoveredServicePlanId,
            placeOfService: "31",
            priorAuthStatus: "NotRequired",
            priorAuthNumber: null,
            renderingState: "AZ");

        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
        var status = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.Paid,
            actualBusinessDenialCode: null);

        Assert.Equal("Mismatched", status);
    }

    [Fact]
    public void ValidationStatus_WhenExpectedPaidClaimIsDenied_ReturnsMismatched()
    {
        var claim = CreateClaim(
            claimType: "Professional",
            benefitPlanId: MccWorkflowValidation.CleanProfessionalPaidPlanId,
            placeOfService: "11",
            priorAuthStatus: "NotRequired",
            priorAuthNumber: null,
            renderingState: "AZ");

        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
        var status = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.BusinessDenial,
            "CARC_96");

        Assert.Equal("Mismatched", status);
    }

    [Fact]
    public void ExpectedValidationFor_UnscoredClaim_ReturnsUnspecified()
    {
        var claim = CreateClaim(
            claimType: "Professional",
            benefitPlanId: "OTHER-PLAN",
            placeOfService: "11",
            priorAuthStatus: "NotRequired",
            priorAuthNumber: null,
            renderingState: "AZ");

        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
        var status = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.Paid,
            actualBusinessDenialCode: null);

        Assert.Null(expected.Scenario);
        Assert.Null(expected.ExpectedOutcome);
        Assert.Null(expected.ExpectedBusinessDenialCode);
        Assert.Equal("Unspecified", status);
    }

    private static SyntheticClaim CreateClaim(
        string claimType,
        string benefitPlanId,
        string placeOfService,
        string priorAuthStatus,
        string? priorAuthNumber,
        string renderingState)
    {
        return new SyntheticClaim
        {
            ClaimId = "MCC-TEST-0000001",
            ClaimType = claimType,
            BenefitPlanId = benefitPlanId,
            PlaceOfService = placeOfService,
            PriorAuthStatus = priorAuthStatus,
            PriorAuthNumber = priorAuthNumber,
            RenderingProvider = new SyntheticProvider { State = renderingState },
            BillingProvider = new SyntheticProvider { State = renderingState },
            Lines = new List<ClaimLine>
            {
                new()
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    PlaceOfService = placeOfService,
                    Units = 1,
                    ChargeAmount = 180m,
                    DiagnosisPointers = new List<int> { 1 }
                }
            },
            ExpectedOutcome = new ExpectedOutcome()
        };
    }
}
