using System.Net;
using System.Text.Json;
using AppealsService.Models;
using AppealsService.Repositories;
using Microsoft.Azure.Cosmos;

using AppealsService.Tests.Fakes;

namespace AppealsService.Tests.Repositories;

/// <summary>
/// Cosmos <see cref="AppealRepository.TransitionStatusAsync"/> against a
/// mocked <see cref="Container"/>: the replace must be built from the fresh
/// read (never from the caller's snapshot), pinned to that read's ETag, and
/// rebuilt from a new read on 412. There is no Cosmos emulator in the test
/// infrastructure, so this checks the write the repository sends, not the
/// server's handling of it.
/// </summary>
public class AppealRepositoryCosmosTransitionTests
{
    private readonly Mock<Container> _container = new();
    private readonly Mock<IAppealEventSink> _events = new();
    private readonly Queue<(Appeal Doc, string ETag)> _reads = new();
    private readonly List<(Appeal Doc, string? IfMatch)> _replaces = new();
    private readonly Queue<bool> _replaceFails412 = new();
    private readonly AppealRepository _repo;

    public AppealRepositoryCosmosTransitionTests()
    {
        var database = new Mock<Database>();
        database.Setup(d => d.GetContainer(AppealRepository.AppealsContainerName)).Returns(_container.Object);
        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetDatabase("db")).Returns(database.Object);

        _container
            .Setup(c => c.ReadItemAsync<Appeal>(It.IsAny<string>(), It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var (doc, etag) = _reads.Dequeue();
                return Response(RoundTrip(doc), etag);
            });

        _container
            .Setup(c => c.ReplaceItemAsync(It.IsAny<Appeal>(), It.IsAny<string>(), It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Appeal item, string _, PartitionKey? _, ItemRequestOptions options, CancellationToken _) =>
            {
                _replaces.Add((RoundTrip(item), options?.IfMatchEtag));
                if (_replaceFails412.Count > 0 && _replaceFails412.Dequeue())
                    throw new CosmosException("precondition", HttpStatusCode.PreconditionFailed, 0, "a", 1);
                return Response(RoundTrip(item), "etag-after");
            });

