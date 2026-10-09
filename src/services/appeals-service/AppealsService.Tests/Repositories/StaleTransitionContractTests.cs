using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Tests.Fakes;
using CloudHealthOffice.Testing.Mongo;

namespace AppealsService.Tests.Repositories;

/// <summary>
/// A status transition is built from a snapshot the controller read
/// earlier. Every other endpoint that writes the appeal can commit between
/// that read and the transition write; the transition must keep what it
/// wrote. One scenario per concurrent writer in <see cref="IAppealRepository"/>.
/// Runs against the in-memory fake and a real mongod (EphemeralMongo). The
/// Cosmos repository is covered against a mocked container in
/// <see cref="AppealRepositoryCosmosTransitionTests"/>.
/// </summary>
public abstract class StaleTransitionContractTests
{
    protected abstract Task<IAppealRepository> CreateRepositoryAsync();

    private static Appeal NewAppeal(AppealStatus status) => new()
    {
        TenantId = "t1",
        Id = Guid.NewGuid().ToString(),
        AppealNumber = "APL-" + Guid.NewGuid().ToString("N")[..6],
        ClaimId = "c1",
        ClaimNumber = "CLM-001",
        MemberId = "m1",
        PatientName = "enc::patient",
        ProviderNPI = "1234567890",
        AppealReason = "enc::reason",
        LineOfBusiness = LineOfBusiness.Medicare,
        AppealType = AppealType.Reconsideration,
        AppealLevel = AppealLevel.FirstLevel,
        Status = status,
        // Whole milliseconds: BSON dates drop sub-millisecond ticks.
        TargetResponseDate = TruncateToMs(DateTime.UtcNow.AddDays(20)),
        CreatedAt = TruncateToMs(DateTime.UtcNow)
    };

