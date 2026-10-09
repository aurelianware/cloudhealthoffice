using System.Net;
using System.Text.Json;
using AppealsService.Middleware;
using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using AppealsService.Tests.Fakes;
using Microsoft.Azure.Cosmos;

namespace AppealsService.Tests.Outbox;

/// <summary>
/// The Cosmos outbox store against a mocked <see cref="Container"/> (no
/// emulator in CI): what the repository writes and queries, not the
/// server's handling of it. Documents round-trip through the production
/// <see cref="CosmosSystemTextJsonSerializer"/> options.
/// </summary>
public class AppealOutboxCosmosTests
{
    private static readonly JsonSerializerOptions CosmosJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly Mock<Container> _container = new();
    private readonly Mock<IAppealEventSink> _events = new();
    private Appeal _stored;
    private int _etag;
    private readonly List<Appeal> _replaces = new();
    private readonly List<QueryDefinition> _queries = new();
    private readonly AppealRepository _repo;

    public AppealOutboxCosmosTests()
    {
        var database = new Mock<Database>();
        database.Setup(d => d.GetContainer(AppealRepository.AppealsContainerName)).Returns(_container.Object);
        var client = new Mock<CosmosClient>();
        client.Setup(c => c.GetDatabase("db")).Returns(database.Object);

        _stored = NewAppeal();
        _container
            .Setup(c => c.ReadItemAsync<Appeal>(It.IsAny<string>(), It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response(RoundTrip(_stored), $"etag-{_etag}"));
        _container
            .Setup(c => c.ReplaceItemAsync(It.IsAny<Appeal>(), It.IsAny<string>(), It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Appeal item, string _, PartitionKey? _, ItemRequestOptions options, CancellationToken _) =>
            {
                options.IfMatchEtag.Should().Be($"etag-{_etag}", "every outbox write is ETag-pinned");
                _stored = RoundTrip(item);
                _replaces.Add(_stored);
                _etag++;
                return Response(RoundTrip(_stored), $"etag-{_etag}");
            });
        var empty = new Mock<FeedIterator<AppealRepository.OutboxKeyRow>>();
        empty.Setup(i => i.HasMoreResults).Returns(false);
        _container
            .Setup(c => c.GetItemQueryIterator<AppealRepository.OutboxKeyRow>(
                It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Callback((QueryDefinition q, string _, QueryRequestOptions _) => _queries.Add(q))
            .Returns(empty.Object);

        _repo = new AppealRepository(client.Object, "db", _events.Object);
    }

    private static Appeal NewAppeal() => new()
    {
        TenantId = "t1", Id = "a1", AppealNumber = "APL-1", ClaimId = "c1", MemberId = "m1",
        Status = AppealStatus.Submitted, LineOfBusiness = LineOfBusiness.Medicare
    };

    private static ItemResponse<Appeal> Response(Appeal doc, string etag)
    {
        var r = new Mock<ItemResponse<Appeal>>();
        r.Setup(x => x.Resource).Returns(doc);
        r.Setup(x => x.ETag).Returns(etag);
        return r.Object;
    }

    private static Appeal RoundTrip(Appeal a) =>
        JsonSerializer.Deserialize<Appeal>(JsonSerializer.Serialize(a, CosmosJson), CosmosJson)!;

    private static AppealEvent Note(string key) => new AppealEvent
    {
        TenantId = "t1", AppealId = "a1", EventId = key, EventType = AppealEventType.AppealNoteAdded, ActorId = "u"
    }.Queued(NewAppeal());

    [Fact]
    public async Task Change_And_Event_Go_Out_In_One_Replace_With_The_Sweep_Fields()
    {
        await _repo.AppendNoteAsync(NewAppeal(), new AppealNote { NoteText = "enc::n" }, Note("k1"));

        _replaces.Should().ContainSingle();
        var written = _replaces[0];
        written.Notes.Should().ContainSingle();
        written.Outbox.Should().ContainSingle(m => m.IdempotencyKey == "k1" && m.Status == AppealOutboxStatus.Pending);
        written.OutboxPendingCount.Should().Be(1);
        written.OutboxNextDueAt.Should().Be(AppealOutboxIndex.ToEpochMs(written.Outbox![0].CreatedAt));
        written.OutboxOldestPendingAt.Should().Be(written.OutboxNextDueAt);
        JsonSerializer.Serialize(written, CosmosJson).Should().Contain("\"outboxNextDueAt\":");
    }

    [Fact]
    public async Task A_Retry_With_The_Same_Key_Is_A_Replay_And_Writes_Nothing()
    {
        await _repo.AppendNoteAsync(NewAppeal(), new AppealNote { NoteText = "enc::n" }, Note("k1"));
        var replay = await _repo.AppendNoteAsync(NewAppeal(), new AppealNote { NoteText = "enc::n" }, Note("k1"));

        _replaces.Should().ContainSingle("the retry finds its key inside the ETag-pinned read and does not replace");
        replay.Notes.Should().ContainSingle();
    }

    [Fact]
    public async Task Outcome_Updates_Match_The_Row_Id_While_Pending_And_Leased()
    {
        // Two rows sharing an event id (legacy duplicate).
        await _repo.AppendNoteAsync(NewAppeal(), new AppealNote { NoteText = "enc::n" }, Note("k1"));
        var twin = RoundTrip(_stored).Outbox![0];
        twin.Id = "twin";
        _stored.Outbox!.Add(twin);

        var now = DateTime.UtcNow;
        var lease = (await _repo.TryLeaseAsync("t1", "a1", "pod-a", now, now.AddSeconds(30)))!;
        var second = lease.Entries.Single(m => m.Id == "twin");
        second.Status = AppealOutboxStatus.Sent;
        second.CompletedAt = now;

        (await _repo.UpdateMessageAsync("t1", "a1", "pod-b", second)).Should().BeFalse("not the lease holder");
        (await _repo.UpdateMessageAsync("t1", "a1", "pod-a", second)).Should().BeTrue();
        (await _repo.UpdateMessageAsync("t1", "a1", "pod-a", second)).Should().BeFalse("no longer pending");

        _stored.Outbox!.Single(m => m.Id == "twin").Status.Should().Be(AppealOutboxStatus.Sent);
        _stored.Outbox!.Single(m => m.Id != "twin").Status.Should().Be(AppealOutboxStatus.Pending);
        _stored.OutboxPendingCount.Should().Be(1);
    }

    [Fact]
    public async Task Lease_Is_Not_Taken_When_Nothing_Is_Due_And_Backoff_Moves_The_Due_Time()
    {
        await _repo.AppendNoteAsync(NewAppeal(), new AppealNote { NoteText = "enc::n" }, Note("k1"));
        var now = DateTime.UtcNow;
        var lease = (await _repo.TryLeaseAsync("t1", "a1", "pod-a", now, now.AddSeconds(30)))!;
        var entry = lease.Entries.Single();
        entry.Attempts = 1;
        entry.NextAttemptAt = now.AddMinutes(5);
        (await _repo.UpdateMessageAsync("t1", "a1", "pod-a", entry)).Should().BeTrue();
        await _repo.ReleaseLeaseAsync("t1", "a1", "pod-a", Array.Empty<string>());

        _stored.OutboxNextDueAt.Should().Be(AppealOutboxIndex.ToEpochMs(now.AddMinutes(5)),
            "an appeal that is only backing off is not due, so it cannot starve due ones in the sweep");
        _stored.OutboxLeaseUntilMs.Should().BeNull();

        var writes = _replaces.Count;
        (await _repo.TryLeaseAsync("t1", "a1", "pod-a", now.AddMinutes(1), now.AddMinutes(2))).Should().BeNull();
        _replaces.Should().HaveCount(writes, "no lease write when nothing is due");
    }

    [Fact]
    public async Task Sweep_Query_Filters_And_Orders_On_The_Top_Level_Due_Time()
    {
        await _repo.FindDueAsync(DateTime.UtcNow, 50);

        var text = _queries.Single().QueryText;
        text.Should().Contain("c.outboxNextDueAt <= @now")
            .And.Contain("ORDER BY c.outboxNextDueAt")
            .And.Contain("c.outboxLeaseUntilMs < @now")
            .And.NotContain("IN c.outbox", "no array scan");
    }

    [Fact]
    public async Task Sequence_Assignment_Is_Conditional_On_The_Counter()
    {
        await _repo.AppendNoteAsync(NewAppeal(), new AppealNote { NoteText = "enc::n" }, Note("k1"));
        var now = DateTime.UtcNow;
        var entry = (await _repo.TryLeaseAsync("t1", "a1", "pod-a", now, now.AddSeconds(30)))!.Entries.Single();

        (await _repo.RenewLeaseAsync("t1", "a1", "pod-a", now.AddSeconds(30),
            new AppealOutboxSequenceAssignment(entry.Id, 2))).Should().BeFalse("the counter is at 0, not 1");
        (await _repo.RenewLeaseAsync("t1", "a1", "pod-a", now.AddSeconds(30),
            new AppealOutboxSequenceAssignment(entry.Id, 1))).Should().BeTrue();
        _stored.OutboxSequence.Should().Be(1);
        _stored.Outbox!.Single().Sequence.Should().Be(1);
    }
}
