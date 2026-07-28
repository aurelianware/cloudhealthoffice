using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccServiceBusReconcilerTests
{
    [Fact]
    public async Task ReconcileAsync_WhenLatePaidOutcomeExists_RescoresWorkflowAndPayment()
    {
        var reconciler = new MccServiceBusReconciler(new SequenceClaimStatusSource([
            new ObservedClaimStatus(
                ClaimValidationOutcome.Paid,
                "Paid",
                null,
                IsTerminal: true,
                PlanPayment: 123.45m)
        ]));

        var result = await reconciler.ReconcileAsync(
            [TimedOutResult() with
            {
                ExpectedOutcome = ClaimValidationOutcome.Paid.ToString(),
                ExpectedPlanPayment = 123.45m
            }],
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            maxParallelism: 1);

        var claim = Assert.Single(result.Results);
        Assert.Equal(1, result.Candidates);
        Assert.Equal(1, result.Reconciled);
        Assert.Equal(0, result.Unreconciled);
        Assert.Equal(ClaimValidationOutcome.Paid, claim.Outcome);
        Assert.Equal(123.45m, claim.ActualPlanPayment);
        Assert.Equal(MccWorkflowValidation.MatchedStatus, claim.ValidationStatus);
        Assert.True(claim.ServiceBusObservationTimedOut);
        Assert.True(claim.ReconciledAfterObservationTimeout);
        Assert.Null(claim.FailureStage);
        Assert.Null(claim.Error);
    }

    [Fact]
    public async Task ReconcileAsync_WhenLateBusinessDenialExists_PreservesDenialEvidence()
    {
        var reconciler = new MccServiceBusReconciler(new SequenceClaimStatusSource([
            new ObservedClaimStatus(
                ClaimValidationOutcome.BusinessDenial,
                "Denied",
                null,
                IsTerminal: true,
                BusinessDenialCode: MccWorkflowValidation.PriorAuthRequiredCode)
        ]));

        var result = await reconciler.ReconcileAsync(
            [TimedOutResult() with
            {
                ExpectedOutcome = ClaimValidationOutcome.BusinessDenial.ToString(),
                ExpectedBusinessDenialCode = MccWorkflowValidation.PriorAuthRequiredCode
            }],
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            maxParallelism: 1);

        var claim = Assert.Single(result.Results);
        Assert.Equal(ClaimValidationOutcome.BusinessDenial, claim.Outcome);
        Assert.Equal(MccWorkflowValidation.PriorAuthRequiredCode, claim.BusinessDenialCode);
        Assert.Equal(MccWorkflowValidation.MatchedStatus, claim.ValidationStatus);
        Assert.True(claim.ReconciledAfterObservationTimeout);
    }

    [Fact]
    public async Task ReconcileAsync_WhenClaimRemainsNonterminal_PreservesUnresolvedTimeout()
    {
        var reconciler = new MccServiceBusReconciler(new SequenceClaimStatusSource([
            new ObservedClaimStatus(
                ClaimValidationOutcome.PlatformFailure,
                "InAdjudication",
                null,
                IsTerminal: false)
        ]));

        var result = await reconciler.ReconcileAsync(
            [TimedOutResult()],
            TimeSpan.Zero,
            TimeSpan.Zero,
            maxParallelism: 1);

        var claim = Assert.Single(result.Results);
        Assert.Equal(1, result.Candidates);
        Assert.Equal(0, result.Reconciled);
        Assert.Equal(1, result.Unreconciled);
        Assert.Equal(ClaimValidationOutcome.ObservationTimeout, claim.Outcome);
        Assert.False(claim.ReconciledAfterObservationTimeout);
        Assert.Contains("remained nonterminal", claim.Error);
    }

    [Fact]
    public async Task ReconcileAsync_IgnoresResultsThatDidNotTimeOutInServiceBusObservation()
    {
        var source = new SequenceClaimStatusSource([]);
        var reconciler = new MccServiceBusReconciler(source);
        var paid = TimedOutResult() with
        {
            Outcome = ClaimValidationOutcome.Paid,
            ServiceBusObservationTimedOut = false
        };

        var result = await reconciler.ReconcileAsync(
            [paid],
            TimeSpan.Zero,
            TimeSpan.Zero,
            maxParallelism: 1);

        Assert.Equal(0, result.Candidates);
        Assert.Equal(paid, Assert.Single(result.Results));
        Assert.Equal(0, source.ReadCount);
    }

    private static ClaimValidationResult TimedOutResult()
        => new(
            "MCC-LATE-0001",
            "submitted-MCC-LATE-0001",
            "Professional",
            MccWorkflowValidation.CleanProfessionalPaidScenario,
            ClaimValidationOutcome.Paid.ToString(),
            null,
            MccWorkflowValidation.ObservationTimeoutStatus,
            ClaimValidationOutcome.ObservationTimeout,
            false,
            null,
            123.45m,
            TimeSpan.FromSeconds(180),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(180),
            TimeSpan.Zero,
            new Dictionary<string, double>(),
            null,
            "servicebus-observation",
            "Timed out waiting for Service Bus adjudication",
            ServiceBusObservationTimedOut: true);

    private sealed class SequenceClaimStatusSource : IClaimStatusSource
    {
        private readonly Queue<ObservedClaimStatus?> _statuses;
        private ObservedClaimStatus? _last;

        public SequenceClaimStatusSource(IEnumerable<ObservedClaimStatus?> statuses)
        {
            _statuses = new Queue<ObservedClaimStatus?>(statuses);
        }

        public int ReadCount { get; private set; }

        public Task<ObservedClaimStatus?> GetAsync(
            string submittedClaimId,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            if (_statuses.Count > 0)
            {
                _last = _statuses.Dequeue();
            }

            return Task.FromResult(_last);
        }
    }
}
