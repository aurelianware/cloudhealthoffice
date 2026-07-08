namespace CloudHealthOffice.Tools.MccPlatformValidator;

internal static class MccRunSummaryBuilder
{
    public static MassAdjudicationRunSummary Build(
        List<ClaimValidationResult> results,
        TimeSpan elapsed,
        ValidatorOptions options,
        DateTimeOffset runStartedAtUtc,
        DateTimeOffset runCompletedAtUtc)
    {
        var processed = results.Count(r => r.Outcome is not ClaimValidationOutcome.PlatformFailure);
        var adjudicated = results.Count(r => r.Outcome is ClaimValidationOutcome.Paid);
        var pended = results.Count(r => r.Outcome is ClaimValidationOutcome.Pended);
        var businessDenials = results.Count(r => r.Outcome is ClaimValidationOutcome.BusinessDenial);
        var observationTimeouts = results.Count(r => r.Outcome is ClaimValidationOutcome.ObservationTimeout);
        var platformFailures = results.Count(r => r.Outcome is ClaimValidationOutcome.PlatformFailure);
        var validationScenarios = results.Count(r => r.ValidationStatus is not "Unspecified");
        var validationMatches = results.Count(r => r.ValidationStatus == MccWorkflowValidation.MatchedStatus);
        var validationMismatches = results.Count(r => r.ValidationStatus == MccWorkflowValidation.MismatchedStatus);
        var validationUnsupported = results.Count(r => r.ValidationStatus == MccWorkflowValidation.UnsupportedStatus);
        var validationObservationTimeouts = results.Count(r => r.ValidationStatus == MccWorkflowValidation.ObservationTimeoutStatus);
        var orderedDurations = results.Select(r => r.Elapsed.TotalMilliseconds).Order().ToArray();
        var p95 = Percentile(orderedDurations, 0.95);
        var p99 = Percentile(orderedDurations, 0.99);
        var throughput = results.Count / Math.Max(0.001, elapsed.TotalSeconds);
        var comparable = results
            .Where(r => r.Outcome is ClaimValidationOutcome.Paid)
            .Where(r => r.ActualPlanPayment.HasValue && r.ExpectedPlanPayment.HasValue)
            .ToList();
        var avgDelta = comparable.Count == 0
            ? (decimal?)null
            : comparable.Average(r => Math.Abs(r.ActualPlanPayment!.Value - r.ExpectedPlanPayment!.Value));
        var denialBreakdown = results
            .Where(r => r.Outcome is ClaimValidationOutcome.BusinessDenial)
            .GroupBy(r => r.BusinessDenialCode ?? "UNKNOWN")
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new MassAdjudicationBusinessDenialSummary(g.Key, g.Count()))
            .ToList();
        var workflowBreakdown = results
            .Where(r => !string.IsNullOrWhiteSpace(r.ValidationScenario))
            .GroupBy(r => r.ValidationScenario!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new MassAdjudicationWorkflowScenarioSummary(
                g.Key,
                g.Count(),
                g.Count(r => r.ValidationStatus == MccWorkflowValidation.MatchedStatus),
                g.Count(r => r.ValidationStatus == MccWorkflowValidation.MismatchedStatus),
                g.Count(r => r.ValidationStatus == MccWorkflowValidation.UnsupportedStatus),
                g.Count(r => r.ValidationStatus == MccWorkflowValidation.ObservationTimeoutStatus),
                g.Count(r => r.ValidationStatus == MccWorkflowValidation.UnspecifiedStatus)))
            .ToList();
        var failures = results
            .Where(r => r.Outcome is ClaimValidationOutcome.PlatformFailure or ClaimValidationOutcome.ObservationTimeout)
            .Take(5)
            .Select(r => new MassAdjudicationFailureSummary(r.GeneratedClaimId, r.FailureStage, r.Error))
            .ToList();
        var claimResults = results
            .OrderByDescending(r => r.Elapsed)
            .Take(options.PublishClaimResultsLimit)
            .Select(r => new MassAdjudicationClaimResult(
                r.GeneratedClaimId,
                r.SubmittedClaimId,
                r.ClaimType,
                r.ValidationScenario,
                r.ExpectedOutcome,
                r.ExpectedBusinessDenialCode,
                r.ValidationStatus,
                r.Outcome.ToString(),
                r.AdjudicationSuccess,
                r.BusinessDenialCode,
                r.FailureStage,
                r.Error,
                r.ActualPlanPayment,
                r.ExpectedPlanPayment,
                r.ActualPlanPayment.HasValue && r.ExpectedPlanPayment.HasValue
                    ? Math.Abs(r.ActualPlanPayment.Value - r.ExpectedPlanPayment.Value)
                    : null,
                r.Elapsed.TotalMilliseconds,
                r.SubmitElapsed.TotalMilliseconds,
                r.AdjudicationElapsed.TotalMilliseconds,
                r.UpdateElapsed.TotalMilliseconds,
                r.AdjudicationStepTimings))
            .ToList();

        return new MassAdjudicationRunSummary(
            new MassAdjudicationRun(
                options.TenantId,
                options.Claims,
                options.Seed,
                options.Parallelism,
                options.ClaimsUrl,
                options.BenefitUrl,
                options.MemberUrl,
                options.CoverageUrl,
                options.ProviderUrl,
                options.SeedMembers,
                options.SeedProviders,
                options.SkipClaimUpdate,
                options.LineOfBusiness,
                runStartedAtUtc,
                runCompletedAtUtc),
            results.Count,
            processed,
            adjudicated,
            pended,
            businessDenials,
            observationTimeouts,
            platformFailures,
            validationScenarios,
            validationMatches,
            validationMismatches,
            validationUnsupported,
            validationObservationTimeouts,
            elapsed,
            throughput,
            p95,
            p99,
            BuildStageTiming("Submit", results.Select(r => r.SubmitElapsed)),
            BuildStageTiming("Adjudicate", results.Select(r => r.AdjudicationElapsed)),
            BuildStageTiming("Writeback", results.Select(r => r.UpdateElapsed)),
            BuildAdjudicationStepTimings(results),
            avgDelta,
            denialBreakdown,
            workflowBreakdown,
            failures,
            claimResults);
    }

    private static MassAdjudicationStageTiming? BuildStageTiming(string label, IEnumerable<TimeSpan> durations)
    {
        var values = durations
            .Select(d => d.TotalMilliseconds)
            .Where(ms => ms > 0)
            .Order()
            .ToArray();

        return values.Length == 0
            ? null
            : new MassAdjudicationStageTiming(label, values.Average(), Percentile(values, 0.95));
    }

    private static IReadOnlyList<MassAdjudicationStageTiming> BuildAdjudicationStepTimings(
        IReadOnlyCollection<ClaimValidationResult> results)
    {
        return results
            .SelectMany(r => r.AdjudicationStepTimings)
            .GroupBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var values = group
                    .Select(kvp => kvp.Value)
                    .Where(ms => ms > 0)
                    .Order()
                    .ToArray();

                return values.Length == 0
                    ? null
                    : new MassAdjudicationStageTiming(
                        $"Adjudicate.{group.Key}",
                        values.Average(),
                        Percentile(values, 0.95));
            })
            .Where(timing => timing is not null)
            .Cast<MassAdjudicationStageTiming>()
            .OrderByDescending(timing => timing.AverageMilliseconds)
            .ToList();
    }

    private static double Percentile(double[] values, double percentile)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile * values.Length) - 1;
        return values[Math.Clamp(index, 0, values.Length - 1)];
    }
}
