using System.Security.Claims;
using ConsentService.Controllers;
using ConsentService.Middleware;
using ConsentService.Models;
using ConsentService.Repositories;
using ConsentService.Services;
using ConsentService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ConsentService.Tests.Controllers;

public class ConsentsControllerTests
{
    private static (ConsentsController controller,
                    InMemoryConsentRepository repo,
                    RecordingConsentEventPublisher publisher,
                    ReversibleConsentFieldEncryptor encryptor)
        BuildController(string tenantId = "tenant-a", string user = "alice@tenant.com")
    {
        var repo = new InMemoryConsentRepository();
        var publisher = new RecordingConsentEventPublisher();
        var encryptor = new ReversibleConsentFieldEncryptor();

        var controller = new ConsentsController(repo, repo, encryptor, publisher);
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

        var result = await controller.CreateConsent("M123", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization,
            GrantedBy = "alice",
            Reason = "for continuity of care",
            GrantedToName = "Dr. Smith",
            Purpose = "follow-up appointment"
        }, CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var view = created.Value.Should().BeOfType<Consent>().Subject;
        view.Status.Should().Be(ConsentStatus.Draft);
        view.Reason.Should().Be("for continuity of care");

        // Persisted form is ciphertext.
        var persisted = await repo.GetByIdAsync("tenant-a", "M123", view.Id);
        persisted.Should().NotBeNull();
        ReversibleConsentFieldEncryptor.LooksEncrypted(persisted!.Reason).Should().BeTrue();
        ReversibleConsentFieldEncryptor.LooksEncrypted(persisted.GrantedToName).Should().BeTrue();
        ReversibleConsentFieldEncryptor.LooksEncrypted(persisted.Purpose).Should().BeTrue();

        repo.SnapshotEvents().Should().ContainSingle(e =>
            e.EventType == ConsentEventType.ConsentCreated &&
            e.FromStatus == null && e.ToStatus == ConsentStatus.Draft);

        publisher.Calls.Should().ContainSingle(c =>
            c.FromStatus == null && c.ToStatus == ConsentStatus.Draft);
    }

    [Fact]
    public async Task Get_CrossTenant_Returns404_TenantIsolation()
    {
        var (controllerA, repoA, _, _) = BuildController(tenantId: "tenant-a");
        var create = await controllerA.CreateConsent("M1", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization,
            GrantedBy = "alice"
        }, CancellationToken.None);
        var id = ((Consent)((CreatedAtActionResult)create).Value!).Id;

        // Same repo, different tenant context.
        var controllerB = new ConsentsController(repoA, repoA,
            new ReversibleConsentFieldEncryptor(),
            new RecordingConsentEventPublisher());
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = "tenant-b";
        http.User = new ClaimsPrincipal(new ClaimsIdentity());
        controllerB.ControllerContext = new ControllerContext { HttpContext = http };

