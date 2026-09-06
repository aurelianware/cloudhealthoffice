using RfaiService.Models;
using RfaiService.Services;

namespace RfaiService.Tests;

/// <summary>
/// The additional-information aggregate's own rules, in isolation.
///
/// These are the invariants every intake path depends on — the internal API, the
/// Da Vinci CDex surface in fhir-service, and 275 correlation in
/// attachment-service — so they are proven here once rather than three times at
/// three edges.
/// </summary>
public class RfaiCaseLifecycleTests
{
    private const string Tenant = "tenant-a";
    private const string AuthNumber = "PAS-20260906-ABCD1234";

    private static RfaiCreationRequest Request(
        string? correlationKey = "decision-1",
        string authNumber = AuthNumber,
        string tenant = Tenant,
        List<RequestedItem>? items = null) => new()
    {
        TenantId = tenant,
        AuthNumber = authNumber,
        CorrelationKey = correlationKey,
        ReviewDecision = "A4",
        RequestSource = RfaiRequestSources.ReviewDecisionA4,
        RequestedItems = items ??
        [
            new RequestedItem { Code = "AS", LoincCode = "18842-5", Description = "Discharge summary" },
        ],
    };

    private static RfaiResponseArtifact Artifact(string submissionId = "sub-1") => new()
    {
        SubmissionId = submissionId,
        StorageProvider = "cdex-attachments",
        StorageKey = "tenant-a/rfai-1/sub-1.pdf",
        FileHash = new string('a', 64),
        ContentType = "application/pdf",
        SizeBytes = 1024,
        Channel = RfaiResponseChannels.CdexSubmitAttachment,
    };

    // ── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public void ARequestThatNamesNothingIsNotARequest()
    {
        // This is what stops a generic pended state from being turned into a
        // documentation request: the caller has to say what is actually needed.
        var validation = RfaiCaseLifecycle.Validate(Request(items: []));

        validation.IsValid.Should().BeFalse();
        validation.Error.Should().Contain("requestedItem");
    }

    [Fact]
    public void ARequestedItemWithoutADescriptionIsRefused()
    {
        var validation = RfaiCaseLifecycle.Validate(Request(
            items: [new RequestedItem { Code = "AS", Description = "  " }]));

        validation.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("PAS 2026 ABCD")]
    [InlineData("../../etc/passwd")]
    public void AnUnsafeAuthorizationNumberIsRefused(string authNumber)
        => RfaiCaseLifecycle.Validate(Request(authNumber: authNumber)).IsValid.Should().BeFalse();

    [Fact]
    public void AValidRequestPasses()
        => RfaiCaseLifecycle.Validate(Request()).IsValid.Should().BeTrue();

    // ── Identity ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheSameDecisionAlwaysAddressesTheSameDocument()
    {
        // Idempotency lives in the PRIMARY KEY, not in an application-level
        // read-then-write, so two workers racing on one decision collide on the
        // insert rather than both succeeding.
        var a = RfaiCaseLifecycle.DeterministicId(Tenant, AuthNumber, "decision-1");
        var b = RfaiCaseLifecycle.DeterministicId(Tenant, AuthNumber, "decision-1");

        a.Should().Be(b);
        RfaiCaseLifecycle.IsDeterministicId(a).Should().BeTrue();
    }

    [Theory]
    [InlineData("tenant-b", AuthNumber, "decision-1")]
    [InlineData(Tenant, "PAS-20260906-ZZZZ9999", "decision-1")]
    [InlineData(Tenant, AuthNumber, "decision-2")]
    public void ADifferentTenantAuthorizationOrDecisionAddressesADifferentDocument(
        string tenant, string authNumber, string correlationKey)
        => RfaiCaseLifecycle.DeterministicId(tenant, authNumber, correlationKey)
            .Should().NotBe(RfaiCaseLifecycle.DeterministicId(Tenant, AuthNumber, "decision-1"));

    [Fact]
    public void WithoutACorrelationKeyThereIsNothingToBeIdempotentAgainst()
    {
        // Reported honestly rather than faked: the id is fresh, and
        // IsDeterministicId says replay protection does not apply.
        var id = RfaiCaseLifecycle.DeterministicId(Tenant, AuthNumber, correlationKey: null);

        RfaiCaseLifecycle.IsDeterministicId(id).Should().BeFalse();
        id.Should().NotBe(RfaiCaseLifecycle.DeterministicId(Tenant, AuthNumber, null));
    }

    [Fact]
    public void TheTrackingIdIsNotDerivableFromWhatACallerAlreadyKnows()
    {
        // It is one of the keys an intake must match, so deriving it from the
        // tenant and authorization number would hand it to anyone who knows them.
        var now = new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc);

        var first = RfaiCaseLifecycle.Create(Request(), sequence: 1, now).TrackingId;
        var second = RfaiCaseLifecycle.Create(Request(), sequence: 1, now).TrackingId;

