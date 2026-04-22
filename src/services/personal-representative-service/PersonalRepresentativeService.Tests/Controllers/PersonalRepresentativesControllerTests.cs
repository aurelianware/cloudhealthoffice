using System.Security.Claims;
using PersonalRepresentativeService.Controllers;
using PersonalRepresentativeService.Middleware;
using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Repositories;
using PersonalRepresentativeService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace PersonalRepresentativeService.Tests.Controllers;

public class PersonalRepresentativesControllerTests
{
    private static (PersonalRepresentativesController controller,
                    InMemoryPersonalRepRepository repo,
                    RecordingPersonalRepEventPublisher publisher,
                    ReversiblePersonalRepFieldEncryptor encryptor)
        BuildController(string tenantId = "tenant-a", string user = "alice@tenant.com")
    {
        var repo = new InMemoryPersonalRepRepository();
        var publisher = new RecordingPersonalRepEventPublisher();
        var encryptor = new ReversiblePersonalRepFieldEncryptor();

        var controller = new PersonalRepresentativesController(repo, repo, encryptor, publisher);
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = tenantId;
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, user) }, "test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, repo, publisher, encryptor);
    }

    [Fact]
    public async Task Create_Returns201_PersistsEncrypted_WritesAuditAndKafka()
    {
        var (controller, repo, publisher, _) = BuildController();

        var result = await controller.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.LegalGuardian,
            FirstName = "Alice",
            LastName = "Smith",
            Email = "alice@example.com",
            PhoneNumber = "555-0100",
            RelationshipNotes = "guardian for minor children",
            ProofOfAuthorityDocumentId = "doc-123"
        }, CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var view = created.Value.Should().BeOfType<PersonalRepresentative>().Subject;
        view.Status.Should().Be(PersonalRepStatus.Draft);
        view.FirstName.Should().Be("Alice");

        var persisted = await repo.GetByIdAsync("tenant-a", view.Id);
        persisted.Should().NotBeNull();
        ReversiblePersonalRepFieldEncryptor.LooksEncrypted(persisted!.FirstName).Should().BeTrue();
        ReversiblePersonalRepFieldEncryptor.LooksEncrypted(persisted.LastName).Should().BeTrue();
        ReversiblePersonalRepFieldEncryptor.LooksEncrypted(persisted.Email).Should().BeTrue();
        ReversiblePersonalRepFieldEncryptor.LooksEncrypted(persisted.PhoneNumber).Should().BeTrue();
        ReversiblePersonalRepFieldEncryptor.LooksEncrypted(persisted.RelationshipNotes).Should().BeTrue();
        persisted.ProofOfAuthorityDocumentId.Should().Be("doc-123");

        repo.SnapshotEvents().Should().ContainSingle(e =>
            e.EventType == PersonalRepEventType.PersonalRepCreated &&
            e.FromStatus == null && e.ToStatus == PersonalRepStatus.Draft);

        publisher.StatusCalls.Should().ContainSingle(c =>
            c.FromStatus == null && c.ToStatus == PersonalRepStatus.Draft);
    }

    [Fact]
    public async Task Get_CrossTenant_Returns404_TenantIsolation()
    {
        var (controllerA, repoA, _, _) = BuildController(tenantId: "tenant-a");
        var create = await controllerA.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.Parent,
            FirstName = "Alice"
        }, CancellationToken.None);
        var id = ((PersonalRepresentative)((CreatedAtActionResult)create).Value!).Id;

        var controllerB = new PersonalRepresentativesController(repoA, repoA,
            new ReversiblePersonalRepFieldEncryptor(),
            new RecordingPersonalRepEventPublisher());
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = "tenant-b";
        http.User = new ClaimsPrincipal(new ClaimsIdentity());
        controllerB.ControllerContext = new ControllerContext { HttpContext = http };

        var result = await controllerB.GetRepresentative(id, CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Activate_FromDraft_PersistsActive_AuditAndKafka()
    {
        var (controller, repo, publisher, _) = BuildController();
        var create = await controller.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.LegalGuardian,
            FirstName = "Alice"
        }, CancellationToken.None);
        var id = ((PersonalRepresentative)((CreatedAtActionResult)create).Value!).Id;
        publisher.StatusCalls.Clear();

        var result = await controller.Activate(id, request: null, CancellationToken.None);
        var view = ((OkObjectResult)result).Value.Should().BeOfType<PersonalRepresentative>().Subject;
        view.Status.Should().Be(PersonalRepStatus.Active);
        view.ActivatedBy.Should().NotBeNullOrEmpty();

        repo.SnapshotEvents().Should().Contain(e => e.EventType == PersonalRepEventType.PersonalRepActivated);
        publisher.StatusCalls.Should().ContainSingle(c =>
            c.FromStatus == PersonalRepStatus.Draft && c.ToStatus == PersonalRepStatus.Active);
    }

    [Fact]
    public async Task Revoke_FromActive_PersistsInactive_ThenIdempotent()
    {
        var (controller, repo, publisher, _) = BuildController();
        var create = await controller.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.HealthcarePowerOfAttorney
        }, CancellationToken.None);
        var id = ((PersonalRepresentative)((CreatedAtActionResult)create).Value!).Id;

        await controller.Activate(id, null, CancellationToken.None);
        publisher.StatusCalls.Clear();

        var revoke1 = await controller.Revoke(id,
            new RevokePersonalRepRequest { ReasonCode = PersonalRepInactivationReasonCode.PoaRevoked },
            CancellationToken.None);
        ((OkObjectResult)revoke1).Value.Should().BeOfType<PersonalRepresentative>()
            .Which.Status.Should().Be(PersonalRepStatus.Inactive);

        var revokeEvents1 = repo.SnapshotEvents().Count(e => e.EventType == PersonalRepEventType.PersonalRepInactivated);
        var kafkaCount1 = publisher.StatusCalls.Count;

        var revoke2 = await controller.Revoke(id, null, CancellationToken.None);
        ((OkObjectResult)revoke2).Value.Should().BeOfType<PersonalRepresentative>()
            .Which.Status.Should().Be(PersonalRepStatus.Inactive);

        repo.SnapshotEvents().Count(e => e.EventType == PersonalRepEventType.PersonalRepInactivated).Should().Be(revokeEvents1);
        publisher.StatusCalls.Count.Should().Be(kafkaCount1);
    }

    [Fact]
    public async Task Revoke_FromDraft_Works()
    {
        var (controller, _, _, _) = BuildController();
        var create = await controller.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.Parent
        }, CancellationToken.None);
        var id = ((PersonalRepresentative)((CreatedAtActionResult)create).Value!).Id;

        var revoke = await controller.Revoke(id, null, CancellationToken.None);
        ((OkObjectResult)revoke).Value.Should().BeOfType<PersonalRepresentative>()
            .Which.Status.Should().Be(PersonalRepStatus.Inactive);
    }

    [Fact]
    public async Task GetHistory_ReturnsAuditTrailChronologicalAndTenantScoped()
    {
        var (controller, _, _, _) = BuildController();
        var create = await controller.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.LegalGuardian
        }, CancellationToken.None);
        var id = ((PersonalRepresentative)((CreatedAtActionResult)create).Value!).Id;
        await controller.Activate(id, null, CancellationToken.None);
        await controller.Revoke(id, null, CancellationToken.None);

        var result = await controller.GetHistory(id, CancellationToken.None);
        var items = ((PersonalRepHistoryResponse)((OkObjectResult)result).Value!).Items;

        items.Should().HaveCount(3);
        items.Select(e => e.EventType).Should().ContainInOrder(
            PersonalRepEventType.PersonalRepCreated,
            PersonalRepEventType.PersonalRepActivated,
            PersonalRepEventType.PersonalRepInactivated);
        items.Should().OnlyContain(e => e.TenantId == "tenant-a");
    }

    [Fact]
    public async Task AddAssociation_PersistsPairAndAudit()
    {
        var (controller, repo, publisher, _) = BuildController();
        var create = await controller.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.LegalGuardian,
            FirstName = "Alice"
        }, CancellationToken.None);
        var id = ((PersonalRepresentative)((CreatedAtActionResult)create).Value!).Id;

        var addResult = await controller.AddAssociation(id,
            new AddAssociationRequest { MemberId = "M123" }, CancellationToken.None);
        addResult.Should().BeOfType<CreatedAtActionResult>();

        var rows = repo.SnapshotAssociations();
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(a => a.RepId == id && a.MemberId == "M123");
        rows.Select(a => a.Direction).Should().BeEquivalentTo(new[]
        {
            AssociationDirection.RepToMember, AssociationDirection.MemberToRep
        });
        rows.Select(a => a.PairId).Distinct().Should().HaveCount(1,
            "both rows of a pair share a single PairId");

        repo.SnapshotEvents().Should().Contain(e =>
            e.EventType == PersonalRepEventType.PersonalRepAssociationAdded && e.MemberId == "M123");
        publisher.AssociationCalls.Should().ContainSingle(c =>
            c.EventType == PersonalRepEventType.PersonalRepAssociationAdded && c.MemberId == "M123");
    }

    [Fact]
    public async Task AddAssociation_Duplicate_Returns409()
    {
        var (controller, _, _, _) = BuildController();
        var create = await controller.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.LegalGuardian
        }, CancellationToken.None);
        var id = ((PersonalRepresentative)((CreatedAtActionResult)create).Value!).Id;

        await controller.AddAssociation(id,
            new AddAssociationRequest { MemberId = "M123" }, CancellationToken.None);
        var second = await controller.AddAssociation(id,
            new AddAssociationRequest { MemberId = "M123" }, CancellationToken.None);

        second.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task RemoveAssociation_SoftDeletesPairAndWritesAudit()
    {
        var (controller, repo, publisher, _) = BuildController();
        var create = await controller.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.LegalGuardian
        }, CancellationToken.None);
        var id = ((PersonalRepresentative)((CreatedAtActionResult)create).Value!).Id;
        await controller.AddAssociation(id,
            new AddAssociationRequest { MemberId = "M123" }, CancellationToken.None);

        var result = await controller.RemoveAssociation(id, "M123", null, CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();

        var active = await repo.FindActiveAssociationAsync("tenant-a", id, "M123");
        active.Should().BeNull();

        repo.SnapshotEvents().Should().Contain(e =>
            e.EventType == PersonalRepEventType.PersonalRepAssociationRemoved && e.MemberId == "M123");
        publisher.AssociationCalls.Should().Contain(c =>
            c.EventType == PersonalRepEventType.PersonalRepAssociationRemoved && c.MemberId == "M123");
    }

    /// <summary>
    /// With the 3-state collapse (Inactive is the single terminal), a
    /// Revoke call on an already-expired rep is idempotent: 200 OK, no new
    /// audit event, no new Kafka publish, and the InactivationReasonCode
    /// stays as Expired — the caller's PoaRevoked intent does NOT overwrite
    /// the reason that actually drove the termination. This diverges from
    /// consent-service, which 409s because Expired and Revoked are distinct
    /// terminal statuses there.
    /// </summary>
    [Fact]
    public async Task Revoke_AfterExpired_IsIdempotentAndPreservesInactivationReason()
    {
        var (controller, repo, publisher, _) = BuildController();
        var create = await controller.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.LegalGuardian,
            ExpiresAt = DateTime.UtcNow.AddHours(-1)
        }, CancellationToken.None);
        var id = ((PersonalRepresentative)((CreatedAtActionResult)create).Value!).Id;
        await controller.Activate(id, null, CancellationToken.None);

        // Trigger the read-time expiry projection — flips persisted
        // Status to Inactive with reason Expired.
        _ = await controller.GetRepresentative(id, CancellationToken.None);

        var expiredEvents = repo.SnapshotEvents().Count(e => e.EventType == PersonalRepEventType.PersonalRepExpired);
        var statusCallsBefore = publisher.StatusCalls.Count;

        var result = await controller.Revoke(id,
            new RevokePersonalRepRequest { ReasonCode = PersonalRepInactivationReasonCode.PoaRevoked },
            CancellationToken.None);

        var view = ((OkObjectResult)result).Value.Should().BeOfType<PersonalRepresentative>().Subject;
        view.Status.Should().Be(PersonalRepStatus.Inactive);
        view.InactivationReasonCode.Should().Be(PersonalRepInactivationReasonCode.Expired,
            "idempotent Revoke on an already-expired rep must NOT overwrite the reason code");

        // No new Inactivated audit event — only the original Expired one.
        repo.SnapshotEvents().Count(e => e.EventType == PersonalRepEventType.PersonalRepInactivated).Should().Be(0);
        repo.SnapshotEvents().Count(e => e.EventType == PersonalRepEventType.PersonalRepExpired).Should().Be(expiredEvents);
        publisher.StatusCalls.Count.Should().Be(statusCallsBefore);
    }

    [Fact]
    public async Task ReadTimeExpiry_PersistsTransition_ExactlyOneExpiredEvent()
    {
        var (controller, repo, publisher, _) = BuildController();
        var create = await controller.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.HealthcarePowerOfAttorney,
            ExpiresAt = DateTime.UtcNow.AddHours(-1)
        }, CancellationToken.None);
        var id = ((PersonalRepresentative)((CreatedAtActionResult)create).Value!).Id;
        await controller.Activate(id, null, CancellationToken.None);
        publisher.StatusCalls.Clear();

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            controller.GetRepresentative(id, CancellationToken.None)));

        repo.SnapshotEvents().Count(e => e.EventType == PersonalRepEventType.PersonalRepExpired)
            .Should().Be(1);
        publisher.StatusCalls.Count(c => c.ToStatus == PersonalRepStatus.Inactive).Should().Be(1);
    }

    [Fact]
    public void TenantMiddleware_GetTenantId_Throws_WhenMissing()
    {
        var http = new DefaultHttpContext();
        Action act = () => http.GetTenantId();
        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Repository-level concurrent-writer races surface as
    /// <see cref="InvalidPersonalRepTransitionException"/>. The controller
    /// must translate them into 409 ProblemDetails rather than letting them
    /// escape as 500.
    /// </summary>
    [Fact]
    public async Task Activate_WhenRepoSignalsRace_Returns409()
    {
        var repo = new Mock<IPersonalRepRepository>();
        var events = new Mock<IPersonalRepEventRepository>();
        var publisher = new RecordingPersonalRepEventPublisher();
        var encryptor = new ReversiblePersonalRepFieldEncryptor();

        var rep = new PersonalRepresentative
        {
            TenantId = "tenant-a",
            Id = "r-1",
            Status = PersonalRepStatus.Draft,
            CredentialType = PersonalRepCredentialType.LegalGuardian,
            CreatedAt = DateTime.UtcNow
        };
        repo.Setup(r => r.GetByIdAsync("tenant-a", "r-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(rep);
        repo.Setup(r => r.TransitionStatusAsync(It.IsAny<PersonalRepresentative>(), It.IsAny<PersonalRepEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidPersonalRepTransitionException(
                PersonalRepStatus.Active, PersonalRepStatus.Active));

        var controller = new PersonalRepresentativesController(repo.Object, events.Object, encryptor, publisher);
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = "tenant-a";
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "alice") }, "test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var result = await controller.Activate("r-1", request: null, CancellationToken.None);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value.Should().BeOfType<ProblemDetails>()
            .Which.Extensions["fromStatus"].Should().Be(PersonalRepStatus.Active.ToString());
        publisher.StatusCalls.Should().NotContain(c => c.ToStatus == PersonalRepStatus.Active,
            "publisher must not emit a status-changed event when the persisted transition never happened");
    }
}
