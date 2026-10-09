using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Tests.Fakes;

namespace AppealsService.Tests.Repositories;

/// <summary>
/// Atomicity / race-safety guarantees of the InMemory repository, which
/// mirrors the Cosmos + Mongo repositories' conditional-replace pattern.
/// The production repositories carry the same invariants; this suite
/// documents and guards them in a reproducible form.
/// </summary>
public class AppealAtomicityTests
{
    private static Appeal NewAppeal(AppealStatus status = AppealStatus.Draft) => new()
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
        LineOfBusiness = LineOfBusiness.Commercial,
        AppealType = AppealType.Reconsideration,
        AppealLevel = AppealLevel.FirstLevel,
        Status = status,
        CreatedAt = DateTime.UtcNow
    };

    private static AppealEvent StatusChangeEvent(Appeal a, AppealStatus from, AppealStatus to) => new AppealEvent()
    {
        TenantId = a.TenantId,
        AppealId = a.Id,
        EventId = Guid.NewGuid().ToString(),
        EventType = AppealEventType.AppealStatusChanged,
        FromStatus = from,
        ToStatus = to,
        ActorId = "user1"
    }.Queued(a);

    [Fact]
    public async Task TransitionStatusAsync_ConcurrentRace_OneWinsOneThrows()
    {
        var repo = new InMemoryAppealRepository();
        var appeal = NewAppeal();
        var genesis = new AppealEvent
        {
            TenantId = appeal.TenantId, AppealId = appeal.Id,
            EventId = Guid.NewGuid().ToString(), EventType = AppealEventType.AppealCreated,
            FromStatus = null, ToStatus = AppealStatus.Draft, ActorId = "user1"
        }.Queued(appeal);
        await repo.CreateAsync(appeal, genesis);

        // Two writers concurrently try to transition Draft -> Submitted.
        // The InMemory repo simulates the Cosmos ETag / Mongo conditional-
        // replace semantics: one wins cleanly, the other surfaces
        // InvalidAppealTransitionException.
        var entityA = await repo.GetByIdAsync(appeal.TenantId, appeal.Id);
        var entityB = await repo.GetByIdAsync(appeal.TenantId, appeal.Id);
        entityA!.Status = AppealStatus.Submitted;
        entityB!.Status = AppealStatus.Submitted;

        // Wrap calls in Task.Run so both writers genuinely race — the
        // InMemory fake's lock serializes synchronously, and without
        // Task.Run the second call throws on the caller thread before
        // reaching Task.WhenAll's unwrap.
        var results = await Task.WhenAll(
            InvokeAsync(() => repo.TransitionStatusAsync(
                entityA, StatusChangeEvent(entityA, AppealStatus.Draft, AppealStatus.Submitted))),
            InvokeAsync(() => repo.TransitionStatusAsync(
                entityB, StatusChangeEvent(entityB, AppealStatus.Draft, AppealStatus.Submitted))));

        results.Count(r => r.Success).Should().Be(1);
        results.Count(r => !r.Success).Should().Be(1);
        results.First(r => !r.Success).Exception.Should().BeOfType<InvalidAppealTransitionException>();
    }

    [Fact]
    public async Task TryTransitionToOverdueAsync_IsExactlyOnce_UnderConcurrentReads()
    {
        var repo = new InMemoryAppealRepository();
        var appeal = NewAppeal(AppealStatus.Submitted);
        appeal.TargetResponseDate = DateTime.UtcNow.AddMinutes(-1); // already overdue
        var genesis = new AppealEvent
        {
            TenantId = appeal.TenantId, AppealId = appeal.Id,
            EventId = Guid.NewGuid().ToString(), EventType = AppealEventType.AppealCreated,
            FromStatus = null, ToStatus = AppealStatus.Submitted, ActorId = "user1"
        }.Queued(appeal);
        await repo.CreateAsync(appeal, genesis);

        // Ten concurrent readers all observe overdue at once.
        var events = Enumerable.Range(0, 10).Select(i => new AppealEvent
        {
            TenantId = appeal.TenantId, AppealId = appeal.Id,
            EventId = Guid.NewGuid().ToString(),
            EventType = AppealEventType.AppealOverdueObserved,
            ActorId = "reader-" + i,
            OccurredAt = DateTime.UtcNow
        }.Queued(appeal)).ToList();

        var snapshot = await repo.GetByIdAsync(appeal.TenantId, appeal.Id);
        var tasks = events.Select(e => repo.TryTransitionToOverdueAsync(snapshot!, e)).ToArray();
        var results = await Task.WhenAll(tasks);

        // Exactly one caller persisted the transition (returned non-null).
        // The other nine observed OverdueAuditEmitted=true and returned null.
        results.Count(r => r != null).Should().Be(1);
        results.Count(r => r == null).Should().Be(9);

        // And exactly one AppealOverdueObserved event in the audit trail.
        var history = await repo.ListByAppealAsync(appeal.TenantId, appeal.Id);
        history.Count(e => e.EventType == AppealEventType.AppealOverdueObserved).Should().Be(1);
    }

    [Fact]
    public async Task AppendNoteAsync_FailureInjection_NoteAppendedEvenIfAuditFails()
    {
        // Documents the documented crash-window posture: the entity update
        // is the source of truth; a crash between entity update and audit
        // append can drop the audit row. Same inherited posture as consent
        // and personal-rep. This test asserts the failure mode is bounded —
        // entity mutation survives, audit is missing, caller sees the
        // exception.
        var repo = new InMemoryAppealRepository();
        var appeal = NewAppeal();
        var genesis = new AppealEvent
        {
            TenantId = appeal.TenantId, AppealId = appeal.Id,
            EventId = Guid.NewGuid().ToString(), EventType = AppealEventType.AppealCreated,
            FromStatus = null, ToStatus = AppealStatus.Draft, ActorId = "user1"
        }.Queued(appeal);
        await repo.CreateAsync(appeal, genesis);

        var note = new AppealNote { CreatedBy = "u", NoteText = "enc::note", IsInternal = true };
        var auditEvent = new AppealEvent
        {
            TenantId = appeal.TenantId, AppealId = appeal.Id,
            EventId = Guid.NewGuid().ToString(),
            EventType = AppealEventType.AppealNoteAdded,
            ActorId = "u"
        }.Queued(appeal);

        // The genesis event succeeded already. Fail the NEXT audit append
        // (the note append) and assert the entity still saw the note.
        // This reproduces the documented crash window, not the desired
        // atomic behavior — we test that the failure mode is exactly this
        // and not worse.
        repo.FailAuditAppendOnce();

        Func<Task> act = () => repo.AppendNoteAsync(appeal, note, auditEvent);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("FailAuditAppendOnce"));

        var stored = repo.PeekStored(appeal.TenantId, appeal.Id);
        stored.Should().NotBeNull();
        stored!.Notes.Should().ContainSingle(n => n.NoteId == note.NoteId,
            "the entity mutation committed before the audit-append failure");

        var history = await repo.ListByAppealAsync(appeal.TenantId, appeal.Id);
        history.Should().NotContain(e => e.EventId == auditEvent.EventId,
            "the audit append failed and its event must not appear in history");
    }

    [Fact]
    public async Task TenantScope_GetByIdCrossTenantReturnsNull()
    {
        var repo = new InMemoryAppealRepository();
        var appeal = NewAppeal();
        appeal.TenantId = "tenant-a";
        var genesis = new AppealEvent
        {
            TenantId = appeal.TenantId, AppealId = appeal.Id,
            EventId = Guid.NewGuid().ToString(), EventType = AppealEventType.AppealCreated,
            FromStatus = null, ToStatus = AppealStatus.Draft, ActorId = "user1"
        }.Queued(appeal);
        await repo.CreateAsync(appeal, genesis);

        (await repo.GetByIdAsync("tenant-b", appeal.Id)).Should().BeNull();
        (await repo.GetByIdAsync("tenant-a", appeal.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task TenantScope_SearchCrossTenantReturnsEmpty()
    {
        var repo = new InMemoryAppealRepository();
        var a1 = NewAppeal(); a1.TenantId = "tenant-a";
        var genesis = new AppealEvent
        {
            TenantId = a1.TenantId, AppealId = a1.Id,
            EventId = Guid.NewGuid().ToString(), EventType = AppealEventType.AppealCreated,
            FromStatus = null, ToStatus = AppealStatus.Draft, ActorId = "user1"
        }.Queued(a1);
        await repo.CreateAsync(a1, genesis);

        var results = await repo.SearchAsync("tenant-b", new AppealSearchParams());
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task IdempotentEventAppend_DuplicateEventIdIsIgnored()
    {
        var repo = new InMemoryAppealRepository();
        var appeal = NewAppeal();
        var eventId = Guid.NewGuid().ToString();
        var genesis = new AppealEvent
        {
            TenantId = appeal.TenantId, AppealId = appeal.Id,
            EventId = eventId,
            EventType = AppealEventType.AppealCreated,
            FromStatus = null, ToStatus = AppealStatus.Draft, ActorId = "u"
        }.Queued(appeal);
        await repo.CreateAsync(appeal, genesis);

        // Same EventId replay → no duplicate row.
        await repo.AppendAsync(new AppealEvent
        {
            TenantId = appeal.TenantId, AppealId = appeal.Id,
            EventId = eventId,
            EventType = AppealEventType.AppealCreated,
            FromStatus = null, ToStatus = AppealStatus.Draft, ActorId = "u"
        }.Queued(appeal));

        var history = await repo.ListByAppealAsync(appeal.TenantId, appeal.Id);
        history.Count(e => e.EventId == eventId).Should().Be(1);
    }

    private static async Task<(bool Success, Exception? Exception)> InvokeAsync(Func<Task> call)
    {
        try { await Task.Run(call); return (true, null); }
        catch (Exception ex) { return (false, ex); }
    }

    [Fact]
    public async Task Stale_Transition_After_Extension_Preserves_It_And_Blocks_A_Second_Extension()
    {
        var repo = new InMemoryAppealRepository();
        var appeal = NewAppeal(AppealStatus.Submitted);
        appeal.LineOfBusiness = LineOfBusiness.Medicare;
        var originalTarget = DateTime.UtcNow.AddDays(20);
        appeal.TargetResponseDate = originalTarget;
        await repo.CreateAsync(appeal, new AppealEvent
        {
            TenantId = appeal.TenantId, AppealId = appeal.Id, EventId = Guid.NewGuid().ToString(),
            EventType = AppealEventType.AppealCreated, ToStatus = AppealStatus.Submitted, ActorId = "user1"
        }.Queued(appeal));

        // 1. begin-review reads its snapshot BEFORE the extension.
        var staleSnapshot = (await repo.GetByIdAsync(appeal.TenantId, appeal.Id))!;

        // 2. The extension commits.
        var extended = await repo.TryExtendDeadlineAsync(
            WithExtension(await repo.GetByIdAsync(appeal.TenantId, appeal.Id), originalTarget, "ext-1"),
            justificationNote: null, _ => [], default);
        extended.Should().NotBeNull();

        // 3. begin-review commits its stale snapshot.
        staleSnapshot.Status = AppealStatus.InReview;
        var transitioned = await repo.TransitionStatusAsync(
            staleSnapshot, StatusChangeEvent(staleSnapshot, AppealStatus.Submitted, AppealStatus.InReview));

        transitioned.Status.Should().Be(AppealStatus.InReview);
        transitioned.DeadlineExtension.Should().NotBeNull("a stale snapshot must not erase a committed extension");
        transitioned.TargetResponseDate.Should().Be(originalTarget.AddDays(14));

        var stored = (await repo.GetByIdAsync(appeal.TenantId, appeal.Id))!;
        stored.DeadlineExtension!.EventId.Should().Be("ext-1");
        stored.TargetResponseDate.Should().Be(originalTarget.AddDays(14));

        // 4. A second extension (different request) must still be refused.
        var second = await repo.TryExtendDeadlineAsync(
            WithExtension(stored, stored.TargetResponseDate!.Value, "ext-2"),
            justificationNote: null, _ => [], default);
        second.Should().BeNull();
    }

    [Fact]
    public async Task TryExtendDeadline_Commits_Justification_Note_Atomically_And_Replay_Completes_Audit()
    {
        var repo = new InMemoryAppealRepository();
        var appeal = NewAppeal(AppealStatus.Submitted);
        appeal.LineOfBusiness = LineOfBusiness.Medicaid;
        var target = DateTime.UtcNow.AddDays(20);
        appeal.TargetResponseDate = target;
        await repo.CreateAsync(appeal, new AppealEvent
        {
            TenantId = appeal.TenantId, AppealId = appeal.Id, EventId = Guid.NewGuid().ToString(),
            EventType = AppealEventType.AppealCreated, ToStatus = AppealStatus.Submitted, ActorId = "user1"
        }.Queued(appeal));

        var request = WithExtension(await repo.GetByIdAsync(appeal.TenantId, appeal.Id), target, "ext-1");
        var note = new AppealNote { NoteId = "note-1", NoteText = "enc::why", CreatedBy = "user1" };
        request.DeadlineExtension!.JustificationNoteId = note.NoteId;
        var auditEvents = new[]
        {
            new AppealEvent { TenantId = appeal.TenantId, AppealId = appeal.Id, EventId = "ext-1",
                EventType = AppealEventType.AppealDeadlineExtended, ActorId = "user1" }.Queued(appeal),
            new AppealEvent { TenantId = appeal.TenantId, AppealId = appeal.Id, EventId = "ext-1:justification-note",
                EventType = AppealEventType.AppealNoteAdded, ActorId = "user1" }.Queued(appeal)
        };

        repo.FailAuditAppendOnce();
        Func<Task> first = () => repo.TryExtendDeadlineAsync(request, note, _ => auditEvents, default);
        await first.Should().ThrowAsync<InvalidOperationException>();

        var afterFailure = (await repo.GetByIdAsync(appeal.TenantId, appeal.Id))!;
        afterFailure.DeadlineExtension.Should().NotBeNull();
        afterFailure.Notes.Should().ContainSingle(n => n.NoteId == "note-1",
            "the justification is committed in the same write as the extension");

        var replay = await repo.TryExtendDeadlineAsync(afterFailure, null, _ => auditEvents, default);
        replay.Should().NotBeNull();
        replay!.Notes.Should().ContainSingle();
        repo.SnapshotEvents().Select(e => e.EventId).Should().Contain(new[] { "ext-1", "ext-1:justification-note" });

        await repo.TryExtendDeadlineAsync(afterFailure, null, _ => auditEvents, default);
        repo.SnapshotEvents().Count(e => e.EventId == "ext-1").Should().Be(1, "audit appends are idempotent");
    }

    private static Appeal WithExtension(Appeal? appeal, DateTime previousTarget, string eventId)
    {
        appeal!.TargetResponseDate = previousTarget.AddDays(14);
        appeal.DeadlineExtension = new AppealDeadlineExtension
        {
            Reason = AppealExtensionReason.EnrolleeRequested,
            ExtensionDays = 14,
            PreviousTargetResponseDate = previousTarget,
            NewTargetResponseDate = previousTarget.AddDays(14),
            WrittenNoticeSentAt = DateTime.UtcNow,
            ExtendedBy = "user1",
            RegulatoryBasis = "42 CFR 422.590(f)",
            EventId = eventId
        };
        return appeal;
    }

    [Fact]
    public async Task Stale_Transition_After_PlanNeedsInfo_Extension_Keeps_The_Justification_Note()
    {
        var repo = new InMemoryAppealRepository();
        var appeal = NewAppeal(AppealStatus.Submitted);
        appeal.LineOfBusiness = LineOfBusiness.Medicare;
        var target = DateTime.UtcNow.AddDays(20);
        appeal.TargetResponseDate = target;
        await repo.CreateAsync(appeal, new AppealEvent
        {
            TenantId = appeal.TenantId, AppealId = appeal.Id, EventId = Guid.NewGuid().ToString(),
            EventType = AppealEventType.AppealCreated, ToStatus = AppealStatus.Submitted, ActorId = "user1"
        }.Queued(appeal));

        // begin-review reads its snapshot (no notes) BEFORE the extension.
        var staleSnapshot = (await repo.GetByIdAsync(appeal.TenantId, appeal.Id))!;
        staleSnapshot.Notes.Should().BeEmpty();

        var request = WithExtension(await repo.GetByIdAsync(appeal.TenantId, appeal.Id), target, "ext-1");
        var note = new AppealNote { NoteId = "note-1", NoteText = "enc::why", CreatedBy = "user1", IsInternal = true };
        request.DeadlineExtension!.JustificationNoteId = note.NoteId;
        (await repo.TryExtendDeadlineAsync(request, note, _ => [], default)).Should().NotBeNull();

        staleSnapshot.Status = AppealStatus.InReview;
        var transitioned = await repo.TransitionStatusAsync(
            staleSnapshot, StatusChangeEvent(staleSnapshot, AppealStatus.Submitted, AppealStatus.InReview));

        var stored = (await repo.GetByIdAsync(appeal.TenantId, appeal.Id))!;
        foreach (var a in new[] { transitioned, stored })
        {
            a.Status.Should().Be(AppealStatus.InReview);
            a.DeadlineExtension!.JustificationNoteId.Should().Be("note-1");
            a.Notes.Should().ContainSingle(n => n.NoteId == a.DeadlineExtension.JustificationNoteId,
                "a stale snapshot must not delete the justification the extension references");
        }
    }

    [Fact]
    public async Task Same_EventId_Race_Builds_Audit_Rows_From_The_Persisted_Winner()
    {
        var repo = new InMemoryAppealRepository();
        var appeal = NewAppeal(AppealStatus.Submitted);
        appeal.LineOfBusiness = LineOfBusiness.Medicaid;
        var target = DateTime.UtcNow.AddDays(20);
        appeal.TargetResponseDate = target;
        await repo.CreateAsync(appeal, new AppealEvent
        {
            TenantId = appeal.TenantId, AppealId = appeal.Id, EventId = Guid.NewGuid().ToString(),
            EventType = AppealEventType.AppealCreated, ToStatus = AppealStatus.Submitted, ActorId = "user1"
        }.Queued(appeal));

        // Two overlapping requests with the same EventId, both pre-read
        // before either commits; each proposes its own note id and actor.
        var snapshotA = WithExtension(await repo.GetByIdAsync(appeal.TenantId, appeal.Id), target, "ext-1");
        var snapshotB = WithExtension(await repo.GetByIdAsync(appeal.TenantId, appeal.Id), target, "ext-1");
        var noteA = new AppealNote { NoteId = "note-A", NoteText = "enc::a", CreatedBy = "alice", IsInternal = true };
        var noteB = new AppealNote { NoteId = "note-B", NoteText = "enc::b", CreatedBy = "bob", IsInternal = true };
        snapshotA.DeadlineExtension!.JustificationNoteId = noteA.NoteId;
        snapshotA.DeadlineExtension.ExtendedBy = "alice";
        snapshotB.DeadlineExtension!.JustificationNoteId = noteB.NoteId;
        snapshotB.DeadlineExtension.ExtendedBy = "bob";

        // Mirrors AppealsController.BuildExtensionAuditEvents: rows are a
        // pure function of whatever extension the repository hands back.
        static IReadOnlyList<AppealEvent> Build(Appeal persisted)
        {
            var ext = persisted.DeadlineExtension!;
            return
            [
                new AppealEvent { TenantId = persisted.TenantId, AppealId = persisted.Id, EventId = ext.EventId!,
                    EventType = AppealEventType.AppealDeadlineExtended, ActorId = ext.ExtendedBy }.Queued(persisted),
                new AppealEvent { TenantId = persisted.TenantId, AppealId = persisted.Id,
                    EventId = $"{ext.EventId}:justification-note", EventType = AppealEventType.AppealNoteAdded,
                    ActorId = ext.ExtendedBy,
                    Payload = new System.Text.Json.Nodes.JsonObject { ["noteId"] = ext.JustificationNoteId } }.Queued(persisted)
            ];
        }

        // A wins the commit, then its audit append fails.
        repo.FailAuditAppendOnce();
        Func<Task> winner = () => repo.TryExtendDeadlineAsync(snapshotA, noteA, Build, default);
        await winner.Should().ThrowAsync<InvalidOperationException>();

        // B enters the replay path with its own (discarded) proposal.
        var resultB = await repo.TryExtendDeadlineAsync(snapshotB, noteB, Build, default);

        resultB.Should().NotBeNull();
        resultB!.DeadlineExtension!.JustificationNoteId.Should().Be("note-A");
        resultB.Notes.Should().ContainSingle().Which.NoteId.Should().Be("note-A");

        var events = repo.SnapshotEvents().Where(e => e.AppealId == appeal.Id).ToList();
        events.Should().ContainSingle(e => e.EventId == "ext-1").Which.ActorId.Should().Be("alice");
        var noteEvent = events.Should().ContainSingle(e => e.EventId == "ext-1:justification-note").Subject;
        noteEvent.ActorId.Should().Be("alice");
        noteEvent.Payload!["noteId"]!.GetValue<string>().Should().Be("note-A",
            "audit rows must reference the committed note, never the loser's discarded one");
    }
}
