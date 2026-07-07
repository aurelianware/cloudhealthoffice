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

    [Fact]
    public void SelectCandidates_SamplesNcciMueDenialsRegardlessOfAnswerKey()
    {
        var results = new List<ClaimValidationResult>
        {
            Result("MCC-1", "ExcludedProviderDenied", ClaimValidationOutcome.BusinessDenial, expectedPend: false, businessDenialCode: "PROVIDER_EXCLUDED"),
            Result("MCC-2", null, ClaimValidationOutcome.BusinessDenial, expectedPend: false, businessDenialCode: "NCCI_MUE_EDIT_FAILURE"),
            Result("MCC-3", null, ClaimValidationOutcome.BusinessDenial, expectedPend: false, businessDenialCode: "NCCI_MUE_EDIT_FAILURE"),
        };

        var candidates = PendDiagnostics.SelectCandidates(results, ncciSampleSize: 200);

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, r => Assert.Equal("NCCI_MUE_EDIT_FAILURE", r.BusinessDenialCode));
    }

    [Fact]
    public void SelectCandidates_CapsNcciMueSampleAtConfiguredSize()
    {
        var results = Enumerable.Range(1, 10)
            .Select(i => Result($"MCC-{i:D2}", null, ClaimValidationOutcome.BusinessDenial, expectedPend: false, businessDenialCode: "NCCI_MUE_EDIT_FAILURE"))
            .ToList();

        var candidates = PendDiagnostics.SelectCandidates(results, ncciSampleSize: 3);

        Assert.Equal(3, candidates.Count);
    }

    [Fact]
    public void BuildScenarioSummaries_GroupsDeniedRowsByDenialCode()
    {
        var rows = new List<PendDiagnosticRow>
        {
            Row("MCC-1", "EdgeCase:CobSecondaryPayer", expectedOutcome: "Pended", outcome: "Pended", syncDenialCode: null, persistedDenialCode: null),
            Row("MCC-2", "EdgeCase:CobSecondaryPayer", expectedOutcome: "Pended", outcome: "Paid", syncDenialCode: null, persistedDenialCode: null),
            Row("MCC-3", "EdgeCase:CobSecondaryPayer", expectedOutcome: "Pended", outcome: "BusinessDenial", syncDenialCode: "CARC_22", persistedDenialCode: "CARC_22"),
            Row("MCC-4", "EdgeCase:CobSecondaryPayer", expectedOutcome: "Pended", outcome: "BusinessDenial", syncDenialCode: "CARC_22", persistedDenialCode: "CARC_22"),
            Row("MCC-5", "EdgeCase:CobSecondaryPayer", expectedOutcome: "Pended", outcome: "ObservationTimeout", syncDenialCode: null, persistedDenialCode: null),
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
            Row("MCC-1", "(unlabeled NCCI/MUE sample)", expectedOutcome: "BusinessDenial", outcome: "BusinessDenial", syncDenialCode: "NCCI_MUE_EDIT_FAILURE", persistedDenialCode: "NCCI_MUE_EDIT_FAILURE"),
        };

        var summaries = PendDiagnostics.BuildScenarioSummaries(rows);

        var sample = Assert.Single(summaries);
        Assert.Equal(0, sample.ExpectedPendCount);
        var denialCount = Assert.Single(sample.DeniedBreakdown);
        Assert.Equal("NCCI_MUE_EDIT_FAILURE", denialCount.Code);
    }

    private static ClaimValidationResult Result(
        string claimId,
        string? scenario,
        ClaimValidationOutcome outcome,
        bool expectedPend,
        string? businessDenialCode)
    {
        return new ClaimValidationResult(
            claimId,
            $"submitted-{claimId}",
            "EdgeCase",
            scenario,
            expectedPend ? ClaimValidationOutcome.Pended.ToString() : outcome.ToString(),
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
            new Dictionary<string, double>(),
            businessDenialCode,
            null,
            null);
    }

    private static PendDiagnosticRow Row(
        string claimId,
        string scenario,
        string expectedOutcome,
        string outcome,
        string? syncDenialCode,
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
            SynchronousAdjudicationSuccess: outcome == "Paid",
            SynchronousBusinessDenialCode: syncDenialCode,
            Error: null,
            SynchronousAdjudicationResponse: null,
            PersistedClaimStatus: outcome,
            PendCode: null,
            PendReason: null,
            PersistedDenialReasonCode: persistedDenialCode,
            PersistedDenialReason: null,
            LineOutcomes: Array.Empty<PendDiagnosticLineOutcome>(),
            ClaimStateFetchError: null);
    }
}