        _repo = new AppealRepository(client.Object, "db", _events.Object);
    }

    private static ItemResponse<Appeal> Response(Appeal doc, string etag)
    {
        var r = new Mock<ItemResponse<Appeal>>();
        r.Setup(x => x.Resource).Returns(doc);
        r.Setup(x => x.ETag).Returns(etag);
        return r.Object;
    }

    private static Appeal RoundTrip(Appeal a) =>
        JsonSerializer.Deserialize<Appeal>(JsonSerializer.Serialize(a))!;

    private static Appeal Snapshot() => new()
    {
        TenantId = "t1",
        Id = "a1",
        AppealNumber = "APL-1",
        ClaimId = "c1",
        MemberId = "m1",
        Status = AppealStatus.Submitted,
        LineOfBusiness = LineOfBusiness.Medicare,
        TargetResponseDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    /// <summary>The persisted row after other endpoints wrote to it.</summary>
    private static Appeal PersistedWithConcurrentWrites()
    {
        var a = Snapshot();
        a.Attachments.Add(new AppealAttachment
        {
            AttachmentId = "att-1", ControlNumber = "CN-1",
            Status = AttachmentStatus.Acknowledged, AcknowledgmentReceived = true
        });
        a.AttachmentControlNumbers.Add("CN-1");
        a.Notes.Add(new AppealNote { NoteId = "note-1", NoteText = "enc::n" });
        a.AssignedReviewerId = "reviewer-7";
        a.OverdueAuditEmitted = true;
        a.TargetResponseDate = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);
        a.DeadlineExtension = new AppealDeadlineExtension { EventId = "ext-1", ExtensionDays = 14 };
        return a;
    }

    private static AppealEvent StatusEvent(AppealStatus from, AppealStatus to) => new AppealEvent
    {
        TenantId = "t1", AppealId = "a1", EventId = Guid.NewGuid().ToString(),
        EventType = AppealEventType.AppealStatusChanged, FromStatus = from, ToStatus = to, ActorId = "u"
    }.Queued(Snapshot());

    private static Appeal StaleBeginReview()
    {
        var stale = Snapshot();
        stale.Status = AppealStatus.InReview;
        stale.UpdatedBy = "reviewer-actor";
        return stale;
    }

    [Fact]
    public async Task Replace_Is_Built_From_The_Fresh_Read_And_Pinned_To_Its_ETag()
    {
        _reads.Enqueue((PersistedWithConcurrentWrites(), "etag-1"));

        var result = await _repo.TransitionStatusAsync(
            StaleBeginReview(), StatusEvent(AppealStatus.Submitted, AppealStatus.InReview));

        var (written, ifMatch) = _replaces.Should().ContainSingle().Subject;
        ifMatch.Should().Be("etag-1");
        foreach (var a in new[] { written, result })
        {
            a.Status.Should().Be(AppealStatus.InReview);
            a.UpdatedBy.Should().Be("reviewer-actor");
            a.Attachments.Should().ContainSingle(x => x.AttachmentId == "att-1"
                && x.Status == AttachmentStatus.Acknowledged && x.AcknowledgmentReceived);
            a.AttachmentControlNumbers.Should().Equal("CN-1");
            a.Notes.Should().ContainSingle(n => n.NoteId == "note-1");
            a.AssignedReviewerId.Should().Be("reviewer-7");
            a.OverdueAuditEmitted.Should().BeTrue();
            a.DeadlineExtension!.EventId.Should().Be("ext-1");
            a.TargetResponseDate.Should().Be(new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc));
        }
        _events.Verify(e => e.AppendAsync(It.IsAny<AppealEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Close_Writes_Its_Own_Fields_Onto_The_Fresh_Read()
    {
        var persisted = PersistedWithConcurrentWrites();
        persisted.Status = AppealStatus.InReview;
        _reads.Enqueue((persisted, "etag-1"));

        var now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        var stale = Snapshot();
        stale.Status = AppealStatus.Closed;
        stale.ClosureReasonCode = AppealClosureReasonCode.Approved;
        stale.ClosedAt = now;
        stale.ClosedBy = "closer";
        stale.DecisionDate = now;
        stale.Decision = new AppealDecision { DecisionType = AppealDecisionType.Approved, ApprovedAmount = 10m };

        await _repo.TransitionStatusAsync(stale, StatusEvent(AppealStatus.InReview, AppealStatus.Closed));

        var written = _replaces.Single().Doc;
        written.Status.Should().Be(AppealStatus.Closed);
        written.ClosureReasonCode.Should().Be(AppealClosureReasonCode.Approved);
        written.ClosedAt.Should().Be(now);
        written.ClosedBy.Should().Be("closer");
        written.DecisionDate.Should().Be(now);
        written.Decision!.ApprovedAmount.Should().Be(10m);
        written.Attachments.Should().ContainSingle();
        written.AssignedReviewerId.Should().Be("reviewer-7");
    }

    [Fact]
    public async Task On_412_The_Replace_Is_Rebuilt_From_A_New_Read()
    {
        _reads.Enqueue((Snapshot(), "etag-1"));
        _reads.Enqueue((PersistedWithConcurrentWrites(), "etag-2"));
        _replaceFails412.Enqueue(true);

        await _repo.TransitionStatusAsync(StaleBeginReview(), StatusEvent(AppealStatus.Submitted, AppealStatus.InReview));

        _replaces.Should().HaveCount(2);
        _replaces[0].IfMatch.Should().Be("etag-1");
        _replaces[1].IfMatch.Should().Be("etag-2");
        _replaces[1].Doc.Attachments.Should().ContainSingle(x => x.AttachmentId == "att-1",
            "an attachment that caused the 412 must survive the retry");
        _replaces[1].Doc.Status.Should().Be(AppealStatus.InReview);
        _events.Verify(e => e.AppendAsync(It.IsAny<AppealEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task On_412_A_Status_Change_Seen_By_The_New_Read_Refuses_The_Transition()
    {
        _reads.Enqueue((Snapshot(), "etag-1"));
        var moved = Snapshot();
        moved.Status = AppealStatus.Closed;
        _reads.Enqueue((moved, "etag-2"));
        _replaceFails412.Enqueue(true);

        var act = () => _repo.TransitionStatusAsync(StaleBeginReview(), StatusEvent(AppealStatus.Submitted, AppealStatus.InReview));

        await act.Should().ThrowAsync<InvalidAppealTransitionException>();
        _replaces.Should().ContainSingle();
        _events.Verify(e => e.AppendAsync(It.IsAny<AppealEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Persistent_412_Ends_In_A_Transition_Conflict()
    {
        for (var i = 0; i < 10; i++)
        {
            _reads.Enqueue((Snapshot(), $"etag-{i}"));
            _replaceFails412.Enqueue(true);
        }

        var act = () => _repo.TransitionStatusAsync(StaleBeginReview(), StatusEvent(AppealStatus.Submitted, AppealStatus.InReview));

        await act.Should().ThrowAsync<InvalidAppealTransitionException>();
        _replaces.Count.Should().BeGreaterThan(1).And.BeLessThan(10);
        _events.Verify(e => e.AppendAsync(It.IsAny<AppealEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