    private static DateTime TruncateToMs(DateTime d) =>
        new(d.Ticks - d.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    private static AppealEvent Event(Appeal a, AppealEventType type, AppealStatus? from = null, AppealStatus? to = null) => new()
    {
        TenantId = a.TenantId,
        AppealId = a.Id,
        EventId = Guid.NewGuid().ToString(),
        EventType = type,
        FromStatus = from,
        ToStatus = to,
        ActorId = "user1"
    };

    private static AppealAttachment Attachment(string id, string? controlNumber) => new()
    {
        AttachmentId = id,
        ControlNumber = controlNumber,
        AttachmentTypeCode = "B4",
        FileName = id + ".pdf",
        Description = "enc::desc",
        UploadedAt = TruncateToMs(DateTime.UtcNow)
    };

    private async Task<(IAppealRepository Repo, Appeal Appeal)> SeedAsync(
        AppealStatus status, Action<Appeal>? configure = null)
    {
        var repo = await CreateRepositoryAsync();
        var appeal = NewAppeal(status);
        configure?.Invoke(appeal);
        await repo.CreateAsync(appeal, Event(appeal, AppealEventType.AppealCreated, to: status));
        return (repo, appeal);
    }

    /// <summary>
    /// Reads a snapshot, lets <paramref name="concurrentWrite"/> commit, then
    /// commits the stale snapshot as <paramref name="from"/> → InReview and
    /// returns (transition result, stored row).
    /// </summary>
    private static async Task<(Appeal Returned, Appeal Stored)> StaleBeginReviewAsync(
        IAppealRepository repo, Appeal appeal, Func<Task> concurrentWrite,
        AppealStatus from = AppealStatus.Submitted)
    {
        var stale = (await repo.GetByIdAsync(appeal.TenantId, appeal.Id))!;
        await concurrentWrite();

        stale.Status = AppealStatus.InReview;
        stale.UpdatedBy = "reviewer-actor";
        var returned = await repo.TransitionStatusAsync(
            stale, Event(stale, AppealEventType.AppealStatusChanged, from, AppealStatus.InReview));
        var stored = (await repo.GetByIdAsync(appeal.TenantId, appeal.Id))!;

        foreach (var a in new[] { returned, stored })
        {
            a.Status.Should().Be(AppealStatus.InReview);
            a.UpdatedBy.Should().Be("reviewer-actor");
        }
        return (returned, stored);
    }

    [Fact]
    public async Task Stale_Transition_Keeps_An_Attachment_Appended_After_The_Snapshot()
    {
        var (repo, appeal) = await SeedAsync(AppealStatus.Submitted);

        var (returned, stored) = await StaleBeginReviewAsync(repo, appeal, () =>
            repo.AppendAttachmentAsync(appeal, Attachment("att-1", "CN-1"),
                Event(appeal, AppealEventType.AppealAttachmentAdded)));

        foreach (var a in new[] { returned, stored })
        {
            a.Attachments.Should().ContainSingle(x => x.AttachmentId == "att-1",
                "a stale snapshot must not drop an attachment added after it was read");
            a.AttachmentControlNumbers.Should().Equal("CN-1");
        }
        (await repo.GetAttachmentByIdAsync(appeal.TenantId, "att-1")).Should().NotBeNull();
    }

    [Fact]
    public async Task Stale_Transition_Keeps_An_Attachment_Acknowledgment_Made_After_The_Snapshot()
    {
        var (repo, appeal) = await SeedAsync(AppealStatus.Submitted,
            a =>
            {
                a.Attachments.Add(Attachment("att-1", "CN-1"));
                a.AttachmentControlNumbers.Add("CN-1");
            });

        var (returned, stored) = await StaleBeginReviewAsync(repo, appeal, () =>
            repo.AcknowledgeAttachmentAsync(appeal.TenantId, appeal.Id, "att-1", acknowledgmentReceived: true,
                Event(appeal, AppealEventType.AppealAttachmentAcknowledged)));

        foreach (var a in new[] { returned, stored })
        {
            var att = a.Attachments.Should().ContainSingle().Subject;
            att.Status.Should().Be(AttachmentStatus.Acknowledged,
                "a stale snapshot must not revert the 999/TA1 acknowledgment");
            att.AcknowledgmentReceived.Should().BeTrue();
            att.SentDate.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Stale_Transition_Keeps_A_Note_Appended_After_The_Snapshot()
    {
        var (repo, appeal) = await SeedAsync(AppealStatus.Submitted);

        var (returned, stored) = await StaleBeginReviewAsync(repo, appeal, () =>
            repo.AppendNoteAsync(appeal,
                new AppealNote { NoteId = "note-1", NoteText = "enc::n", CreatedBy = "user1" },
                Event(appeal, AppealEventType.AppealNoteAdded)));

        foreach (var a in new[] { returned, stored })
            a.Notes.Should().ContainSingle(n => n.NoteId == "note-1");
    }

    [Fact]
    public async Task Stale_Transition_Keeps_A_Reviewer_Assigned_After_The_Snapshot()
    {
        var (repo, appeal) = await SeedAsync(AppealStatus.Submitted);

        var (returned, stored) = await StaleBeginReviewAsync(repo, appeal, async () =>
        {
            var fresh = (await repo.GetByIdAsync(appeal.TenantId, appeal.Id))!;
            fresh.AssignedReviewerId = "reviewer-7";
            await repo.AssignReviewerAsync(fresh, Event(appeal, AppealEventType.AppealAssigned));
        });

        foreach (var a in new[] { returned, stored })
            a.AssignedReviewerId.Should().Be("reviewer-7",
                "a stale snapshot must not undo a reviewer assignment");
    }

    [Fact]
    public async Task Stale_Transition_Keeps_The_Overdue_Audit_Flag_Set_After_The_Snapshot()
    {
        var (repo, appeal) = await SeedAsync(AppealStatus.Submitted,
            a => a.TargetResponseDate = TruncateToMs(DateTime.UtcNow.AddDays(-1)));

        var (returned, stored) = await StaleBeginReviewAsync(repo, appeal, async () =>
        {
            var fresh = (await repo.GetByIdAsync(appeal.TenantId, appeal.Id))!;
            (await repo.TryTransitionToOverdueAsync(fresh, Event(appeal, AppealEventType.AppealOverdueObserved)))
                .Should().NotBeNull();
        });

        foreach (var a in new[] { returned, stored })
            a.OverdueAuditEmitted.Should().BeTrue(
                "resetting the flag would emit a second AppealOverdueObserved event");

        var again = await repo.TryTransitionToOverdueAsync(stored, Event(appeal, AppealEventType.AppealOverdueObserved));
        again.Should().BeNull("the overdue observation is one-shot");
    }

    [Fact]
    public async Task Stale_Transition_Keeps_An_Extension_And_Its_Note_And_Still_Refuses_A_Second_Extension()
    {
        var (repo, appeal) = await SeedAsync(AppealStatus.Submitted);
        var originalTarget = appeal.TargetResponseDate!.Value;

        var (returned, stored) = await StaleBeginReviewAsync(repo, appeal, async () =>
        {
            var request = WithExtension((await repo.GetByIdAsync(appeal.TenantId, appeal.Id))!, originalTarget, "ext-1");
            var note = new AppealNote { NoteId = "note-ext", NoteText = "enc::why", CreatedBy = "user1" };
            request.DeadlineExtension!.JustificationNoteId = note.NoteId;
            (await repo.TryExtendDeadlineAsync(request, note, _ => [], default)).Should().NotBeNull();
        });

        foreach (var a in new[] { returned, stored })
        {
            a.DeadlineExtension!.EventId.Should().Be("ext-1");
            a.TargetResponseDate.Should().Be(originalTarget.AddDays(14));
            a.Notes.Should().ContainSingle(n => n.NoteId == "note-ext");
        }

        var second = await repo.TryExtendDeadlineAsync(
            WithExtension(stored, stored.TargetResponseDate!.Value, "ext-2"), null, _ => [], default);
        second.Should().BeNull("an appeal can be extended once");
    }

    [Fact]
    public async Task Stale_Close_Writes_The_Decision_And_Keeps_Every_Concurrent_Write()
    {
        var (repo, appeal) = await SeedAsync(AppealStatus.InReview,
            a => a.Attachments.Add(Attachment("att-0", null)));

        var stale = (await repo.GetByIdAsync(appeal.TenantId, appeal.Id))!;

        await repo.AppendAttachmentAsync(appeal, Attachment("att-1", "CN-1"),
            Event(appeal, AppealEventType.AppealAttachmentAdded));
        await repo.AcknowledgeAttachmentAsync(appeal.TenantId, appeal.Id, "att-0", true,
            Event(appeal, AppealEventType.AppealAttachmentAcknowledged));
        await repo.AppendNoteAsync(appeal,
            new AppealNote { NoteId = "note-1", NoteText = "enc::n", CreatedBy = "user1" },
            Event(appeal, AppealEventType.AppealNoteAdded));
        var assign = (await repo.GetByIdAsync(appeal.TenantId, appeal.Id))!;
        assign.AssignedReviewerId = "reviewer-7";
        await repo.AssignReviewerAsync(assign, Event(appeal, AppealEventType.AppealAssigned));

        var now = TruncateToMs(DateTime.UtcNow);
        stale.Status = AppealStatus.Closed;
        stale.ClosureReasonCode = AppealClosureReasonCode.Approved;
        stale.ClosedAt = now;
        stale.ClosedBy = "closer";
        stale.UpdatedBy = "closer";
        stale.DecisionDate = now;
        stale.Decision = new AppealDecision
        {
            DecisionType = AppealDecisionType.Approved,
            ApprovedAmount = 125m,
            DecisionReason = "enc::reason",
            DecisionMaker = "closer",
            DecisionDate = now
        };

        var returned = await repo.TransitionStatusAsync(
            stale, Event(stale, AppealEventType.AppealClosed, AppealStatus.InReview, AppealStatus.Closed));
        var stored = (await repo.GetByIdAsync(appeal.TenantId, appeal.Id))!;

        foreach (var a in new[] { returned, stored })
        {
            a.Status.Should().Be(AppealStatus.Closed);
            a.ClosureReasonCode.Should().Be(AppealClosureReasonCode.Approved);
            a.ClosedAt.Should().Be(now);
            a.ClosedBy.Should().Be("closer");
            a.UpdatedBy.Should().Be("closer");
            a.DecisionDate.Should().Be(now);
            a.Decision!.DecisionType.Should().Be(AppealDecisionType.Approved);
            a.Decision.ApprovedAmount.Should().Be(125m);
            a.Decision.DecisionReason.Should().Be("enc::reason");

            a.Attachments.Select(x => x.AttachmentId).Should().BeEquivalentTo(new[] { "att-0", "att-1" });
            a.Attachments.Single(x => x.AttachmentId == "att-0").Status.Should().Be(AttachmentStatus.Acknowledged);
            a.AttachmentControlNumbers.Should().Equal("CN-1");
            a.Notes.Should().ContainSingle(n => n.NoteId == "note-1");
            a.AssignedReviewerId.Should().Be("reviewer-7");
        }
    }

    [Fact]
    public async Task Stale_Transition_Is_Refused_When_The_Status_Moved_After_The_Snapshot()
    {
        var (repo, appeal) = await SeedAsync(AppealStatus.Submitted);
        var stale = (await repo.GetByIdAsync(appeal.TenantId, appeal.Id))!;

        var winner = (await repo.GetByIdAsync(appeal.TenantId, appeal.Id))!;
        winner.Status = AppealStatus.InReview;
        await repo.TransitionStatusAsync(winner,
            Event(winner, AppealEventType.AppealStatusChanged, AppealStatus.Submitted, AppealStatus.InReview));

        stale.Status = AppealStatus.Closed;
        stale.ClosureReasonCode = AppealClosureReasonCode.Withdrawn;
        var act = () => repo.TransitionStatusAsync(stale,
            Event(stale, AppealEventType.AppealClosed, AppealStatus.Submitted, AppealStatus.Closed));
        await act.Should().ThrowAsync<InvalidAppealTransitionException>();

        var stored = (await repo.GetByIdAsync(appeal.TenantId, appeal.Id))!;
        stored.Status.Should().Be(AppealStatus.InReview);
        stored.ClosureReasonCode.Should().BeNull();
    }

    private static Appeal WithExtension(Appeal appeal, DateTime previousTarget, string eventId)
    {
        appeal.TargetResponseDate = previousTarget.AddDays(14);
        appeal.DeadlineExtension = new AppealDeadlineExtension
        {
            Reason = AppealExtensionReason.PlanNeedsInfo,
            ExtensionDays = 14,
            PreviousTargetResponseDate = previousTarget,
            NewTargetResponseDate = previousTarget.AddDays(14),
            WrittenNoticeSentAt = TruncateToMs(DateTime.UtcNow),
            ExtendedAt = TruncateToMs(DateTime.UtcNow),
            ExtendedBy = "user1",
            RegulatoryBasis = "42 CFR 422.590(f)",
            EventId = eventId
        };
        return appeal;
    }
}

public sealed class InMemoryStaleTransitionTests : StaleTransitionContractTests
{
    protected override Task<IAppealRepository> CreateRepositoryAsync() =>
        Task.FromResult<IAppealRepository>(new InMemoryAppealRepository());
}

[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MongoStaleTransitionTests : StaleTransitionContractTests, IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private readonly List<MongoDB.Driver.IMongoDatabase> _databases = new();

    public MongoStaleTransitionTests(MongoRunnerFixture mongo) => _mongo = mongo;

    protected override Task<IAppealRepository> CreateRepositoryAsync()
    {
        var db = _mongo.CreateDatabase("appeals_stale");
        _databases.Add(db);
        return Task.FromResult<IAppealRepository>(
            new AppealRepositoryMongo(db, new InMemoryAppealRepository()));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var db in _databases) await _mongo.DropDatabaseAsync(db);
    }
}
