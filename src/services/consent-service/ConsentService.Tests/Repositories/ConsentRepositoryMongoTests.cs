using ConsentService.Models;
using ConsentService.Tests.Fakes;

namespace ConsentService.Tests.Repositories;

/// <summary>
/// Repository contract tests. Exercised against the in-memory fake — the
/// fake implements the same transition-and-append contract as the Cosmos
/// and Mongo repositories. Mongo container-backed integration runs in CI
/// under a separate fixture once operators publish a test mongo instance.
/// </summary>
public class ConsentRepositoryMongoTests
{
    private static Consent NewActiveConsent(string tenantId = "t1", string memberId = "M1")
    {
        return new Consent
        {
            TenantId = tenantId,
            Id = Guid.NewGuid().ToString(),
            MemberId = memberId,
            ConsentType = ConsentType.GeneralAuthorization,
            Status = ConsentStatus.Active,
            GrantedBy = "alice",
            EffectiveAt = DateTime.UtcNow.AddHours(-2),
            ExpiresAt = DateTime.UtcNow.AddHours(-1),
            CreatedAt = DateTime.UtcNow.AddHours(-3)
        };
    }

    private static ConsentEvent NewExpiredEvent(Consent c) => new()
    {
        TenantId = c.TenantId,
        ConsentId = c.Id,
        MemberId = c.MemberId,
        EventId = Guid.NewGuid().ToString(),
        EventType = ConsentEventType.ConsentExpired,
        FromStatus = ConsentStatus.Active,
        ToStatus = ConsentStatus.Expired,
        ActorId = "System",
        OccurredAt = DateTime.UtcNow
    };

    [Fact]
    public async Task TryTransitionToExpired_Concurrent_WritesExactlyOnce()
    {
        var repo = new InMemoryConsentRepository();
        var consent = NewActiveConsent();

        var genesis = new ConsentEvent
        {
            TenantId = consent.TenantId,
            ConsentId = consent.Id,
            MemberId = consent.MemberId,
            EventId = Guid.NewGuid().ToString(),
            EventType = ConsentEventType.ConsentCreated,
            ActorId = "alice"
        };
        await repo.CreateAsync(consent, genesis);

        const int concurrency = 16;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(() => repo.TryTransitionToExpiredAsync(consent, NewExpiredEvent(consent))))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        results.Count(r => r).Should().Be(1, "exactly one caller wins the Active->Expired race");