        var result = await controllerB.GetConsent("M1", id, CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ListByMember_IsMostRecentFirst()
    {
        var (controller, _, _, _) = BuildController();
        for (int i = 0; i < 3; i++)
        {
            await controller.CreateConsent("M1", new CreateConsentRequest
            {
                ConsentType = ConsentType.GeneralAuthorization,
                GrantedBy = $"alice-{i}"
            }, CancellationToken.None);
            await Task.Delay(5);
        }

        var listResult = await controller.ListByMember("M1", status: null, CancellationToken.None);
        var list = ((ConsentListResponse)((OkObjectResult)listResult).Value!).Items;
        list.Should().HaveCount(3);
        list.Select(c => c.CreatedAt).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Activate_FromDraft_PersistsActive_AuditAndKafka()
    {
        var (controller, repo, publisher, _) = BuildController();
        var create = await controller.CreateConsent("M1", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization, GrantedBy = "alice"
        }, CancellationToken.None);
        var id = ((Consent)((CreatedAtActionResult)create).Value!).Id;
        publisher.Calls.Clear();

        var result = await controller.Activate("M1", id, request: null, CancellationToken.None);
        var view = ((OkObjectResult)result).Value.Should().BeOfType<Consent>().Subject;
        view.Status.Should().Be(ConsentStatus.Active);
        view.ActivatedBy.Should().NotBeNullOrEmpty();

        repo.SnapshotEvents().Should().Contain(e => e.EventType == ConsentEventType.ConsentActivated);
        publisher.Calls.Should().ContainSingle(c =>
            c.FromStatus == ConsentStatus.Draft && c.ToStatus == ConsentStatus.Active);
    }

    [Fact]
    public async Task Revoke_FromActive_PersistsRevoked_ThenIdempotent()
    {
        var (controller, repo, publisher, _) = BuildController();
        var create = await controller.CreateConsent("M1", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization, GrantedBy = "alice"
        }, CancellationToken.None);
        var id = ((Consent)((CreatedAtActionResult)create).Value!).Id;

        await controller.Activate("M1", id, request: null, CancellationToken.None);
        publisher.Calls.Clear();

        var revoke1 = await controller.Revoke("M1", id,
            new RevokeConsentRequest { ReasonCode = ConsentRevocationReasonCode.MemberRequest },
            CancellationToken.None);
        ((OkObjectResult)revoke1).Value.Should().BeOfType<Consent>()
            .Which.Status.Should().Be(ConsentStatus.Revoked);

        var revokeEvents1 = repo.SnapshotEvents().Count(e => e.EventType == ConsentEventType.ConsentRevoked);
        var kafkaCount1 = publisher.Calls.Count;

        // Second call — idempotent 200, no new event, no new Kafka.
        var revoke2 = await controller.Revoke("M1", id, null, CancellationToken.None);
        ((OkObjectResult)revoke2).Value.Should().BeOfType<Consent>()
            .Which.Status.Should().Be(ConsentStatus.Revoked);

        repo.SnapshotEvents().Count(e => e.EventType == ConsentEventType.ConsentRevoked).Should().Be(revokeEvents1);
        publisher.Calls.Count.Should().Be(kafkaCount1);
    }

    [Fact]
    public async Task Revoke_FromDraft_Works()
    {
        var (controller, _, _, _) = BuildController();
        var create = await controller.CreateConsent("M1", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization, GrantedBy = "alice"
        }, CancellationToken.None);
        var id = ((Consent)((CreatedAtActionResult)create).Value!).Id;