        first.Should().NotBe(second);
        first.Should().StartWith("RFAI-20260906-");
        RfaiCaseLifecycle.IsSafeIdentifier(first).Should().BeTrue();
    }

    [Fact]
    public void ALaterCycleContinuesTheSequenceRatherThanReplacingIt()
    {
        var existing = new[]
        {
            RfaiCaseLifecycle.Create(Request(), sequence: 1, DateTime.UtcNow),
            RfaiCaseLifecycle.Create(Request("decision-2"), sequence: 2, DateTime.UtcNow),
        };

        RfaiCaseLifecycle.NextSequence(existing).Should().Be(3);
        RfaiCaseLifecycle.NextSequence([]).Should().Be(1);
    }

    // ── Response intake ──────────────────────────────────────────────────────

    [Fact]
    public void AFirstResponseMovesTheCaseAndAnnouncesTheTransitionOnce()
    {
        var rfaiCase = RfaiCaseLifecycle.Create(Request(), 1, DateTime.UtcNow);

        var result = RfaiCaseLifecycle.OfferResponse(rfaiCase, [Artifact()], DateTime.UtcNow);

        result.Outcome.Should().Be(RfaiIntakeOutcome.Accepted);
        result.TransitionedToDocsReceived.Should().BeTrue();
        result.RequiresPersist.Should().BeTrue();
        rfaiCase.Status.Should().Be(RfaiStatus.DocsReceived);
        rfaiCase.RespondedAt.Should().NotBeNull();
        rfaiCase.ReceivedAttachments.Should().ContainSingle();
    }

    [Fact]
    public void ReplayingASubmissionRecordsNothingAndAnnouncesNothing()
    {
        var rfaiCase = RfaiCaseLifecycle.Create(Request(), 1, DateTime.UtcNow);
        RfaiCaseLifecycle.OfferResponse(rfaiCase, [Artifact()], DateTime.UtcNow);

        var replay = RfaiCaseLifecycle.OfferResponse(rfaiCase, [Artifact()], DateTime.UtcNow);

        replay.Outcome.Should().Be(RfaiIntakeOutcome.DuplicateIgnored);
        replay.Recorded.Should().BeEmpty();
        replay.RequiresPersist.Should().BeFalse();
        replay.TransitionedToDocsReceived.Should().BeFalse(
            "a retry must not restart the review clock a second time");
        rfaiCase.ReceivedAttachments.Should().ContainSingle();
    }

    [Fact]
    public void ANewArtifactUnderTheSameRequestIsAppendedNotSubstituted()
    {
        var rfaiCase = RfaiCaseLifecycle.Create(Request(), 1, DateTime.UtcNow);
        var firstReceipt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        RfaiCaseLifecycle.OfferResponse(rfaiCase, [Artifact("sub-1")], firstReceipt);
        var second = RfaiCaseLifecycle.OfferResponse(
            rfaiCase, [Artifact("sub-2")], firstReceipt.AddDays(1));

        second.Outcome.Should().Be(RfaiIntakeOutcome.Accepted);
        second.TransitionedToDocsReceived.Should().BeFalse("the case was already answered");
        rfaiCase.ReceivedAttachments.Select(a => a.SubmissionId)
            .Should().BeEquivalentTo(["sub-1", "sub-2"]);
        rfaiCase.RespondedAt.Should().Be(firstReceipt,
            "the FIRST response is the one the response date records");
    }

    [Fact]
    public void AMixOfNewAndAlreadySeenArtifactsRecordsOnlyTheNewOnes()
    {
        var rfaiCase = RfaiCaseLifecycle.Create(Request(), 1, DateTime.UtcNow);
        RfaiCaseLifecycle.OfferResponse(rfaiCase, [Artifact("sub-1")], DateTime.UtcNow);

        var result = RfaiCaseLifecycle.OfferResponse(
            rfaiCase, [Artifact("sub-1"), Artifact("sub-2")], DateTime.UtcNow);

        result.Recorded.Should().ContainSingle(a => a.SubmissionId == "sub-2");
        rfaiCase.ReceivedAttachments.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(RfaiStatus.Closed)]
    [InlineData(RfaiStatus.Cancelled)]
    public void AFinishedRequestTakesNoFurtherResponse(RfaiStatus status)
    {
        var rfaiCase = RfaiCaseLifecycle.Create(Request(), 1, DateTime.UtcNow);
        rfaiCase.Status = status;

        var result = RfaiCaseLifecycle.OfferResponse(rfaiCase, [Artifact()], DateTime.UtcNow);

        result.Outcome.Should().Be(RfaiIntakeOutcome.CaseNotOpenForResponse);
        result.IsRefusal.Should().BeTrue();
        rfaiCase.ReceivedAttachments.Should().BeEmpty("a refused response records nothing");
    }

    [Fact]
    public void AnAnsweredRequestStillTakesASupplementaryResponse()
    {
        // DocsReceived is not an ending. A provider who sends one more document
        // must not be turned away, and a retry has to reach the duplicate check.
        var rfaiCase = RfaiCaseLifecycle.Create(Request(), 1, DateTime.UtcNow);
        RfaiCaseLifecycle.OfferResponse(rfaiCase, [Artifact("sub-1")], DateTime.UtcNow);

        RfaiCaseLifecycle.OfferResponse(rfaiCase, [Artifact("sub-2")], DateTime.UtcNow)
            .Outcome.Should().Be(RfaiIntakeOutcome.Accepted);
    }

    [Fact]
    public void TheArtifactCapIsEnforcedByTheAggregateItself()
    {
        // Enforced here rather than at an edge, so no intake path can bypass it.
        var rfaiCase = RfaiCaseLifecycle.Create(Request(), 1, DateTime.UtcNow);

        var many = Enumerable.Range(0, RfaiCaseLifecycle.MaxArtifactsPerCase + 1)
            .Select(i => Artifact($"sub-{i}"))
            .ToList();

        var result = RfaiCaseLifecycle.OfferResponse(rfaiCase, many, DateTime.UtcNow);

        result.Outcome.Should().Be(RfaiIntakeOutcome.TooManyArtifacts);
        rfaiCase.ReceivedAttachments.Should().BeEmpty();
    }

    [Fact]
    public void AnArtifactWithoutASubmissionIdIsSkippedNotGuessedAt()
    {
        var rfaiCase = RfaiCaseLifecycle.Create(Request(), 1, DateTime.UtcNow);

        var result = RfaiCaseLifecycle.OfferResponse(
            rfaiCase, [Artifact() with { SubmissionId = "  " }], DateTime.UtcNow);

        result.Outcome.Should().Be(RfaiIntakeOutcome.DuplicateIgnored);
        rfaiCase.ReceivedAttachments.Should().BeEmpty();
    }

    [Fact]
    public void ReceivingDocumentsCountsThemAndJudgesNothing()
    {
        // Whether a document actually ANSWERS a clinical question is the
        // reviewer's call — which is exactly why the round trip returns an
        // authorization to review rather than approving it.
        var rfaiCase = RfaiCaseLifecycle.Create(Request(items:
        [
            new RequestedItem { Description = "Discharge summary", Required = true },
            new RequestedItem { Description = "Operative report", Required = true },
        ]), 1, DateTime.UtcNow);

        RfaiCaseLifecycle.OfferResponse(rfaiCase, [Artifact("sub-1")], DateTime.UtcNow);
        RfaiCaseLifecycle.AllRequiredItemsAnswered(rfaiCase).Should().BeFalse();

        RfaiCaseLifecycle.OfferResponse(rfaiCase, [Artifact("sub-2")], DateTime.UtcNow);
        RfaiCaseLifecycle.AllRequiredItemsAnswered(rfaiCase).Should().BeTrue();
    }

    // ── Closure and provenance ───────────────────────────────────────────────

    [Fact]
    public void ClosingAndCancellingAreOneWayAndRecordWhoAndWhy()
    {
        var closed = RfaiCaseLifecycle.Create(Request(), 1, DateTime.UtcNow);

        RfaiCaseLifecycle.Close(closed, "reviewer-1", "Decision made.", DateTime.UtcNow)
            .Should().BeTrue();
        closed.Status.Should().Be(RfaiStatus.Closed);
        closed.ClosedBy.Should().Be("reviewer-1");
        closed.ClosureReason.Should().Be("Decision made.");
        closed.ClosedAt.Should().NotBeNull();

        RfaiCaseLifecycle.Close(closed, "reviewer-2", "again", DateTime.UtcNow)
            .Should().BeFalse("a finished cycle does not close twice");
        RfaiCaseLifecycle.Cancel(closed, "reviewer-2", "again", DateTime.UtcNow)
            .Should().BeFalse();
        closed.ClosedBy.Should().Be("reviewer-1", "the original closure survives");
    }

    [Fact]
    public void DeliveryIsRecordedAsProvenanceAndTheFirstTimeIsKept()
    {
        var rfaiCase = RfaiCaseLifecycle.Create(Request(), 1, DateTime.UtcNow);
        var first = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

        RfaiCaseLifecycle.MarkDelivered(rfaiCase, first);
        RfaiCaseLifecycle.MarkDelivered(rfaiCase, first.AddHours(3));

        rfaiCase.FirstDeliveredAt.Should().Be(first);
        rfaiCase.LastDeliveredAt.Should().Be(first.AddHours(3));
        rfaiCase.DeliveryCount.Should().Be(2);
    }

    [Fact]
    public void OverdueIsDerivedFromTheDueDateNotStored()
    {
        // Nothing in this repository sweeps due dates, so expiry is REPORTED,
        // never recorded — a stored "Expired" nothing sets would be a lie.
        var now = new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc);
        var rfaiCase = RfaiCaseLifecycle.Create(Request(), 1, now);

        rfaiCase.IsOverdue(now).Should().BeFalse("no due date was set");

        rfaiCase.DueDate = now.AddDays(-1);
        rfaiCase.IsOverdue(now).Should().BeTrue();

        rfaiCase.Status = RfaiStatus.DocsReceived;
        rfaiCase.IsOverdue(now).Should().BeFalse("an answered request is not overdue");
    }
}
