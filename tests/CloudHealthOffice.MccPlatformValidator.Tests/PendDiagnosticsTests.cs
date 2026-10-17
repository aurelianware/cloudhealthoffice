using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class PendDiagnosticsTests
{
    [Fact]
    public void SelectCandidates_IncludesEveryExpectedPendClaim()
    {
        var results = new List<ClaimValidationResult>
        {
            Result("MCC-1", "EdgeCase:CobSecondaryPayer", ClaimValidationOutcome.Pended, expectedPend: true, businessDenialCode: null),
            Result("MCC-2", "EdgeCase:SubrogationWorkersComp", ClaimValidationOutcome.BusinessDenial, expectedPend: true, businessDenialCode: "W1"),
            Result("MCC-3", "CleanProfessionalPaid", ClaimValidationOutcome.Paid, expectedPend: false, businessDenialCode: null),
        };

        var candidates = PendDiagnostics.SelectCandidates(results, ncciSampleSize: 200);

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, r => r.GeneratedClaimId == "MCC-1");
        Assert.Contains(candidates, r => r.GeneratedClaimId == "MCC-2");
    }

    [Theory]
    [InlineData("NCCI")]
    [InlineData("MUE")]
    public void SelectCandidates_SamplesClaimsService_NcciPendCodes_RegardlessOfAnswerKey(string pendCode)
    {
        // NcciEditsStage persists PendDetails.PendCode "MUE" (MUE-only
        // failures) or "NCCI" (pair edits, or mixed) — as a pend, or still on
        // the claim when NcciMode=Deny denies it.
        var results = new List<ClaimValidationResult>
        {
            Result("MCC-1", "ExcludedProviderDenied", ClaimValidationOutcome.BusinessDenial, expectedPend: false, businessDenialCode: "PROVIDER_EXCLUDED"),
            Result("MCC-2", null, ClaimValidationOutcome.Pended, expectedPend: false, businessDenialCode: null, pendCode: pendCode),
            Result("MCC-3", null, ClaimValidationOutcome.BusinessDenial, expectedPend: false, businessDenialCode: "CARC_236", pendCode: pendCode),
            Result("MCC-4", null, ClaimValidationOutcome.Pended, expectedPend: false, businessDenialCode: null, pendCode: "COB"),
        };

        var candidates = PendDiagnostics.SelectCandidates(results, ncciSampleSize: 200);

        Assert.Equal(["MCC-2", "MCC-3"], candidates.Select(r => r.GeneratedClaimId));
        Assert.All(candidates, r => Assert.Equal(pendCode, r.PendCode));
    }

    [Fact]
    public void SelectCandidates_IgnoresRemovedDryRunDenialCode()
    {
        // NCCI_MUE_EDIT_FAILURE was only ever produced by benefit-plan's
        // removed /adjudicate dry run; the claims-service pipeline never
        // persists it, so it must not drive the sample.
        var results = new List<ClaimValidationResult>
        {
            Result("MCC-1", null, ClaimValidationOutcome.BusinessDenial, expectedPend: false, businessDenialCode: "NCCI_MUE_EDIT_FAILURE"),
        };

        Assert.Empty(PendDiagnostics.SelectCandidates(results, ncciSampleSize: 200));
    }

    [Fact]
    public void SelectCandidates_CapsNcciMueSampleAtConfiguredSize()
    {
        var results = Enumerable.Range(1, 10)
            .Select(i => Result($"MCC-{i:D2}", null, ClaimValidationOutcome.Pended, expectedPend: false, businessDenialCode: null, pendCode: i % 2 == 0 ? "NCCI" : "MUE"))
            .ToList();

        var candidates = PendDiagnostics.SelectCandidates(results, ncciSampleSize: 3);

        Assert.Equal(3, candidates.Count);
    }

    [Fact]
    public void BuildScenarioSummaries_GroupsDeniedRowsByDenialCode()
    {
        var rows = new List<PendDiagnosticRow>
        {
            Row("MCC-1", "EdgeCase:CobSecondaryPayer", expectedOutcome: "Pended", outcome: "Pended", observedDenialCode: null, persistedDenialCode: null),
            Row("MCC-2", "EdgeCase:CobSecondaryPayer", expectedOutcome: "Pended", outcome: "Paid", observedDenialCode: null, persistedDenialCode: null),
            Row("MCC-3", "EdgeCase:CobSecondaryPayer", expectedOutcome: "Pended", outcome: "BusinessDenial", observedDenialCode: "CARC_22", persistedDenialCode: "CARC_22"),
            Row("MCC-4", "EdgeCase:CobSecondaryPayer", expectedOutcome: "Pended", outcome: "BusinessDenial", observedDenialCode: "CARC_22", persistedDenialCode: "CARC_22"),
            Row("MCC-5", "EdgeCase:CobSecondaryPayer", expectedOutcome: "Pended", outcome: "ObservationTimeout", observedDenialCode: null, persistedDenialCode: null),
        };

        var summaries = PendDiagnostics.BuildScenarioSummaries(rows);

        var cob = Assert.Single(summaries);
        Assert.Equal("EdgeCase:CobSecondaryPayer", cob.Scenario);
        Assert.Equal(5, cob.Total);
        Assert.Equal(5, cob.ExpectedPendCount);
        Assert.Equal(1, cob.ObservedPaid);
        Assert.Equal(1, cob.ObservedPended);
        Assert.Equal(1, cob.ObservedTimeouts);
        var denialCount = Assert.Single(cob.DeniedBreakdown);
        Assert.Equal("CARC_22", denialCount.Code);
        Assert.Equal(2, denialCount.Count);
    }

    [Fact]
    public void BuildScenarioSummaries_LabelsUnscopedNcciSampleRowsSeparately()
    {
        var rows = new List<PendDiagnosticRow>
        {
            // NcciMode=Deny: denied with CARC 236, pend code still recorded.
            Row("MCC-1", "(unlabeled NCCI/MUE sample)", expectedOutcome: "Paid", outcome: "BusinessDenial", observedDenialCode: "CARC_236", persistedDenialCode: "236"),
        };

        var summaries = PendDiagnostics.BuildScenarioSummaries(rows);

        var sample = Assert.Single(summaries);
        Assert.Equal(0, sample.ExpectedPendCount);
        var denialCount = Assert.Single(sample.DeniedBreakdown);
        Assert.Equal("CARC_236", denialCount.Code);
    }

    private static ClaimValidationResult Result(
        string claimId,
        string? scenario,
        ClaimValidationOutcome outcome,
        bool expectedPend,
        string? businessDenialCode,
        string? pendCode = null)
    {
        return new ClaimValidationResult(
            claimId,
            $"submitted-{claimId}",
            "EdgeCase",
            scenario,
            // A claim the answer key does not expect to pend expects to pay.
            expectedPend ? ClaimValidationOutcome.Pended.ToString()
                : outcome is ClaimValidationOutcome.Pended ? ClaimValidationOutcome.Paid.ToString() : outcome.ToString(),
            null,
            MccWorkflowValidation.MatchedStatus,
            outcome,
            outcome is ClaimValidationOutcome.Paid,
            null,
            null,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(80),
            TimeSpan.FromMilliseconds(10),
            businessDenialCode,
            null,
            null,
            PendCode: pendCode);
    }

    private static PendDiagnosticRow Row(
        string claimId,
        string scenario,
        string expectedOutcome,
        string outcome,
        string? observedDenialCode,
        string? persistedDenialCode)
    {
        return new PendDiagnosticRow(
            claimId,
            $"submitted-{claimId}",
            "EdgeCase",
            scenario,
            expectedOutcome,
            null,
            MccWorkflowValidation.MatchedStatus,
            outcome,
            AdjudicationSuccess: outcome == "Paid",
            BusinessDenialCode: observedDenialCode,
            Error: null,
            PersistedClaimStatus: outcome,
            PendCode: null,
            PendReason: null,
            PersistedDenialReasonCode: persistedDenialCode,
            PersistedDenialReason: null,
            LineOutcomes: Array.Empty<PendDiagnosticLineOutcome>(),
            ClaimStateFetchError: null);
    }
}