        var revoke = await controller.Revoke("M1", id, null, CancellationToken.None);
        ((OkObjectResult)revoke).Value.Should().BeOfType<Consent>()
            .Which.Status.Should().Be(ConsentStatus.Revoked);
    }

    [Fact]
    public async Task Revoke_AfterExpired_Returns409()
    {
        var (controller, repo, _, _) = BuildController();
        var create = await controller.CreateConsent("M1", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization, GrantedBy = "alice",
            ExpiresAt = DateTime.UtcNow.AddHours(-1)
        }, CancellationToken.None);
        var id = ((Consent)((CreatedAtActionResult)create).Value!).Id;
        await controller.Activate("M1", id, null, CancellationToken.None);

        // Trigger the read-time expiry projection — TryTransitionToExpiredAsync
        // flips the persisted status.
        _ = await controller.GetConsent("M1", id, CancellationToken.None);

        var result = await controller.Revoke("M1", id, null, CancellationToken.None);
        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        var problem = conflict.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["fromStatus"].Should().Be(ConsentStatus.Expired.ToString());
        problem.Extensions["toStatus"].Should().Be(ConsentStatus.Revoked.ToString());
    }

    [Fact]
    public async Task GetHistory_ReturnsAuditTrailChronologicalAndTenantScoped()
    {
        var (controller, _, _, _) = BuildController();
        var create = await controller.CreateConsent("M1", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization, GrantedBy = "alice"
        }, CancellationToken.None);
        var id = ((Consent)((CreatedAtActionResult)create).Value!).Id;
        await controller.Activate("M1", id, null, CancellationToken.None);
        await controller.Revoke("M1", id, null, CancellationToken.None);

        var result = await controller.GetHistory("M1", id, CancellationToken.None);
        var items = ((ConsentHistoryResponse)((OkObjectResult)result).Value!).Items;

        items.Should().HaveCount(3);
        items.Select(e => e.EventType).Should().ContainInOrder(
            ConsentEventType.ConsentCreated,
            ConsentEventType.ConsentActivated,
            ConsentEventType.ConsentRevoked);
        items.Should().OnlyContain(e => e.TenantId == "tenant-a");
    }

    [Fact]
    public async Task ReadTimeExpiry_PersistsTransition_ExactlyOneExpiredEvent()
    {
        var (controller, repo, publisher, _) = BuildController();
        var create = await controller.CreateConsent("M1", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization, GrantedBy = "alice",
            ExpiresAt = DateTime.UtcNow.AddHours(-1)
        }, CancellationToken.None);
        var id = ((Consent)((CreatedAtActionResult)create).Value!).Id;
        await controller.Activate("M1", id, null, CancellationToken.None);
        publisher.Calls.Clear();

        // Three concurrent reads race to observe the expired state.
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            controller.GetConsent("M1", id, CancellationToken.None)));

        repo.SnapshotEvents().Count(e => e.EventType == ConsentEventType.ConsentExpired)
            .Should().Be(1);
        publisher.Calls.Count(c => c.ToStatus == ConsentStatus.Expired).Should().Be(1);
    }

    [Fact]
    public void TenantMiddleware_GetTenantId_Throws_WhenMissing()
    {
        var http = new DefaultHttpContext();
        Action act = () => http.GetTenantId();
        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Repository-level concurrent-writer races (Cosmos 412 PreconditionFailed
    /// from the IfMatchEtag check, Mongo ReplaceOneAsync MatchedCount == 0)
    /// surface as <see cref="InvalidConsentTransitionException"/>. The
    /// controller must translate them into 409 ProblemDetails rather than
    /// letting them escape as 500 — the same shape used for state-machine
    /// rejections so clients see one uniform conflict surface.
    /// </summary>
    [Fact]
    public async Task Activate_WhenRepoSignalsRace_Returns409()
    {
        var repo = new Mock<IConsentRepository>();
        var events = new Mock<IConsentEventRepository>();
        var publisher = new RecordingConsentEventPublisher();
        var encryptor = new ReversibleConsentFieldEncryptor();

        var consent = new Consent
        {
            TenantId = "tenant-a",
            Id = "c-1",
            MemberId = "M1",
            ConsentType = ConsentType.GeneralAuthorization,
            Status = ConsentStatus.Draft,
            GrantedBy = "alice",
            CreatedAt = DateTime.UtcNow
        };
        repo.Setup(r => r.GetByIdAsync("tenant-a", "M1", "c-1")).ReturnsAsync(consent);
        repo.Setup(r => r.TransitionStatusAsync(It.IsAny<Consent>(), It.IsAny<ConsentEvent>()))
            .ThrowsAsync(new InvalidConsentTransitionException(
                ConsentStatus.Active, ConsentStatus.Active));

        var controller = new ConsentsController(repo.Object, events.Object, encryptor, publisher);
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = "tenant-a";
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "alice") }, "test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var result = await controller.Activate("M1", "c-1", request: null, CancellationToken.None);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value.Should().BeOfType<ProblemDetails>()
            .Which.Extensions["fromStatus"].Should().Be(ConsentStatus.Active.ToString());
        publisher.Calls.Should().NotContain(c => c.ToStatus == ConsentStatus.Active,
            "publisher must not emit a status-changed event when the persisted transition never happened");
    }
}
