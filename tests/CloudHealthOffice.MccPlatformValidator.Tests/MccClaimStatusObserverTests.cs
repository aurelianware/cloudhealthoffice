using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccClaimStatusObserverTests
{
    [Fact]
    public async Task ObserveExpectedPendAsync_WhenFirstPollIsPended_ReturnsMatchedPended()
    {
        var observer = new MccClaimStatusObserver(new FakeClaimStatusSource([
            new ObservedClaimStatus(ClaimValidationOutcome.Pended, "Pended", "COB", IsTerminal: true)
        ]));

        var result = await observer.ObserveExpectedPendAsync(
            Result(ClaimValidationOutcome.BusinessDenial),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(100));

        Assert.Equal(ClaimValidationOutcome.Pended, result.Outcome);
        Assert.Equal(MccWorkflowValidation.MatchedStatus, result.ValidationStatus);
    }

    [Fact]
    public async Task ObserveExpectedPendAsync_WhenPendArrivesAfterIntermediatePolls_ReturnsMatchedPended()
    {
        var observer = new MccClaimStatusObserver(new FakeClaimStatusSource([
            new ObservedClaimStatus(ClaimValidationOutcome.PlatformFailure, "InAdjudication", null, IsTerminal: false),
            new ObservedClaimStatus(ClaimValidationOutcome.PlatformFailure, "Received", null, IsTerminal: false),
            new ObservedClaimStatus(ClaimValidationOutcome.Pended, "Pended", "COB", IsTerminal: true)
        ]));

        var result = await observer.ObserveExpectedPendAsync(
            Result(ClaimValidationOutcome.BusinessDenial),
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero);

        Assert.Equal(ClaimValidationOutcome.Pended, result.Outcome);
        Assert.Equal(MccWorkflowValidation.MatchedStatus, result.ValidationStatus);
    }

    [Fact]
    public async Task ObserveExpectedPendAsync_WhenObservedPaid_ReturnsMismatched()
    {
        var observer = new MccClaimStatusObserver(new FakeClaimStatusSource([
            new ObservedClaimStatus(ClaimValidationOutcome.Paid, "Approved", null, IsTerminal: true)
        ]));

        var result = await observer.ObserveExpectedPendAsync(
            Result(ClaimValidationOutcome.BusinessDenial),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(100));

        Assert.Equal(ClaimValidationOutcome.Paid, result.Outcome);
        Assert.Equal(MccWorkflowValidation.MismatchedStatus, result.ValidationStatus);
    }

    [Fact]
    public async Task ObserveExpectedPendAsync_WhenNoTerminalStateBeforeTimeout_ReturnsObservationTimeout()
    {
        var observer = new MccClaimStatusObserver(new FakeClaimStatusSource([
            new ObservedClaimStatus(ClaimValidationOutcome.PlatformFailure, "InAdjudication", null, IsTerminal: false)
        ]));

        var result = await observer.ObserveExpectedPendAsync(
            Result(ClaimValidationOutcome.BusinessDenial),
            TimeSpan.Zero,
            TimeSpan.Zero);

        Assert.Equal(ClaimValidationOutcome.ObservationTimeout, result.Outcome);
        Assert.Equal(MccWorkflowValidation.ObservationTimeoutStatus, result.ValidationStatus);
        Assert.Equal("pend-observation", result.FailureStage);
    }

    [Fact]
    public async Task ObserveExpectedPendAsync_WhenStatusReadFails_ReturnsObservationTimeoutResult()
    {
        var observer = new MccClaimStatusObserver(new ThrowingClaimStatusSource());

        var result = await observer.ObserveExpectedPendAsync(
            Result(ClaimValidationOutcome.BusinessDenial),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(100));

        Assert.Equal(ClaimValidationOutcome.ObservationTimeout, result.Outcome);
        Assert.Equal(MccWorkflowValidation.ObservationTimeoutStatus, result.ValidationStatus);
        Assert.Equal("pend-observation", result.FailureStage);
        Assert.Contains("claim read boom", result.Error);
    }

    [Fact]
    public void FromClaimJson_ParsesNumericPendedStatusAndPendCode()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
        {
          "status": 4,
          "pendDetails": {
            "pendCode": "COB"
          }
        }
        """);

        var observed = ObservedClaimStatus.FromClaimJson(document.RootElement);

        Assert.Equal(ClaimValidationOutcome.Pended, observed.Outcome);
        Assert.Equal("4", observed.RawStatus);
        Assert.Equal("COB", observed.PendCode);
        Assert.True(observed.IsTerminal);
    }

    [Fact]
    public void FromClaimJson_ParsesNumericBusinessDenialCode()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
        {
          "status": 6,
          "adjudicationResult": {
            "denialReasonCode": "197"
          }
        }
        """);

        var observed = ObservedClaimStatus.FromClaimJson(document.RootElement);

        Assert.Equal(ClaimValidationOutcome.BusinessDenial, observed.Outcome);
        Assert.Equal(MccWorkflowValidation.PriorAuthRequiredCode, observed.BusinessDenialCode);
        Assert.True(observed.IsTerminal);
    }

    [Fact]
    public void FromClaimJson_ParsesPersistedPlanPayment()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
        {
          "status": 5,
          "adjudicationResult": {
            "payerPayment": 123.45
          }
        }
        """);

        var observed = ObservedClaimStatus.FromClaimJson(document.RootElement);

        Assert.Equal(ClaimValidationOutcome.Paid, observed.Outcome);
        Assert.Equal(123.45m, observed.PlanPayment);
        Assert.True(observed.IsTerminal);
    }

    [Fact]
    public async Task DetectUnexpectedPendAsync_WhenExpectedPaidPersistedPended_ReturnsMismatch()
    {
        var observer = new MccClaimStatusObserver(new FakeClaimStatusSource([
            new ObservedClaimStatus(ClaimValidationOutcome.Pended, "Pended", "MED_REVIEW", IsTerminal: true)
        ]));
        var source = Result(ClaimValidationOutcome.Paid) with
        {
            ExpectedOutcome = ClaimValidationOutcome.Paid.ToString(),
            ValidationStatus = MccWorkflowValidation.MatchedStatus
        };

        var result = await observer.DetectUnexpectedPendAsync(source);

        Assert.Equal(ClaimValidationOutcome.Pended, result.Outcome);
        Assert.Equal(MccWorkflowValidation.MismatchedStatus, result.ValidationStatus);
        Assert.Equal("false-pend-observation", result.FailureStage);
        Assert.Contains("MED_REVIEW", result.Error);
    }

    [Fact]
    public async Task DetectUnexpectedPendAsync_WhenExpectedDeniedPersistedDenied_PreservesResult()
    {
        var observer = new MccClaimStatusObserver(new FakeClaimStatusSource([
            new ObservedClaimStatus(ClaimValidationOutcome.BusinessDenial, "Denied", null, IsTerminal: true)
        ]));
        var source = Result(ClaimValidationOutcome.BusinessDenial) with
        {
            ExpectedOutcome = ClaimValidationOutcome.BusinessDenial.ToString(),
            ValidationStatus = MccWorkflowValidation.MatchedStatus
        };

        var result = await observer.DetectUnexpectedPendAsync(source);

        Assert.Equal(source, result);
    }

    [Fact]
    public async Task DetectUnexpectedPendAsync_WhenExpectedDeniedDirectPaidButPersistedDenied_ReconcilesToMatched()
    {
        var observer = new MccClaimStatusObserver(new FakeClaimStatusSource([
            new ObservedClaimStatus(
                ClaimValidationOutcome.BusinessDenial,
                "Denied",
                null,
                IsTerminal: true,
                MccWorkflowValidation.PriorAuthRequiredCode)
        ]));
        var source = Result(ClaimValidationOutcome.Paid) with
        {
            ExpectedOutcome = ClaimValidationOutcome.BusinessDenial.ToString(),
            ExpectedBusinessDenialCode = MccWorkflowValidation.PriorAuthRequiredCode,
            ValidationStatus = MccWorkflowValidation.MismatchedStatus,
            BusinessDenialCode = null
        };

        var result = await observer.DetectUnexpectedPendAsync(
            source,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero);

        Assert.Equal(ClaimValidationOutcome.BusinessDenial, result.Outcome);
        Assert.Equal(MccWorkflowValidation.PriorAuthRequiredCode, result.BusinessDenialCode);
        Assert.Equal(MccWorkflowValidation.MatchedStatus, result.ValidationStatus);
        Assert.Equal("terminal-status-observation", result.FailureStage);
    }

    private static ClaimValidationResult Result(ClaimValidationOutcome startingOutcome)
    {
        return new ClaimValidationResult(
            "MCC-PEND-0001",
            "submitted-MCC-PEND-0001",
            "EdgeCase",
            "EdgeCase:CobSecondaryPayer",
            ClaimValidationOutcome.Pended.ToString(),
            "CARC_22",
            MccWorkflowValidation.MismatchedStatus,
            startingOutcome,
            AdjudicationSuccess: false,
            ActualPlanPayment: null,
            ExpectedPlanPayment: null,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(80),
            TimeSpan.FromMilliseconds(10),
            new Dictionary<string, double>(),
            BusinessDenialCode: null,
            FailureStage: null,
            Error: null);
    }

    private sealed class FakeClaimStatusSource : IClaimStatusSource
    {
        private readonly Queue<ObservedClaimStatus?> _statuses;

        public FakeClaimStatusSource(IEnumerable<ObservedClaimStatus?> statuses)
        {
            _statuses = new Queue<ObservedClaimStatus?>(statuses);
        }

        public Task<ObservedClaimStatus?> GetAsync(string submittedClaimId, CancellationToken cancellationToken)
            => Task.FromResult(_statuses.Count == 0 ? null : _statuses.Dequeue());
    }

    private sealed class ThrowingClaimStatusSource : IClaimStatusSource
    {
        public Task<ObservedClaimStatus?> GetAsync(string submittedClaimId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("claim read boom");
    }
}
