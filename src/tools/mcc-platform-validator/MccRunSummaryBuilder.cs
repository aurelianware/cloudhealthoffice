namespace CloudHealthOffice.Tools.MccPlatformValidator;

internal static class MccRunSummaryBuilder
{
    internal const decimal PaymentTolerance = 0.01m;
    internal const string ProcessingClaimsPhase = "Processing claims";

    public static MassAdjudicationRunSummary Build(
        List<ClaimValidationResult> results,
        TimeSpan elapsed,
        ValidatorOptions options,
        DateTimeOffset runStartedAtUtc,
        DateTimeOffset runCompletedAtUtc,
        string? runId = null,
        string status = "Completed",
        int? totalClaimsOverride = null,
        MassAdjudicationRunProgress? progress = null,
        bool publishClaimResults = true,
        IReadOnlyList<MassAdjudicationLifecycleTiming>? lifecycleTimings = null,
        MassAdjudicationFixturePreparation? fixturePreparation = null)
    {
        var totalClaims = Math.Max(results.Count, totalClaimsOverride ?? results.Count);
        var processed = results.Count(r =>
            r.Outcome is not ClaimValidationOutcome.PlatformFailure
                and not ClaimValidationOutcome.ObservationTimeout);
        var adjudicated = results.Count(r => r.Outcome is ClaimValidationOutcome.Paid);
        var pended = results.Count(r => r.Outcome is ClaimValidationOutcome.Pended);
        var businessDenials = results.Count(r => r.Outcome is ClaimValidationOutcome.BusinessDenial);
        var observationTimeouts = results.Count(r => r.Outcome is ClaimValidationOutcome.ObservationTimeout);
        var platformFailures = results.Count(r => r.Outcome is ClaimValidationOutcome.PlatformFailure);
        var serviceBusObservationTimeouts = results.Count(r => r.ServiceBusObservationTimedOut);
        var serviceBusLateCompletions = results.Count(r => r.ReconciledAfterObservationTimeout);
        var serviceBusUnreconciledClaims = serviceBusObservationTimeouts - serviceBusLateCompletions;
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
            .Where(IsPaymentComparable)
            .ToList();
        var paymentDeltas = comparable
            .Select(r => PaymentDelta(r)!.Value)
            .ToList();
        var avgDelta = paymentDeltas.Count == 0
            ? (decimal?)null
            : paymentDeltas.Average();
        var paymentMatches = paymentDeltas.Count(d => d <= PaymentTolerance);
        var paymentMismatches = comparable.Count - paymentMatches;
        var maxPaymentDelta = paymentDeltas.Count == 0
            ? (decimal?)null
            : paymentDeltas.Max();
        var paymentDeltaDistribution = BuildPaymentDeltaDistribution(paymentDeltas);
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
        var claimResults = publishClaimResults
            ? SelectPublishedClaimResults(results, options.PublishClaimResultsLimit)
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
                IsPaymentComparable(r) ? r.ExpectedPlanPayment : null,
                PaymentDelta(r),
                r.Elapsed.TotalMilliseconds,
                r.SubmitElapsed.TotalMilliseconds,
                r.AdjudicationElapsed.TotalMilliseconds,
                r.UpdateElapsed.TotalMilliseconds,
                r.AdjudicationStepTimings,
                r.ServiceBusObservationTimedOut,
                r.ReconciledAfterObservationTimeout))
            .ToList()
            : new List<MassAdjudicationClaimResult>();

        return new MassAdjudicationRunSummary(
            string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N") : runId,
            status,
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
            totalClaims,
            processed,
            adjudicated,
            pended,
            businessDenials,
            observationTimeouts,
            platformFailures,
            serviceBusObservationTimeouts,
            serviceBusLateCompletions,
            serviceBusUnreconciledClaims,
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
            lifecycleTimings ?? Array.Empty<MassAdjudicationLifecycleTiming>(),
            fixturePreparation,
            avgDelta,
            PaymentTolerance,
            comparable.Count,
            paymentMatches,
            paymentMismatches,
            maxPaymentDelta,
            paymentDeltaDistribution,
            denialBreakdown,
            workflowBreakdown,
            failures,
            claimResults,
            runStartedAtUtc.UtcDateTime,
            DateTimeOffset.UtcNow.UtcDateTime,
            progress);
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

    private static IReadOnlyList<MassAdjudicationPaymentDeltaBucket> BuildPaymentDeltaDistribution(
        IReadOnlyCollection<decimal> paymentDeltas)
    {
        if (paymentDeltas.Count == 0)
        {
            return Array.Empty<MassAdjudicationPaymentDeltaBucket>();
        }

        return
        [
            new("Exact", null, 0m, paymentDeltas.Count(d => d == 0m)),
            new("Within tolerance", 0m, PaymentTolerance, paymentDeltas.Count(d => d > 0m && d <= PaymentTolerance)),
            new("<= $1", PaymentTolerance, 1m, paymentDeltas.Count(d => d > PaymentTolerance && d <= 1m)),
            new("<= $10", 1m, 10m, paymentDeltas.Count(d => d > 1m && d <= 10m)),
            new("> $10", 10m, null, paymentDeltas.Count(d => d > 10m))
        ];
    }

    private static IReadOnlyList<ClaimValidationResult> SelectPublishedClaimResults(
        IReadOnlyList<ClaimValidationResult> results,
        int limit)
    {
        if (limit <= 0)
        {
            return Array.Empty<ClaimValidationResult>();
        }

        var selected = results
            .Where(IsEvidencePriority)
            .OrderBy(PublishPriority)
            .ThenByDescending(r => r.Elapsed)
            .ThenBy(r => r.GeneratedClaimId, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        if (selected.Count >= limit)
        {
            return selected;
        }

        var selectedIds = selected
            .Select(r => r.GeneratedClaimId)
            .ToHashSet(StringComparer.Ordinal);

        selected.AddRange(results
            .Where(r => !selectedIds.Contains(r.GeneratedClaimId))
            .OrderByDescending(r => r.Elapsed)
            .ThenBy(r => r.GeneratedClaimId, StringComparer.Ordinal)
            .Take(limit - selected.Count));

        return selected;
    }

    private static bool IsEvidencePriority(ClaimValidationResult result)
        => PublishPriority(result) < 100;

    private static int PublishPriority(ClaimValidationResult result)
    {
        if (result.Outcome is ClaimValidationOutcome.PlatformFailure or ClaimValidationOutcome.ObservationTimeout
            || result.ValidationStatus == MccWorkflowValidation.ObservationTimeoutStatus)
        {
            return 0;
        }

        if (result.ValidationStatus == MccWorkflowValidation.MismatchedStatus)
        {
            return 1;
        }

        if (result.ValidationStatus == MccWorkflowValidation.UnsupportedStatus)
        {
            return 2;
        }

        if (result.ReconciledAfterObservationTimeout)
        {
            return 3;
        }

        var paymentDelta = PaymentDelta(result);
        if (paymentDelta.HasValue && paymentDelta.Value > PaymentTolerance)
        {
            return 4;
        }

        return 100;
    }

    private static bool IsPaymentComparable(ClaimValidationResult result)
        => result.Outcome is ClaimValidationOutcome.Paid
        // Amount scoring is valid only when the expected amount was authored
        // for the same benefit plan used by local adjudication. Generic MCC
        // edge cases retain disposition scoring, but their amounts come from
        // their original synthetic plans and are not contract-comparable.
        && result.ValidationScenario == MccWorkflowValidation.CleanProfessionalPaidScenario
        && result.ActualPlanPayment.HasValue
        && result.ExpectedPlanPayment.HasValue;

    private static decimal? PaymentDelta(ClaimValidationResult result)
        => IsPaymentComparable(result)
            ? Math.Abs(result.ActualPlanPayment!.Value - result.ExpectedPlanPayment!.Value)
            : null;

    internal static bool IsWorkflowObservationPending(ClaimValidationResult result, string phase)
        => IsExpectedPendObservationPending(result, phase)
            || IsTerminalStatusObservationPending(result, phase);

    internal static bool IsExpectedPendObservationPending(ClaimValidationResult result, string phase)
        => IsProcessingClaimsPhase(phase)
            && result.ExpectedOutcome == ClaimValidationOutcome.Pended.ToString()
            && result.ValidationStatus == MccWorkflowValidation.MismatchedStatus
            && result.Outcome is not ClaimValidationOutcome.PlatformFailure
            && !string.IsNullOrWhiteSpace(result.SubmittedClaimId);

    internal static bool IsTerminalStatusObservationPending(ClaimValidationResult result, string phase)
        => IsProcessingClaimsPhase(phase)
            && result.ExpectedOutcome == ClaimValidationOutcome.BusinessDenial.ToString()
            && result.ValidationStatus == MccWorkflowValidation.MismatchedStatus
            && result.Outcome is not ClaimValidationOutcome.PlatformFailure
            && !string.IsNullOrWhiteSpace(result.SubmittedClaimId);

    private static bool IsProcessingClaimsPhase(string phase)
        => phase.Equals(ProcessingClaimsPhase, StringComparison.OrdinalIgnoreCase);

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