        repo.SnapshotEvents()
            .Count(e => e.EventType == ConsentEventType.ConsentExpired)
            .Should().Be(1, "exactly one ConsentExpired audit event is persisted");
    }

    [Fact]
    public async Task ListByMember_OrdersByCreatedAtDescending()
    {
        var repo = new InMemoryConsentRepository();
        for (int i = 0; i < 3; i++)
        {
            var c = NewActiveConsent();
            c.CreatedAt = new DateTime(2026, 1, 1 + i, 0, 0, 0, DateTimeKind.Utc);
            c.Status = ConsentStatus.Draft;
            c.ExpiresAt = null;
            await repo.CreateAsync(c, new ConsentEvent
            {
                TenantId = c.TenantId, ConsentId = c.Id, MemberId = c.MemberId,
                EventId = Guid.NewGuid().ToString(),
                EventType = ConsentEventType.ConsentCreated, ActorId = "a"
            });
        }

        var list = await repo.ListByMemberAsync("t1", "M1", activeOnly: false);
        list.Select(c => c.CreatedAt).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task GetById_WrongTenant_ReturnsNull()
    {
        var repo = new InMemoryConsentRepository();
        var consent = NewActiveConsent("tenant-a", "M1");
        await repo.CreateAsync(consent, new ConsentEvent
        {
            TenantId = consent.TenantId, ConsentId = consent.Id, MemberId = consent.MemberId,
            EventId = Guid.NewGuid().ToString(),
            EventType = ConsentEventType.ConsentCreated, ActorId = "a"
        });

        (await repo.GetByIdAsync("tenant-b", "M1", consent.Id)).Should().BeNull();
        (await repo.GetByIdAsync("tenant-a", "M1", consent.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task TransitionStatus_AppendsAuditEvent()
    {
        var repo = new InMemoryConsentRepository();
        var consent = NewActiveConsent();
        consent.Status = ConsentStatus.Draft;
        consent.ExpiresAt = null;
        await repo.CreateAsync(consent, new ConsentEvent
        {
            TenantId = consent.TenantId, ConsentId = consent.Id, MemberId = consent.MemberId,
            EventId = Guid.NewGuid().ToString(),
            EventType = ConsentEventType.ConsentCreated, ActorId = "alice"
        });

        consent.Status = ConsentStatus.Active;
        var auditEvent = new ConsentEvent
        {
            TenantId = consent.TenantId, ConsentId = consent.Id, MemberId = consent.MemberId,
            EventId = Guid.NewGuid().ToString(),
            EventType = ConsentEventType.ConsentActivated,
            FromStatus = ConsentStatus.Draft, ToStatus = ConsentStatus.Active, ActorId = "alice"
        };
        await repo.TransitionStatusAsync(consent, auditEvent);

        var events = await repo.ListByConsentAsync(consent.TenantId, consent.Id);
        events.Should().HaveCount(2);
        events.Last().EventType.Should().Be(ConsentEventType.ConsentActivated);
    }

    [Fact]
    public async Task TransitionStatus_WhenFromStatusMissing_ThrowsArgumentException()
    {
        // The tightened repository contract requires every transition
        // audit event to declare its expected from-status so the Cosmos
        // and Mongo implementations can enforce a write-side precondition.
        // The fake mirrors that contract — a null FromStatus is a caller bug.
        var repo = new InMemoryConsentRepository();
        var consent = NewActiveConsent();
        await repo.CreateAsync(consent, new ConsentEvent
        {
            TenantId = consent.TenantId, ConsentId = consent.Id, MemberId = consent.MemberId,
            EventId = Guid.NewGuid().ToString(),
            EventType = ConsentEventType.ConsentCreated, ActorId = "alice"
        });

        var invalidAuditEvent = new ConsentEvent
        {
            TenantId = consent.TenantId, ConsentId = consent.Id, MemberId = consent.MemberId,
            EventId = Guid.NewGuid().ToString(),
            EventType = ConsentEventType.ConsentRevoked,
            FromStatus = null, ToStatus = ConsentStatus.Revoked, ActorId = "alice"
        };

        var act = async () => await repo.TransitionStatusAsync(consent, invalidAuditEvent);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*FromStatus*");
    }

    [Fact]
    public async Task TransitionStatus_WhenFromStatusMismatchesPersisted_ThrowsInvalidConsentTransition()
    {
        // Simulates the concurrent-writer race at the fake layer: the
        // caller read the record as Draft, but another writer has since
        // persisted Active. The caller's TransitionStatusAsync must NOT
        // silently overwrite — it surfaces the race as an
        // InvalidConsentTransitionException so the controller can return
        // 409, matching the Cosmos (IfMatchEtag) and Mongo
        // (status-filter ReplaceOneAsync) implementations.
        var repo = new InMemoryConsentRepository();
        var consent = NewActiveConsent();
        consent.Status = ConsentStatus.Draft;
        consent.ExpiresAt = null;
        await repo.CreateAsync(consent, new ConsentEvent
        {
            TenantId = consent.TenantId, ConsentId = consent.Id, MemberId = consent.MemberId,
            EventId = Guid.NewGuid().ToString(),
            EventType = ConsentEventType.ConsentCreated, ActorId = "alice"
        });

        // Concurrent writer lands Draft -> Active first.
        var winningWrite = new Consent
        {
            TenantId = consent.TenantId, Id = consent.Id, MemberId = consent.MemberId,
            ConsentType = consent.ConsentType,
            Status = ConsentStatus.Active,
            GrantedBy = consent.GrantedBy, CreatedAt = consent.CreatedAt
        };
        await repo.TransitionStatusAsync(winningWrite, new ConsentEvent
        {
            TenantId = consent.TenantId, ConsentId = consent.Id, MemberId = consent.MemberId,
            EventId = Guid.NewGuid().ToString(),
            EventType = ConsentEventType.ConsentActivated,
            FromStatus = ConsentStatus.Draft, ToStatus = ConsentStatus.Active, ActorId = "winner"
        });

        // Our caller, still holding a stale Draft snapshot, tries to
        // transition Draft -> Revoked. Persisted status is Active now, so
        // the from-status precondition fails.
        consent.Status = ConsentStatus.Revoked;
        var losingAuditEvent = new ConsentEvent
        {
            TenantId = consent.TenantId, ConsentId = consent.Id, MemberId = consent.MemberId,
            EventId = Guid.NewGuid().ToString(),
            EventType = ConsentEventType.ConsentRevoked,
            FromStatus = ConsentStatus.Draft, ToStatus = ConsentStatus.Revoked, ActorId = "loser"
        };

        var act = async () => await repo.TransitionStatusAsync(consent, losingAuditEvent);
        await act.Should().ThrowAsync<InvalidConsentTransitionException>()
            .Where(ex => ex.FromStatus == ConsentStatus.Active &&
                         ex.ToStatus == ConsentStatus.Revoked);

        // No ConsentRevoked audit row appears — the winner's Activated
        // event is the only lifecycle event after creation.
        var events = await repo.ListByConsentAsync(consent.TenantId, consent.Id);
        events.Should().HaveCount(2);
        events.Should().NotContain(e => e.EventType == ConsentEventType.ConsentRevoked);
    }
}
