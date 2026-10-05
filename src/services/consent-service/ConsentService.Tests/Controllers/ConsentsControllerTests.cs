using System.Security.Claims;
using ConsentService.Controllers;
using ConsentService.Middleware;
using ConsentService.Models;
using ConsentService.Repositories;
using ConsentService.Services;
using ConsentService.Tests.Fakes;
using CloudHealthOffice.Infrastructure.Security;
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

        var http = NewHttpContext(tenantId, user);
        var controller = new ConsentsController(repo, repo, encryptor, publisher, ActorFor(http));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, repo, publisher, encryptor);
    }

    /// <summary>
    /// The shape the shared authentication leaves behind: the tenant in
    /// HttpContext.Items and the token subject as the "sub" claim.
    /// </summary>
    private static DefaultHttpContext NewHttpContext(string tenantId, string? subject)
    {
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = tenantId;
        http.User = subject is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ChoClaimTypes.Subject, subject) }, "test"));
        return http;
    }

    private static ICurrentActor ActorFor(HttpContext http)
        => new HttpContextCurrentActor(new HttpContextAccessor { HttpContext = http });

    [Fact]
    public async Task Create_RecordsRecordedByAndAuditActor_FromToken_GrantedByFromRequest()
    {
        var (controller, repo, publisher, _) = BuildController(user: "token-user-7");

        var result = await controller.CreateConsent("M1", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization,
            GrantorType = ConsentGrantorType.Member,
            GrantedBy = "M1"
        }, CancellationToken.None);

        var view = ((CreatedAtActionResult)result).Value.Should().BeOfType<Consent>().Subject;
        view.RecordedBy.Should().Be("token-user-7");
        view.GrantedBy.Should().Be("M1");
        view.GrantorType.Should().Be(ConsentGrantorType.Member);
        var stored = (await repo.GetByIdAsync("tenant-a", "M1", view.Id))!;
        stored.RecordedBy.Should().Be("token-user-7");
        stored.GrantedBy.Should().Be("M1");
        stored.GrantorType.Should().Be(ConsentGrantorType.Member);
        repo.SnapshotEvents().Should().ContainSingle()
            .Which.ActorId.Should().Be("token-user-7");
        publisher.Calls.Should().ContainSingle().Which.Actor.Should().Be("token-user-7");
    }

    [Theory]
    [InlineData("M2")]
    [InlineData("token-user-7")]
    [InlineData("m1")]
    public async Task Create_MemberGrantor_NotMatchingMember_Returns400_AndWritesNothing(string grantedBy)
    {
        var (controller, repo, publisher, _) = BuildController(user: "token-user-7");

        var result = await controller.CreateConsent("M1", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization,
            GrantorType = ConsentGrantorType.Member,
            GrantedBy = grantedBy
        }, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().BeOfType<ValidationProblemDetails>()
            .Which.Errors.Should().ContainKey(nameof(CreateConsentRequest.GrantedBy));
        (await repo.ListByMemberAsync("tenant-a", "M1", activeOnly: false)).Should().BeEmpty();
        repo.SnapshotEvents().Should().BeEmpty();
        publisher.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_PersonalRepresentativeGrantor_IsAcceptedAsNamed()
    {
        var (controller, repo, _, _) = BuildController(user: "token-user-7");

        var result = await controller.CreateConsent("M1", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization,
            GrantorType = ConsentGrantorType.PersonalRepresentative,
            GrantedBy = "rep-guardian-9"
        }, CancellationToken.None);

        var view = result.Should().BeOfType<CreatedAtActionResult>().Subject.Value.Should().BeOfType<Consent>().Subject;
        var stored = (await repo.GetByIdAsync("tenant-a", "M1", view.Id))!;
        stored.GrantorType.Should().Be(ConsentGrantorType.PersonalRepresentative);
        stored.GrantedBy.Should().Be("rep-guardian-9");
        stored.RecordedBy.Should().Be("token-user-7");
    }

    [Fact]
    public async Task Create_WithoutGrantorType_Returns400_AndWritesNothing()
    {
        var (controller, repo, publisher, _) = BuildController();

        var result = await controller.CreateConsent("M1", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization,
            GrantedBy = "M1"
        }, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().BeOfType<ValidationProblemDetails>()
            .Which.Errors.Should().ContainKey(nameof(CreateConsentRequest.GrantorType));
        repo.SnapshotEvents().Should().BeEmpty();
        publisher.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithUndefinedGrantorType_Returns400()
    {
        var (controller, repo, _, _) = BuildController();

        var result = await controller.CreateConsent("M1", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization,
            GrantorType = (ConsentGrantorType)42,
            GrantedBy = "M1"
        }, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        repo.SnapshotEvents().Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithoutGrantedBy_Returns400()
    {
        var (controller, repo, _, _) = BuildController();

        var result = await controller.CreateConsent("M1", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization,
            GrantorType = ConsentGrantorType.PersonalRepresentative
        }, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().BeOfType<ValidationProblemDetails>()
            .Which.Errors.Should().ContainKey(nameof(CreateConsentRequest.GrantedBy));
        repo.SnapshotEvents().Should().BeEmpty();
    }

    [Fact]
    public async Task Activate_And_Revoke_DoNotChangeGrantorOrRecorder()
    {
        var (creator, repo, publisher, encryptor) = BuildController(user: "recorder-1");
        var create = await creator.CreateConsent("M1", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization,
            GrantorType = ConsentGrantorType.Member,
            GrantedBy = "M1"
        }, CancellationToken.None);
        var id = ((Consent)((CreatedAtActionResult)create).Value!).Id;

        var http = NewHttpContext("tenant-a", "other-staff-2");
        var other = new ConsentsController(repo, repo, encryptor, publisher, ActorFor(http))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        await other.Activate("M1", id, null, CancellationToken.None);
        var revoked = (Consent)((OkObjectResult)await other.Revoke("M1", id, null, CancellationToken.None)).Value!;

        revoked.GrantedBy.Should().Be("M1");
        revoked.GrantorType.Should().Be(ConsentGrantorType.Member);
        revoked.RecordedBy.Should().Be("recorder-1");
        revoked.ActivatedBy.Should().Be("other-staff-2");
        revoked.RevokedBy.Should().Be("other-staff-2");
    }

    [Fact]
    public async Task Get_RecordPredatingGrantorSplit_ReadsWithNullGrantorTypeAndRecordedBy()
    {
        var (controller, repo, _, _) = BuildController();
        var legacy = new Consent
        {
            TenantId = "tenant-a",
            MemberId = "M1",
            ConsentType = ConsentType.GeneralAuthorization,
            GrantedBy = "legacy-value",
            CreatedAt = DateTime.UtcNow
        };
        await repo.CreateAsync(legacy, new ConsentEvent
        {
            TenantId = "tenant-a", ConsentId = legacy.Id, MemberId = "M1",
            EventType = ConsentEventType.ConsentCreated, ToStatus = ConsentStatus.Draft, ActorId = "legacy-actor"
        });

        var view = (Consent)((OkObjectResult)await controller.GetConsent("M1", legacy.Id, CancellationToken.None)).Value!;

        view.GrantedBy.Should().Be("legacy-value");
        view.GrantorType.Should().BeNull("an old record's consenting party is unknown and is not inferred");
        view.RecordedBy.Should().BeNull();
    }

    [Fact]
    public async Task ActivateAndRevoke_RecordTokenSubject_NotSystem()
    {
        var (controller, repo, publisher, _) = BuildController(user: "token-user-7");
        var create = await controller.CreateConsent("M1", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization,
            GrantorType = ConsentGrantorType.Member,
            GrantedBy = "M1"
        }, CancellationToken.None);
        var id = ((Consent)((CreatedAtActionResult)create).Value!).Id;

        await controller.Activate("M1", id, null, CancellationToken.None);
        var revoked = (Consent)((OkObjectResult)await controller.Revoke("M1", id, null, CancellationToken.None)).Value!;

        revoked.ActivatedBy.Should().Be("token-user-7");
        revoked.RevokedBy.Should().Be("token-user-7");
        repo.SnapshotEvents().Should().HaveCount(3).And.OnlyContain(e => e.ActorId == "token-user-7");
        publisher.Calls.Should().OnlyContain(c => c.Actor == "token-user-7");
    }

    [Fact]
    public async Task Revoke_WithExpiredReasonCode_Returns400_AndDoesNotTransition()
    {
        var (controller, repo, publisher, _) = BuildController();
        var create = await controller.CreateConsent("M1", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization,
            GrantorType = ConsentGrantorType.Member,
            GrantedBy = "M1"
        }, CancellationToken.None);
        var id = ((Consent)((CreatedAtActionResult)create).Value!).Id;
        await controller.Activate("M1", id, null, CancellationToken.None);
        publisher.Calls.Clear();

        var result = await controller.Revoke("M1", id,
            new RevokeConsentRequest { ReasonCode = ConsentRevocationReasonCode.Expired },
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await repo.GetByIdAsync("tenant-a", "M1", id))!.Status.Should().Be(ConsentStatus.Active);
        repo.SnapshotEvents().Should().NotContain(e => e.EventType == ConsentEventType.ConsentRevoked);
        publisher.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_Returns201_PersistsEncrypted_WritesAuditAndKafka()
    {
        var (controller, repo, publisher, _) = BuildController();

        var result = await controller.CreateConsent("M123", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization,
            GrantorType = ConsentGrantorType.Member,
            GrantedBy = "M123",
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
            GrantorType = ConsentGrantorType.Member,
            GrantedBy = "M1"
        }, CancellationToken.None);
        var id = ((Consent)((CreatedAtActionResult)create).Value!).Id;

        // Same repo, different tenant context.
        var http = NewHttpContext("tenant-b", subject: null);
        var controllerB = new ConsentsController(repoA, repoA,
            new ReversibleConsentFieldEncryptor(),
            new RecordingConsentEventPublisher(),
            ActorFor(http));
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
                GrantorType = ConsentGrantorType.Member,
                GrantedBy = "M1"
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
            ConsentType = ConsentType.GeneralAuthorization,
            GrantorType = ConsentGrantorType.Member,
            GrantedBy = "M1"
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
            ConsentType = ConsentType.GeneralAuthorization,
            GrantorType = ConsentGrantorType.Member,
            GrantedBy = "M1"
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
            ConsentType = ConsentType.GeneralAuthorization,
            GrantorType = ConsentGrantorType.Member,
            GrantedBy = "M1"
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
            ConsentType = ConsentType.GeneralAuthorization,
            GrantorType = ConsentGrantorType.Member,
            GrantedBy = "M1",
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
            ConsentType = ConsentType.GeneralAuthorization,
            GrantorType = ConsentGrantorType.Member,
            GrantedBy = "M1"
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
            ConsentType = ConsentType.GeneralAuthorization,
            GrantorType = ConsentGrantorType.Member,
            GrantedBy = "M1",
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

        var http = NewHttpContext("tenant-a", "alice");
        var controller = new ConsentsController(repo.Object, events.Object, encryptor, publisher, ActorFor(http));
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var result = await controller.Activate("M1", "c-1", request: null, CancellationToken.None);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value.Should().BeOfType<ProblemDetails>()
            .Which.Extensions["fromStatus"].Should().Be(ConsentStatus.Active.ToString());
        publisher.Calls.Should().NotContain(c => c.ToStatus == ConsentStatus.Active,
            "publisher must not emit a status-changed event when the persisted transition never happened");
    }
}
