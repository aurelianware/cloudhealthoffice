using System.Text.Json.Nodes;
using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Tests.Fakes;

namespace PersonalRepresentativeService.Tests.Repositories;

/// <summary>
/// Enumerated-failure-mode coverage for
/// <c>IPersonalRepRepository.AddAssociationPairAsync</c> and
/// <c>RemoveAssociationPairAsync</c>. Each of the four failure modes on
/// <see cref="Repositories.IPersonalRepRepository.AddAssociationPairAsync"/>
/// gets a named test so the "accepted audit gap" (mode 4) is an
/// explicitly tested thing, not a buried caveat.
///
/// These tests exercise the in-memory fake. The fake stages the forward
/// insert first; if the inverse hook throws, it rolls the forward back —
/// matching the Cosmos TransactionalBatch "both or neither" semantics and
/// the Mongo session-transaction abort semantics.
/// </summary>
public class PersonalRepAssociationPairAtomicityTests
{
    private static PersonalRepAssociation Row(
        string tenantId, string pairId, AssociationDirection dir) => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = tenantId,
        PairId = pairId,
        RepId = "R1",
        MemberId = "M1",
        Direction = dir,
        CredentialType = PersonalRepCredentialType.LegalGuardian,
        EffectiveFrom = DateTime.UtcNow
    };

    private static PersonalRepEvent Evt(string tenantId, string repId) => new()
    {
        TenantId = tenantId,
        PersonalRepId = repId,
        EventType = PersonalRepEventType.PersonalRepAssociationAdded,
        ActorId = "alice",
        OccurredAt = DateTime.UtcNow,
        Payload = new JsonObject()
    };

    // ─── MODE 1: Tenant mismatch — no writes, no audit event ─────────────

    [Fact]
    public async Task AddPair_MismatchedTenant_ThrowsAndWritesNothing()
    {
        var repo = new InMemoryPersonalRepRepository();
        var forward = Row("tenant-a", "pair-1", AssociationDirection.RepToMember);
        var inverse = Row("tenant-b", "pair-1", AssociationDirection.MemberToRep);

        var act = async () => await repo.AddAssociationPairAsync(forward, inverse,
            Evt("tenant-a", "R1"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        repo.SnapshotAssociations().Should().BeEmpty();
        repo.SnapshotEvents().Should().BeEmpty();
    }

    // ─── MODE 2: Forward insert fails — neither row, no audit event ──────

    [Fact]
    public async Task AddPair_ForwardInsertFails_TransactionAborts_NoAuditEvent()
    {
        var repo = new InMemoryPersonalRepRepository();
        repo.OnBeforeForwardInsert = _ =>
            throw new InvalidOperationException("simulated forward insert failure");

        var forward = Row("tenant-a", "pair-1", AssociationDirection.RepToMember);
        var inverse = Row("tenant-a", "pair-1", AssociationDirection.MemberToRep);

        var act = async () => await repo.AddAssociationPairAsync(forward, inverse,
            Evt("tenant-a", "R1"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*forward*");

        repo.SnapshotAssociations().Should().BeEmpty();
        repo.SnapshotEvents().Should().BeEmpty();
    }

    // ─── MODE 3: Inverse insert fails — forward rolled back, no audit ────

    [Fact]
    public async Task AddPair_InverseInsertFails_TransactionAborts_NoAuditEvent()
    {
        var repo = new InMemoryPersonalRepRepository();
        repo.OnBeforeInverseInsert = _ =>
            throw new InvalidOperationException("simulated inverse insert failure");

        var forward = Row("tenant-a", "pair-1", AssociationDirection.RepToMember);
        var inverse = Row("tenant-a", "pair-1", AssociationDirection.MemberToRep);

        var act = async () => await repo.AddAssociationPairAsync(forward, inverse,
            Evt("tenant-a", "R1"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*inverse*");

        repo.SnapshotAssociations().Should().BeEmpty(
            "forward insert must be rolled back when the inverse insert fails — pair atomicity invariant");
        repo.SnapshotEvents().Should().BeEmpty();
    }

    // ─── MODE 4: Pair commits, audit append fails — ACCEPTED GAP ─────────
    //
    // This is the compliance-visible case. Pair IS persisted. Audit row
    // is NOT. The repository must log at Error so ops can reconcile, and
    // the exception propagates to the caller. The in-memory fake exposes
    // an AuditAppendFailureCount counter for the assertion; the Cosmos
    // and Mongo implementations are required by
    // IPersonalRepRepository.AddAssociationPairAsync's docstring to log
    // at ILogger.LogError with tenantId, pairId, eventId, correlationId.

    [Fact]
    public async Task AddPair_CommitSucceeds_AuditAppendFails_PairPersists_FailureIsObservable()
    {
        var repo = new InMemoryPersonalRepRepository();
        repo.OnBeforePairAuditAppend = _ =>
            throw new InvalidOperationException("simulated audit append failure");

        var forward = Row("tenant-a", "pair-1", AssociationDirection.RepToMember);
        var inverse = Row("tenant-a", "pair-1", AssociationDirection.MemberToRep);

        var act = async () => await repo.AddAssociationPairAsync(forward, inverse,
            Evt("tenant-a", "R1"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        // Pair IS persisted — this is the accepted gap.
        repo.SnapshotAssociations().Should().HaveCount(2);
        // Audit row is NOT.
        repo.SnapshotEvents().Should().BeEmpty();
        // The gap is observable.
        repo.AuditAppendFailureCount.Should().Be(1);
    }

    // ─── Symmetric coverage for RemoveAssociationPairAsync ───────────────

    [Fact]
    public async Task RemovePair_CommitSucceeds_AuditAppendFails_PairStaysSoftDeleted_FailureIsObservable()
    {
        var repo = new InMemoryPersonalRepRepository();
        var forward = Row("tenant-a", "pair-1", AssociationDirection.RepToMember);
        var inverse = Row("tenant-a", "pair-1", AssociationDirection.MemberToRep);
        await repo.AddAssociationPairAsync(forward, inverse,
            Evt("tenant-a", "R1"), CancellationToken.None);

        repo.OnBeforePairAuditAppend = _ =>
            throw new InvalidOperationException("simulated audit append failure");

        var removeEvent = Evt("tenant-a", "R1");
        removeEvent.EventType = PersonalRepEventType.PersonalRepAssociationRemoved;
        removeEvent.EventId = Guid.NewGuid().ToString();

        var act = async () => await repo.RemoveAssociationPairAsync(
            "tenant-a", "pair-1", "alice", removeEvent, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        var rows = repo.SnapshotAssociations();
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.EffectiveTo != null,
            "pair must remain soft-deleted even when the audit append fails");

        repo.AuditAppendFailureCount.Should().Be(1);
    }

    [Fact]
    public async Task AddPair_HappyPath_BothRowsAndAuditAllPresent()
    {
        var repo = new InMemoryPersonalRepRepository();
        var forward = Row("tenant-a", "pair-1", AssociationDirection.RepToMember);
        var inverse = Row("tenant-a", "pair-1", AssociationDirection.MemberToRep);

        await repo.AddAssociationPairAsync(forward, inverse,
            Evt("tenant-a", "R1"), CancellationToken.None);

        var rows = repo.SnapshotAssociations();
        rows.Should().HaveCount(2);
        rows.Select(r => r.Direction).Should().BeEquivalentTo(new[]
        {
            AssociationDirection.RepToMember, AssociationDirection.MemberToRep
        });
        rows.Select(r => r.PairId).Distinct().Should().ContainSingle();

        repo.SnapshotEvents().Should().ContainSingle(e =>
            e.EventType == PersonalRepEventType.PersonalRepAssociationAdded);
        repo.AuditAppendFailureCount.Should().Be(0);
    }
}
