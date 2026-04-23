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

    private static AppealEvent StatusChangeEvent(Appeal a, AppealStatus from, AppealStatus to) => new()
    {
        TenantId = a.TenantId,
        AppealId = a.Id,
        EventId = Guid.NewGuid().ToString(),
        EventType = AppealEventType.AppealStatusChanged,
        FromStatus = from,
        ToStatus = to,
        ActorId = "user1"
    };

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
        };
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
        };
        await repo.CreateAsync(appeal, genesis);

        // Ten concurrent readers all observe overdue at once.
        var events = Enumerable.Range(0, 10).Select(i => new AppealEvent
        {
            TenantId = appeal.TenantId, AppealId = appeal.Id,
            EventId = Guid.NewGuid().ToString(),
            EventType = AppealEventType.AppealOverdueObserved,
            ActorId = "reader-" + i,
            OccurredAt = DateTime.UtcNow
        }).ToList();

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
        };
        await repo.CreateAsync(appeal, genesis);

        var note = new AppealNote { CreatedBy = "u", NoteText = "enc::note", IsInternal = true };
        var auditEvent = new AppealEvent
        {
            TenantId = appeal.TenantId, AppealId = appeal.Id,
            EventId = Guid.NewGuid().ToString(),
            EventType = AppealEventType.AppealNoteAdded,
            ActorId = "u"
        };

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
        };
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
        };
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
        };
        await repo.CreateAsync(appeal, genesis);

        // Same EventId replay → no duplicate row.
        await repo.AppendAsync(new AppealEvent
        {
            TenantId = appeal.TenantId, AppealId = appeal.Id,
            EventId = eventId,
            EventType = AppealEventType.AppealCreated,
            FromStatus = null, ToStatus = AppealStatus.Draft, ActorId = "u"
        });

        var history = await repo.ListByAppealAsync(appeal.TenantId, appeal.Id);
        history.Count(e => e.EventId == eventId).Should().Be(1);
    }

    private static async Task<(bool Success, Exception? Exception)> InvokeAsync(Func<Task> call)
    {
        try { await Task.Run(call); return (true, null); }
        catch (Exception ex) { return (false, ex); }
    }
}
