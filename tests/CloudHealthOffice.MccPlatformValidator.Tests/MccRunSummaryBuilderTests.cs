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
            Result("MCC-1", "EdgeCase:CobSecondaryPayer", MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Pended),
            Result("MCC-2", "EdgeCase:CobSecondaryPayer", MccWorkflowValidation.ObservationTimeoutStatus, ClaimValidationOutcome.ObservationTimeout),
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
        Assert.Equal(2, summary.WorkflowMatches);
        Assert.Equal(1, summary.WorkflowMismatches);
        Assert.Equal(0, summary.WorkflowUnsupported);
        Assert.Equal(1, summary.WorkflowObservationTimeouts);
        Assert.Equal(1, summary.Pended);
        Assert.Equal(1, summary.ObservationTimeouts);
        Assert.Equal(2, summary.WorkflowScenarioBreakdown.Count);

        var cob = Assert.Single(summary.WorkflowScenarioBreakdown, s => s.Scenario == "EdgeCase:CobSecondaryPayer");
        Assert.Equal(2, cob.Total);
        Assert.Equal(1, cob.Matches);
        Assert.Equal(0, cob.Mismatches);
        Assert.Equal(0, cob.Unsupported);
        Assert.Equal(1, cob.ObservationTimeouts);
        Assert.Equal(0, cob.Unspecified);

        var behavioral = Assert.Single(summary.WorkflowScenarioBreakdown, s => s.Scenario == "EdgeCase:BehavioralHealthCarveIn");
        Assert.Equal(2, behavioral.Total);
        Assert.Equal(1, behavioral.Matches);
        Assert.Equal(1, behavioral.Mismatches);
        Assert.Equal(0, behavioral.Unsupported);
        Assert.Equal(0, behavioral.ObservationTimeouts);
        Assert.Equal(0, behavioral.Unspecified);
    }

    [Fact]
    public void Build_PrioritizesNonGreenClaimResultsBeforeSlowSuccessfulSamples()
    {
        var options = ValidatorOptions.Parse([
            "--claims", "7",
            "--claim-results-limit", "5"
        ]);
        var results = new List<ClaimValidationResult>
        {
            Result("MCC-SLOW-MATCHED", "CleanProfessionalPaid", MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid, elapsedMilliseconds: 5_000),
            Result("MCC-MISMATCH-B", "CleanProfessionalPaid", MccWorkflowValidation.MismatchedStatus, ClaimValidationOutcome.BusinessDenial, elapsedMilliseconds: 10),
            Result("MCC-MISMATCH-A", "CleanProfessionalPaid", MccWorkflowValidation.MismatchedStatus, ClaimValidationOutcome.BusinessDenial, elapsedMilliseconds: 10),
            Result("MCC-UNSUPPORTED", "EdgeCase:SubrogationWorkersComp", MccWorkflowValidation.UnsupportedStatus, ClaimValidationOutcome.Paid, elapsedMilliseconds: 20),
            Result("MCC-TIMEOUT", "EdgeCase:CobSecondaryPayer", MccWorkflowValidation.ObservationTimeoutStatus, ClaimValidationOutcome.ObservationTimeout, elapsedMilliseconds: 30),
            Result("MCC-MEDIUM-MATCHED-B", "CleanProfessionalPaid", MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid, elapsedMilliseconds: 4_000),
            Result("MCC-MEDIUM-MATCHED-A", "CleanProfessionalPaid", MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid, elapsedMilliseconds: 4_000)
        };

        var summary = MccRunSummaryBuilder.Build(
            results,
            TimeSpan.FromSeconds(10),
            options,
            DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-07T00:00:10Z"));

        Assert.Equal(
            ["MCC-TIMEOUT", "MCC-MISMATCH-A", "MCC-MISMATCH-B", "MCC-UNSUPPORTED", "MCC-SLOW-MATCHED"],
            summary.ClaimResults.Select(r => r.GeneratedClaimId).ToArray());
    }

    [Fact]
    public void Build_ScoresPaymentAmountsAgainstExplicitTolerance()
    {
        var options = ValidatorOptions.Parse(["--claims", "3"]);
        var results = new List<ClaimValidationResult>
        {
            Result("EXACT", "CleanProfessionalPaid", MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid),
            Result("ROUNDING", "CleanProfessionalPaid", MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid) with { ActualPlanPayment = 100.01m },
            Result("WRONG", "CleanProfessionalPaid", MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid) with { ActualPlanPayment = 101m }
        };

        var summary = MccRunSummaryBuilder.Build(results, TimeSpan.FromSeconds(1), options, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.Equal(0.01m, summary.PaymentTolerance);
        Assert.Equal(3, summary.PaymentComparisons);
        Assert.Equal(2, summary.PaymentMatches);
        Assert.Equal(1, summary.PaymentMismatches);
        Assert.Equal(1m, summary.MaximumPaymentDelta);
        Assert.Contains(summary.ClaimResults, r => r.GeneratedClaimId == "WRONG" && r.PaymentDelta == 1m);
    }

    [Fact]
    public void Build_ReportsServiceBusPostWindowReconciliationSeparately()
    {
        var options = ValidatorOptions.Parse(["--claims", "3"]);
        var results = new List<ClaimValidationResult>
        {
            Result("IN-WINDOW", MccWorkflowValidation.CleanProfessionalPaidScenario, MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid),
            Result("LATE", MccWorkflowValidation.CleanProfessionalPaidScenario, MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid)
                with
                {
                    ServiceBusObservationTimedOut = true,
                    ReconciledAfterObservationTimeout = true
                },
            Result("UNRESOLVED", MccWorkflowValidation.CleanProfessionalPaidScenario, MccWorkflowValidation.ObservationTimeoutStatus, ClaimValidationOutcome.ObservationTimeout)
                with
                {
                    ServiceBusObservationTimedOut = true,
                    FailureStage = "servicebus-observation"
                }
        };

        var summary = MccRunSummaryBuilder.Build(
            results,
            TimeSpan.FromSeconds(1),
            options,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        Assert.Equal(2, summary.Processed);
        Assert.Equal(2, summary.ServiceBusObservationTimeouts);
        Assert.Equal(1, summary.ServiceBusLateCompletions);
        Assert.Equal(1, summary.ServiceBusUnreconciledClaims);
        Assert.Equal(1, summary.ObservationTimeouts);
        Assert.Contains(
            summary.ClaimResults,
            result => result.GeneratedClaimId == "LATE"
                && result.ReconciledAfterObservationTimeout);
    }

    [Fact]
    public void Build_GroupsPaymentDeltaDistributionForComparableRowsOnly()
    {
        var options = ValidatorOptions.Parse(["--claims", "6"]);
        var results = new List<ClaimValidationResult>
        {
            Result("EXACT", MccWorkflowValidation.CleanProfessionalPaidScenario, MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid),
            Result("WITHIN-TOLERANCE", MccWorkflowValidation.CleanProfessionalPaidScenario, MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid)
                with { ActualPlanPayment = 100.01m },
            Result("UNDER-ONE", MccWorkflowValidation.CleanProfessionalPaidScenario, MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid)
                with { ActualPlanPayment = 100.50m },
            Result("UNDER-TEN", MccWorkflowValidation.CleanProfessionalPaidScenario, MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid)
                with { ActualPlanPayment = 105m },
            Result("OVER-TEN", MccWorkflowValidation.CleanProfessionalPaidScenario, MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid)
                with { ActualPlanPayment = 125m },
            Result("EDGE-NOT-COMPARABLE", "EdgeCase:BehavioralHealthCarveIn", MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid)
                with { ActualPlanPayment = 250m, ExpectedPlanPayment = 100m }
        };

        var summary = MccRunSummaryBuilder.Build(results, TimeSpan.FromSeconds(1), options, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.Equal(5, summary.PaymentComparisons);
        Assert.Equal(2, summary.PaymentMatches);
        Assert.Equal(3, summary.PaymentMismatches);
        Assert.Equal(25m, summary.MaximumPaymentDelta);
        Assert.Equal(5, summary.PaymentDeltaDistribution.Sum(b => b.Count));

        AssertPaymentBucket(summary, "Exact", 1);
        AssertPaymentBucket(summary, "Within tolerance", 1);
        AssertPaymentBucket(summary, "<= $1", 1);
        AssertPaymentBucket(summary, "<= $10", 1);
        AssertPaymentBucket(summary, "> $10", 1);
    }

    [Fact]
    public void Build_PublishesPaymentDeltaOnlyForPaymentComparableRows()
    {
        var options = ValidatorOptions.Parse([
            "--claims", "2",
            "--claim-results-limit", "10"
        ]);
        var results = new List<ClaimValidationResult>
        {
            Result("CLEAN-WRONG", MccWorkflowValidation.CleanProfessionalPaidScenario, MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid)
                with { ActualPlanPayment = 101m, ExpectedPlanPayment = 100m },
            Result("EDGE-NOT-COMPARABLE", "EdgeCase:BehavioralHealthCarveIn", MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid)
                with { ActualPlanPayment = 250m, ExpectedPlanPayment = 100m }
        };

        var summary = MccRunSummaryBuilder.Build(
            results,
            TimeSpan.FromSeconds(1),
            options,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        Assert.Equal(1, summary.PaymentComparisons);
        Assert.Equal(1, summary.PaymentMismatches);
        Assert.Equal(1m, summary.MaximumPaymentDelta);

        var cleanWrong = Assert.Single(summary.ClaimResults, r => r.GeneratedClaimId == "CLEAN-WRONG");
        Assert.Equal(101m, cleanWrong.ActualPlanPayment);
        Assert.Equal(100m, cleanWrong.ExpectedPlanPayment);
        Assert.Equal(1m, cleanWrong.PaymentDelta);

        var edgeCase = Assert.Single(summary.ClaimResults, r => r.GeneratedClaimId == "EDGE-NOT-COMPARABLE");
        Assert.Equal(250m, edgeCase.ActualPlanPayment);
        Assert.Null(edgeCase.ExpectedPlanPayment);
        Assert.Null(edgeCase.PaymentDelta);
    }

    [Fact]
    public void Build_IncludesLifecycleTimings()
    {
        var startedAt = DateTimeOffset.Parse("2026-07-13T00:00:00Z");
        var completedAt = DateTimeOffset.Parse("2026-07-13T00:00:01Z");
        var options = ValidatorOptions.Parse(["--claims", "1"]);
        var phase = new MassAdjudicationLifecycleTiming(
            "Corpus generation",
            "Preparation",
            1_000,
            startedAt,
            completedAt);

        var summary = MccRunSummaryBuilder.Build(
            [Result("MCC-1", MccWorkflowValidation.CleanProfessionalPaidScenario, MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid)],
            TimeSpan.FromSeconds(1),
            options,
            startedAt,
            completedAt,
            lifecycleTimings: [phase]);

        var timing = Assert.Single(summary.LifecycleTimings);
        Assert.Equal("Corpus generation", timing.Label);
        Assert.Equal("Preparation", timing.Category);
        Assert.Equal(1_000, timing.DurationMilliseconds);
        Assert.Equal(startedAt, timing.StartedAtUtc);
        Assert.Equal(completedAt, timing.CompletedAtUtc);
    }

    [Fact]
    public void Build_IncludesFixturePreparationSummary()
    {
        var startedAt = DateTimeOffset.Parse("2026-07-14T00:00:00Z");
        var completedAt = DateTimeOffset.Parse("2026-07-14T00:00:01Z");
        var options = ValidatorOptions.Parse(["--claims", "1"]);
        var fixturePreparation = new MassAdjudicationFixturePreparation(
            GeneratedClaims: 1_000,
            ProviderPoolDistinctBefore: 2_000,
            ProviderPoolDistinctAfter: 1_096,
            ProviderPoolReusedAssignments: 904,
            ProviderPoolProtectedClaims: 28,
            MembersCreated: 108,
            MembersExisting: 892,
            MemberStatusesAligned: 1_000,
            CobCoverageCreated: 0,
            CobCoverageExisting: 9,
            ProviderNetworksCreated: 0,
            ProviderNetworksExisting: 2,
            ProvidersCreated: 1_077,
            ProvidersExisting: 19);

        var summary = MccRunSummaryBuilder.Build(
            [Result("MCC-1", MccWorkflowValidation.CleanProfessionalPaidScenario, MccWorkflowValidation.MatchedStatus, ClaimValidationOutcome.Paid)],
            TimeSpan.FromSeconds(1),
            options,
            startedAt,
            completedAt,
            fixturePreparation: fixturePreparation);

        Assert.NotNull(summary.FixturePreparation);
        Assert.Equal(1_000, summary.FixturePreparation.GeneratedClaims);
        Assert.Equal(2_000, summary.FixturePreparation.ProviderPoolDistinctBefore);
        Assert.Equal(1_096, summary.FixturePreparation.ProviderPoolDistinctAfter);
        Assert.Equal(904, summary.FixturePreparation.ProviderPoolReusedAssignments);
        Assert.Equal(28, summary.FixturePreparation.ProviderPoolProtectedClaims);
        Assert.Equal(108, summary.FixturePreparation.MembersCreated);
        Assert.Equal(892, summary.FixturePreparation.MembersExisting);
        Assert.Equal(1_000, summary.FixturePreparation.MemberStatusesAligned);
        Assert.Equal(0, summary.FixturePreparation.CobCoverageCreated);
        Assert.Equal(9, summary.FixturePreparation.CobCoverageExisting);
        Assert.Equal(0, summary.FixturePreparation.ProviderNetworksCreated);
        Assert.Equal(2, summary.FixturePreparation.ProviderNetworksExisting);
        Assert.Equal(1_077, summary.FixturePreparation.ProvidersCreated);
        Assert.Equal(19, summary.FixturePreparation.ProvidersExisting);
    }

    [Fact]
    public void IsWorkflowObservationPending_SeparatesReconciliableProgressMismatches()
    {
        var expectedPend = Result(
            "EXPECTED-PEND",
            "EdgeCase:CobSecondaryPayer",
            MccWorkflowValidation.MismatchedStatus,
            ClaimValidationOutcome.Paid) with
        {
            ExpectedOutcome = ClaimValidationOutcome.Pended.ToString()
        };
        var expectedBusinessDenial = Result(
            "EXPECTED-DENIAL",
            MccWorkflowValidation.TexasStarInpatientNoAuthScenario,
            MccWorkflowValidation.MismatchedStatus,
            ClaimValidationOutcome.Paid) with
        {
            ExpectedOutcome = ClaimValidationOutcome.BusinessDenial.ToString(),
            ExpectedBusinessDenialCode = MccWorkflowValidation.PriorAuthRequiredCode
        };
        var confirmedMismatch = Result(
            "CONFIRMED-MISMATCH",
            MccWorkflowValidation.CleanProfessionalPaidScenario,
            MccWorkflowValidation.MismatchedStatus,
            ClaimValidationOutcome.BusinessDenial) with
        {
            ExpectedOutcome = ClaimValidationOutcome.Paid.ToString()
        };
        var platformFailure = expectedPend with
        {
            Outcome = ClaimValidationOutcome.PlatformFailure,
            SubmittedClaimId = null
        };

        Assert.True(MccRunSummaryBuilder.IsExpectedPendObservationPending(
            expectedPend,
            MccRunSummaryBuilder.ProcessingClaimsPhase));
        Assert.True(MccRunSummaryBuilder.IsTerminalStatusObservationPending(
            expectedBusinessDenial,
            MccRunSummaryBuilder.ProcessingClaimsPhase));
        Assert.True(MccRunSummaryBuilder.IsWorkflowObservationPending(
            expectedPend,
            MccRunSummaryBuilder.ProcessingClaimsPhase));
        Assert.True(MccRunSummaryBuilder.IsWorkflowObservationPending(
            expectedBusinessDenial,
            MccRunSummaryBuilder.ProcessingClaimsPhase));

        Assert.False(MccRunSummaryBuilder.IsWorkflowObservationPending(
            confirmedMismatch,
            MccRunSummaryBuilder.ProcessingClaimsPhase));
        Assert.False(MccRunSummaryBuilder.IsWorkflowObservationPending(expectedPend, "Completed"));
        Assert.False(MccRunSummaryBuilder.IsWorkflowObservationPending(
            platformFailure,
            MccRunSummaryBuilder.ProcessingClaimsPhase));
    }

    private static void AssertPaymentBucket(MassAdjudicationRunSummary summary, string label, int count)
    {
        var bucket = Assert.Single(summary.PaymentDeltaDistribution, b => b.Label == label);
        Assert.Equal(count, bucket.Count);
    }

    private static ClaimValidationResult Result(
        string claimId,
        string? scenario,
        string validationStatus,
        ClaimValidationOutcome outcome,
        double elapsedMilliseconds = 100)
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
            TimeSpan.FromMilliseconds(elapsedMilliseconds),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(80),
            TimeSpan.FromMilliseconds(10),
            new Dictionary<string, double> { ["benefit"] = 25 },
            outcome is ClaimValidationOutcome.BusinessDenial ? "CARC_96" : null,
            null,
            null);
    }
}
