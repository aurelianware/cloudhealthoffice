using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccRunSummaryBuilderTests
{
    [Fact]
    public void Build_AggregatesWorkflowScenarioBreakdownWithoutDoubleCounting()
    {
        var options = ValidatorOptions.Parse([
            "--claims", "5",
            "--claim-results-limit", "10"
        ]);
        var results = new List<ClaimValidationResult>
        {
            Result("MCC-1", "EdgeCase:CobSecondaryPayer", MccWorkflowValidation.UnsupportedStatus, ClaimValidationOutcome.Paid),
            Result("MCC-2", "EdgeCase:CobSecondaryPayer", MccWorkflowValidation.UnsupportedStatus, ClaimValidationOutcome.BusinessDenial),
            Result("MCC-3", "EdgeCase:BehavioralHealthCarveIn", MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid),
            Result("MCC-4", "EdgeCase:BehavioralHealthCarveIn", MccWorkflowValidation.MismatchedStatus, ClaimValidationOutcome.BusinessDenial),
            Result("MCC-5", null, MccWorkflowValidation.UnspecifiedStatus, ClaimValidationOutcome.Paid)
        };

        var summary = MccRunSummaryBuilder.Build(
            results,
            TimeSpan.FromSeconds(10),
            options,
            DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-07T00:00:10Z"));

        Assert.Equal(5, summary.TotalClaims);
        Assert.Equal(4, summary.WorkflowScenarios);
        Assert.Equal(1, summary.WorkflowMatches);
        Assert.Equal(1, summary.WorkflowMismatches);
        Assert.Equal(2, summary.WorkflowUnsupported);
        Assert.Equal(2, summary.WorkflowScenarioBreakdown.Count);

        var cob = Assert.Single(summary.WorkflowScenarioBreakdown, s => s.Scenario == "EdgeCase:CobSecondaryPayer");
        Assert.Equal(2, cob.Total);
        Assert.Equal(0, cob.Matches);
        Assert.Equal(0, cob.Mismatches);
        Assert.Equal(2, cob.Unsupported);
        Assert.Equal(0, cob.Unspecified);

        var behavioral = Assert.Single(summary.WorkflowScenarioBreakdown, s => s.Scenario == "EdgeCase:BehavioralHealthCarveIn");
        Assert.Equal(2, behavioral.Total);
        Assert.Equal(1, behavioral.Matches);
        Assert.Equal(1, behavioral.Mismatches);
        Assert.Equal(0, behavioral.Unsupported);
        Assert.Equal(0, behavioral.Unspecified);
    }

    private static ClaimValidationResult Result(
        string claimId,
        string? scenario,
        string validationStatus,
        ClaimValidationOutcome outcome)
    {
        return new ClaimValidationResult(
            claimId,
            $"submitted-{claimId}",
            "EdgeCase",
            scenario,
            scenario is null ? null : outcome.ToString(),
            null,
            validationStatus,
            outcome,
            outcome is ClaimValidationOutcome.Paid,
            outcome is ClaimValidationOutcome.Paid ? 100m : null,
            outcome is ClaimValidationOutcome.Paid ? 100m : null,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(80),
            TimeSpan.FromMilliseconds(10),
            new Dictionary<string, double> { ["benefit"] = 25 },
            outcome is ClaimValidationOutcome.BusinessDenial ? "CARC_96" : null,
            null,
            null);
    }
}
