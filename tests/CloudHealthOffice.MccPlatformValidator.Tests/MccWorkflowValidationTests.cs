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

    [Theory]
    [InlineData("B7")]
    [InlineData("CARC_B7")]
    public void ExpectedValidationFor_ExcludedProviderClaim_NormalizesCarcB7(string actualBusinessDenialCode)
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
            MccWorkflowValidation.NormalizeBusinessDenialCode(actualBusinessDenialCode));

        Assert.Equal(MccWorkflowValidation.ProviderExcludedCode, expected.ExpectedBusinessDenialCode);
        Assert.Equal(MccWorkflowValidation.MatchedStatus, status);
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
        claim.EdgeCase = EdgeCaseScenario.RetroEligibilityAdd;
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

        Assert.Equal("EdgeCase:RetroEligibilityAdd", expected.Scenario);
        Assert.Equal(ClaimValidationOutcome.Paid, expected.ExpectedOutcome);
        Assert.Null(expected.ExpectedBusinessDenialCode);
        Assert.Equal("Matched", status);
    }

    [Theory]
    [InlineData(EdgeCaseScenario.BehavioralHealthCarveIn)]
    [InlineData(EdgeCaseScenario.BehavioralHealthParityCheck)]
    public void ExpectedValidationFor_BehavioralHealthPaidEdgeCases_ReturnsScoreablePaid(
        EdgeCaseScenario scenario)
    {
        var claim = CreateClaim(
            claimType: "EdgeCase",
            benefitPlanId: "MCC-PLAN",
            placeOfService: "11",
            priorAuthStatus: "NotRequired",
            priorAuthNumber: null,
            renderingState: "AZ");
        claim.EdgeCase = scenario;
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

        Assert.Equal($"EdgeCase:{scenario}", expected.Scenario);
        Assert.Equal(ClaimValidationOutcome.Paid, expected.ExpectedOutcome);
        Assert.Null(expected.ExpectedBusinessDenialCode);
        Assert.False(expected.IsUnsupported);
        Assert.Equal(MccWorkflowValidation.MatchedStatus, status);
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
        claim.EdgeCase = EdgeCaseScenario.RetroEligibilityTermination;
        claim.ExpectedOutcome = new ExpectedOutcome
        {
            Disposition = "Denied",
            DenialReasonCode = "27"
        };

        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
        var status = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.BusinessDenial,
            actualBusinessDenialCode: "CARC_27");

        Assert.Equal("EdgeCase:RetroEligibilityTermination", expected.Scenario);
        Assert.Equal(ClaimValidationOutcome.BusinessDenial, expected.ExpectedOutcome);
        Assert.Equal("CARC_27", expected.ExpectedBusinessDenialCode);
        Assert.Equal("Matched", status);
    }

    [Fact]
    public void ExpectedValidationFor_PriorAuthNoAuthEdgeCase_UsesPlatformBusinessCode()
    {
        var claim = CreateClaim(
            claimType: "EdgeCase",
            benefitPlanId: "MCC-PLAN",
            placeOfService: "21",
            priorAuthStatus: "Required",
            priorAuthNumber: null,
            renderingState: "TX");
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
            actualBusinessDenialCode: MccWorkflowValidation.PriorAuthRequiredCode);

        Assert.Equal("EdgeCase:PriorAuthRequired_NoAuth", expected.Scenario);
        Assert.Equal(ClaimValidationOutcome.BusinessDenial, expected.ExpectedOutcome);
        Assert.Equal(MccWorkflowValidation.PriorAuthRequiredCode, expected.ExpectedBusinessDenialCode);
        Assert.False(expected.IsUnsupported);
        Assert.Equal(MccWorkflowValidation.MatchedStatus, status);
    }

    [Theory]
    [InlineData(EdgeCaseScenario.PriorAuthRequired_ExpiredAuth)]
    [InlineData(EdgeCaseScenario.PriorAuthRequired_WrongProvider)]
    [InlineData(EdgeCaseScenario.PriorAuthRequired_WrongProcedure)]
    public void ExpectedValidationFor_PriorAuthValidationEdgeCaseWithoutCapabilities_ReturnsUnsupported(
        EdgeCaseScenario scenario)
    {
        var claim = CreateClaim(
            claimType: "EdgeCase",
            benefitPlanId: "MCC-PLAN",
            placeOfService: "21",
            priorAuthStatus: "OnFile",
            priorAuthNumber: "AUTH-TEST",
            renderingState: "TX");
        claim.EdgeCase = scenario;
        claim.ExpectedOutcome = new ExpectedOutcome
        {
            Disposition = "Denied",
            DenialReasonCode = "197"
        };

        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
        var status = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.Paid,
            actualBusinessDenialCode: null);

        Assert.Equal($"EdgeCase:{scenario}", expected.Scenario);
        Assert.Null(expected.ExpectedOutcome);
        Assert.Equal(MccWorkflowValidation.PriorAuthRequiredCode, expected.ExpectedBusinessDenialCode);
        Assert.True(expected.IsUnsupported);
        Assert.Equal(MccWorkflowValidation.UnsupportedStatus, status);
    }

    [Theory]
    [InlineData(EdgeCaseScenario.PriorAuthRequired_ExpiredAuth)]
    [InlineData(EdgeCaseScenario.PriorAuthRequired_WrongProcedure)]
    public void ExpectedValidationFor_PriorAuthValidationEvidenceCapability_ReturnsScoreableDenial(
        EdgeCaseScenario scenario)
    {
        var claim = CreateClaim(
            claimType: "Institutional",
            benefitPlanId: "MCC-PLAN",
            placeOfService: "21",
            priorAuthStatus: scenario is EdgeCaseScenario.PriorAuthRequired_ExpiredAuth ? "Expired" : "OnFile",
            priorAuthNumber: "AUTH-TEST",
            renderingState: "TX");
        claim.EdgeCase = scenario;
        claim.ExpectedOutcome = new ExpectedOutcome
        {
            Disposition = "Denied",
            DenialReasonCode = "197"
        };

        var expected = MccWorkflowValidation.ExpectedValidationFor(
            claim,
            new MccWorkflowValidationCapabilities(ScorePriorAuthValidationEvidence: true));
        var status = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.BusinessDenial,
            actualBusinessDenialCode: MccWorkflowValidation.PriorAuthRequiredCode);

        Assert.Equal($"EdgeCase:{scenario}", expected.Scenario);
        Assert.Equal(ClaimValidationOutcome.BusinessDenial, expected.ExpectedOutcome);
        Assert.Equal(MccWorkflowValidation.PriorAuthRequiredCode, expected.ExpectedBusinessDenialCode);
        Assert.False(expected.IsUnsupported);
        Assert.Equal(MccWorkflowValidation.MatchedStatus, status);
    }

    [Fact]
    public void ExpectedValidationFor_WrongProviderWithoutProviderEvidence_ReturnsUnsupported()
    {
        var claim = CreateClaim(
            claimType: "Institutional",
            benefitPlanId: "MCC-PLAN",
            placeOfService: "21",
            priorAuthStatus: "OnFile",
            priorAuthNumber: "AUTH-TEST",
            renderingState: "TX");
        claim.EdgeCase = EdgeCaseScenario.PriorAuthRequired_WrongProvider;
        claim.ExpectedOutcome = new ExpectedOutcome
        {
            Disposition = "Denied",
            DenialReasonCode = "197"
        };

        var expected = MccWorkflowValidation.ExpectedValidationFor(
            claim,
            new MccWorkflowValidationCapabilities(ScorePriorAuthValidationEvidence: true));
        var status = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.Paid,
            actualBusinessDenialCode: null);

        Assert.Equal("EdgeCase:PriorAuthRequired_WrongProvider", expected.Scenario);
        Assert.Null(expected.ExpectedOutcome);
        Assert.Equal(MccWorkflowValidation.PriorAuthRequiredCode, expected.ExpectedBusinessDenialCode);
        Assert.True(expected.IsUnsupported);
        Assert.Equal(MccWorkflowValidation.UnsupportedStatus, status);
    }

    [Fact]
    public void ExpectedValidationFor_WrongProviderWithProviderEvidence_ReturnsScoreableDenial()
    {
        var claim = CreateClaim(
            claimType: "Institutional",
            benefitPlanId: "MCC-PLAN",
            placeOfService: "21",
            priorAuthStatus: "OnFile",
            priorAuthNumber: "AUTH-TEST",
            renderingState: "TX");
        claim.EdgeCase = EdgeCaseScenario.PriorAuthRequired_WrongProvider;
        claim.ExpectedOutcome = new ExpectedOutcome
        {
            Disposition = "Denied",
            DenialReasonCode = "197"
        };

        var expected = MccWorkflowValidation.ExpectedValidationFor(
            claim,
            new MccWorkflowValidationCapabilities(
                ScorePriorAuthValidationEvidence: true,
                ScorePriorAuthProviderValidationEvidence: true));
        var status = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.BusinessDenial,
            actualBusinessDenialCode: MccWorkflowValidation.PriorAuthRequiredCode);

        Assert.Equal("EdgeCase:PriorAuthRequired_WrongProvider", expected.Scenario);
        Assert.Equal(ClaimValidationOutcome.BusinessDenial, expected.ExpectedOutcome);
        Assert.Equal(MccWorkflowValidation.PriorAuthRequiredCode, expected.ExpectedBusinessDenialCode);
        Assert.False(expected.IsUnsupported);
        Assert.Equal(MccWorkflowValidation.MatchedStatus, status);
    }

    [Fact]
    public void ExpectedValidationFor_PendedEdgeCase_ReturnsPendedOutcome()
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
        var statusWhenPaid = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.Paid,
            actualBusinessDenialCode: null);
        var statusWhenPended = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.Pended,
            actualBusinessDenialCode: null);
        var statusWhenTimeout = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.ObservationTimeout,
            actualBusinessDenialCode: null);

        Assert.Equal("EdgeCase:CobSecondaryPayer", expected.Scenario);
        Assert.Equal(ClaimValidationOutcome.Pended, expected.ExpectedOutcome);
        Assert.Equal("CARC_22", expected.ExpectedBusinessDenialCode);
        Assert.False(expected.IsUnsupported);
        Assert.Equal(MccWorkflowValidation.MismatchedStatus, statusWhenPaid);
        Assert.Equal(MccWorkflowValidation.MatchedStatus, statusWhenPended);
        Assert.Equal(MccWorkflowValidation.ObservationTimeoutStatus, statusWhenTimeout);
    }

    [Fact]
    public void ExpectedValidationFor_SubrogationEdgeCase_ReturnsScoreablePendedOutcome()
    {
        // SubrogationWorkersComp was the last remaining unsupported-pended edge case
        // before it and its siblings were converted to real scored Pends; this
        // replaces the old unsupported-gate regression test now that the gate itself
        // has no members left.
        var claim = CreateClaim(
            claimType: "EdgeCase",
            benefitPlanId: "MCC-PLAN",
            placeOfService: "23",
            priorAuthStatus: "NotRequired",
            priorAuthNumber: null,
            renderingState: "AZ");
        claim.EdgeCase = EdgeCaseScenario.SubrogationWorkersComp;
        claim.ExpectedOutcome = new ExpectedOutcome
        {
            Disposition = "Pended",
            DenialReasonCode = "W1"
        };

        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
        var statusWhenPended = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.Pended,
            actualBusinessDenialCode: null);
        var statusWhenDenied = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.BusinessDenial,
            actualBusinessDenialCode: "CARC_96");

        Assert.Equal("EdgeCase:SubrogationWorkersComp", expected.Scenario);
        Assert.Equal(ClaimValidationOutcome.Pended, expected.ExpectedOutcome);
        Assert.Equal("W1", expected.ExpectedBusinessDenialCode);
        Assert.False(expected.IsUnsupported);
        Assert.Equal(MccWorkflowValidation.MatchedStatus, statusWhenPended);
        Assert.Equal(MccWorkflowValidation.MismatchedStatus, statusWhenDenied);
    }

    [Fact]
    public void ExpectedValidationFor_BehavioralHealthCarveOut_ReturnsScoreableCoverageDenial()
    {
        var claim = CreateClaim(
            claimType: "EdgeCase",
            benefitPlanId: "MCC-PLAN",
            placeOfService: "11",
            priorAuthStatus: "NotRequired",
            priorAuthNumber: null,
            renderingState: "AZ");
        claim.EdgeCase = EdgeCaseScenario.BehavioralHealthCarveOut;
        claim.ExpectedOutcome = new ExpectedOutcome
        {
            Disposition = "Denied",
            DenialReasonCode = "96"
        };

        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
        var status = MccWorkflowValidation.ValidationStatus(
            expected,
            ClaimValidationOutcome.BusinessDenial,
            actualBusinessDenialCode: MccWorkflowValidation.UncoveredServiceCode);

        Assert.Equal("EdgeCase:BehavioralHealthCarveOut", expected.Scenario);
        Assert.Equal(ClaimValidationOutcome.BusinessDenial, expected.ExpectedOutcome);
        Assert.Equal(MccWorkflowValidation.UncoveredServiceCode, expected.ExpectedBusinessDenialCode);
        Assert.False(expected.IsUnsupported);
        Assert.Equal(MccWorkflowValidation.MatchedStatus, status);
    }

    [Fact]
    public void AnswerKey_WhenClaimMissing_ReturnsUnspecified()
    {
        var key = MccAnswerKey.FromClaims([
            CreateClaim(
                claimType: "Professional",
                benefitPlanId: MccWorkflowValidation.CleanProfessionalPaidPlanId,
                placeOfService: "11",
                priorAuthStatus: "NotRequired",
                priorAuthNumber: null,
                renderingState: "AZ")
        ]);
        var missing = CreateClaim(
            claimType: "Professional",
            benefitPlanId: "OTHER-PLAN",
            placeOfService: "11",
            priorAuthStatus: "NotRequired",
            priorAuthNumber: null,
            renderingState: "AZ");
        missing.ClaimId = "MCC-MISSING-0000001";

        var expected = key.ExpectedValidationFor(missing);

        Assert.Null(expected.Scenario);
        Assert.Null(expected.ExpectedOutcome);
        Assert.False(expected.IsUnsupported);
    }

    [Fact]
    public void AnswerKey_WhenEdgeCaseEntryMissingExpectedOutcome_ReturnsUnspecified()
    {
        var claim = CreateClaim(
            claimType: "EdgeCase",
            benefitPlanId: "MCC-PLAN",
            placeOfService: "11",
            priorAuthStatus: "NotRequired",
            priorAuthNumber: null,
            renderingState: "AZ");
        claim.EdgeCase = EdgeCaseScenario.CobSecondaryPayer;
        claim.ExpectedOutcome = null!;

        var key = MccAnswerKey.FromClaims([claim]);
        var expected = key.ExpectedValidationFor(claim);

        Assert.Null(expected.Scenario);
        Assert.Null(expected.ExpectedOutcome);
        Assert.Equal(MccWorkflowValidation.UnspecifiedStatus,
            MccWorkflowValidation.ValidationStatus(expected, ClaimValidationOutcome.Paid, null));
    }

    [Fact]
    public void AnswerKey_WhenDuplicateClaimIds_Throws()
    {
        var first = CreateClaim(
            claimType: "Professional",
            benefitPlanId: MccWorkflowValidation.CleanProfessionalPaidPlanId,
            placeOfService: "11",
            priorAuthStatus: "NotRequired",
            priorAuthNumber: null,
            renderingState: "AZ");
        var second = CreateClaim(
            claimType: "Professional",
            benefitPlanId: MccWorkflowValidation.ExcludedProviderPlanId,
            placeOfService: "11",
            priorAuthStatus: "NotRequired",
            priorAuthNumber: null,
            renderingState: "AZ");
        second.ClaimId = first.ClaimId;

        var ex = Assert.Throws<InvalidOperationException>(() => MccAnswerKey.FromClaims([first, second]));

        Assert.Contains(first.ClaimId, ex.Message);
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
