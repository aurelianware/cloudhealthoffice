using System.Collections.Concurrent;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

internal sealed class MccServiceBusReconciler
{
    private readonly IClaimStatusSource _source;

    public MccServiceBusReconciler(IClaimStatusSource source)
    {
        _source = source;
    }

    public async Task<ServiceBusReconciliationResult> ReconcileAsync(
        IReadOnlyList<ClaimValidationResult> results,
        TimeSpan timeout,
        TimeSpan interval,
        int maxParallelism,
        CancellationToken cancellationToken = default)
    {
        var candidates = results
            .Where(IsCandidate)
            .ToList();
        if (candidates.Count == 0)
        {
            return new ServiceBusReconciliationResult(results.ToList(), 0, 0, 0);
        }

        var reconciled = new ConcurrentDictionary<string, ClaimValidationResult>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(candidates.Count, Math.Max(1, maxParallelism)),
                CancellationToken = cancellationToken
            },
            async (candidate, ct) =>
            {
                reconciled[candidate.GeneratedClaimId] = await ReconcileOneAsync(
                    candidate,
                    timeout,
                    interval,
                    ct);
            });

        var updated = results
            .Select(result => reconciled.TryGetValue(result.GeneratedClaimId, out var replacement)
                ? replacement
                : result)
            .OrderBy(result => result.GeneratedClaimId, StringComparer.Ordinal)
            .ToList();
        var lateCompletions = reconciled.Values.Count(result => result.ReconciledAfterObservationTimeout);

        return new ServiceBusReconciliationResult(
            updated,
            candidates.Count,
            lateCompletions,
            candidates.Count - lateCompletions);
    }

    private async Task<ClaimValidationResult> ReconcileOneAsync(
        ClaimValidationResult result,
        TimeSpan timeout,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        try
        {
            while (true)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return result with
                    {
                        Error =
                            $"Claim remained nonterminal after the initial observation timeout and " +
                            $"{timeout.TotalSeconds:N0}s post-window reconciliation"
                    };
                }

                ObservedClaimStatus? observed;
                using (var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    attemptTimeout.CancelAfter(
                        remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5));
                    try
                    {
                        observed = await _source.GetAsync(result.SubmittedClaimId!, attemptTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        observed = null;
                    }
                    catch (HttpRequestException)
                    {
                        observed = null;
                    }
                }

                if (observed?.IsTerminal is true)
                {
                    var businessDenialCode = observed.Outcome is ClaimValidationOutcome.BusinessDenial
                        ? observed.BusinessDenialCode
                        : null;
                    var expected = new ExpectedValidation(
                        result.ValidationScenario,
                        ParseExpectedOutcome(result.ExpectedOutcome),
                        result.ExpectedBusinessDenialCode);

                    return result with
                    {
                        Outcome = observed.Outcome,
                        AdjudicationSuccess = observed.Outcome is ClaimValidationOutcome.Paid,
                        ActualPlanPayment = observed.PlanPayment,
                        BusinessDenialCode = businessDenialCode,
                        ValidationStatus = MccWorkflowValidation.ValidationStatus(
                            expected,
                            observed.Outcome,
                            businessDenialCode),
                        FailureStage = null,
                        Error = null,
                        ReconciledAfterObservationTimeout = true
                    };
                }

                remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return result with
                    {
                        Error =
                            $"Claim remained nonterminal after the initial observation timeout and " +
                            $"{timeout.TotalSeconds:N0}s post-window reconciliation"
                    };
                }

                var delay = interval <= TimeSpan.Zero || interval < remaining ? interval : remaining;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return result with
            {
                Error = $"Post-window reconciliation failed: {ex.Message}"
            };
        }
    }

    private static bool IsCandidate(ClaimValidationResult result)
        => result.ServiceBusObservationTimedOut
            && !result.ReconciledAfterObservationTimeout
            && result.Outcome is ClaimValidationOutcome.ObservationTimeout
            && !string.IsNullOrWhiteSpace(result.SubmittedClaimId);

    private static ClaimValidationOutcome? ParseExpectedOutcome(string? expectedOutcome)
        => Enum.TryParse<ClaimValidationOutcome>(expectedOutcome, ignoreCase: true, out var parsed)
            ? parsed
            : null;
}

internal sealed record ServiceBusReconciliationResult(
    List<ClaimValidationResult> Results,
    int Candidates,
    int Reconciled,
    int Unreconciled);
