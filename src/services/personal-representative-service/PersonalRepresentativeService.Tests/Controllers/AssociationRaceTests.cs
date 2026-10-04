using PersonalRepresentativeService.Controllers;
using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;

namespace PersonalRepresentativeService.Tests.Controllers;

/// <summary>
/// The member set an activating user approves is exactly the set the
/// representative covers, even when a member is added concurrently:
/// whichever request commits second withdraws the unapproved association.
/// </summary>
public class AssociationRaceTests
{
    private const string Tenant = "tenant-race";

    private static (PersonalRepresentativesController controller, HookedPersonalRepRepository hooked,
        InMemoryPersonalRepRepository inner, RecordingPersonalRepEventPublisher publisher) Build()
    {
        var inner = new InMemoryPersonalRepRepository();
        var hooked = new HookedPersonalRepRepository(inner);
        var publisher = new RecordingPersonalRepEventPublisher();
        var http = PersonalRepresentativesControllerTests.NewHttpContext(Tenant, "user-a");
        var controller = new PersonalRepresentativesController(hooked, inner,
            new ReversiblePersonalRepFieldEncryptor(), publisher,
            PersonalRepresentativesControllerTests.ActorFor(http), new AllowingActivationControls());
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, hooked, inner, publisher);
    }

    private static async Task<string> DraftAsync(PersonalRepresentativesController controller)
    {
        var created = await controller.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.LegalGuardian
        }, CancellationToken.None);
        return ((PersonalRepresentative)((CreatedAtActionResult)created).Value!).Id;
    }

    private static PersonalRepAssociation Pair(string repId, string memberId, string pairId, AssociationDirection direction)
        => new()
        {
            TenantId = Tenant, PairId = pairId, RepId = repId, MemberId = memberId, Direction = direction,
            CredentialType = PersonalRepCredentialType.LegalGuardian, EffectiveFrom = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, CreatedBy = "user-b"
        };

    [Fact]
    public async Task MemberAdded_WhileActivationIsChecked_IsWithdrawn()
    {
        var (controller, hooked, inner, publisher) = Build();
        var repId = await DraftAsync(controller);
        await controller.AddAssociation(repId, new AddAssociationRequest { MemberId = "M1" }, CancellationToken.None);

        // Another request adds M2 after activation listed the members, before it commits.
        hooked.BeforeTransition = () =>
        {
            var pairId = Guid.NewGuid().ToString();
            return inner.AddAssociationPairAsync(
                Pair(repId, "M2", pairId, AssociationDirection.RepToMember),
                Pair(repId, "M2", pairId, AssociationDirection.MemberToRep),
                new PersonalRepEvent
                {
                    TenantId = Tenant, PersonalRepId = repId, MemberId = "M2",
                    EventType = PersonalRepEventType.PersonalRepAssociationAdded, ActorId = "user-b"
                });
        };

        var result = await controller.Activate(repId, null, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        (await inner.FindActiveAssociationAsync(Tenant, repId, "M2")).Should().BeNull();
        (await inner.FindActiveAssociationAsync(Tenant, repId, "M1")).Should().NotBeNull();

        var activated = inner.SnapshotEvents().Single(e => e.EventType == PersonalRepEventType.PersonalRepActivated);
        activated.Payload!["approvedMemberIds"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().BeEquivalentTo(new[] { "M1" });
        var withdrawn = inner.SnapshotEvents().Single(e =>
            e.EventType == PersonalRepEventType.PersonalRepAssociationRemoved && e.MemberId == "M2");
        withdrawn.ActorId.Should().Be("System");
        withdrawn.Payload!["withdrawnReason"]!.GetValue<string>().Should().Contain("not approved");
        withdrawn.Payload["addedBy"]!.GetValue<string>().Should().Be("user-b");

        publisher.AssociationCalls.Should().Contain(c =>
            c.MemberId == "M2" && c.EventType == PersonalRepEventType.PersonalRepAssociationRemoved && c.Actor == "System");
        publisher.StatusCalls.Single(c => c.ToStatus == PersonalRepStatus.Active)
            .AssociatedMemberIds.Should().BeEquivalentTo(new[] { "M1" });
    }

    [Fact]
    public async Task Activation_LandingWhileAMemberIsAdded_WithdrawsThatMember()
    {
        var (controller, hooked, inner, publisher) = Build();
        var repId = await DraftAsync(controller);

        // Another request activates the rep after this one checked it was a Draft.
        hooked.AfterAddAssociationPair = async () =>
        {
            var rep = (await inner.GetByIdAsync(Tenant, repId))!;
            rep.Status = PersonalRepStatus.Active;
            await inner.TransitionStatusAsync(rep, new PersonalRepEvent
            {
                TenantId = Tenant, PersonalRepId = repId, EventType = PersonalRepEventType.PersonalRepActivated,
                FromStatus = PersonalRepStatus.Draft, ToStatus = PersonalRepStatus.Active, ActorId = "user-b"
            });
        };

        var result = await controller.AddAssociation(repId,
            new AddAssociationRequest { MemberId = "M9" }, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        (await inner.FindActiveAssociationAsync(Tenant, repId, "M9")).Should().BeNull();
        inner.SnapshotEvents().Should().Contain(e =>
            e.EventType == PersonalRepEventType.PersonalRepAssociationRemoved && e.MemberId == "M9"
            && e.Payload!["withdrawnReason"]!.GetValue<string>().Contains("left Draft"));
        publisher.AssociationCalls.Should().NotContain(c => c.MemberId == "M9",
            "the addition was never announced, so there is nothing to retract");
    }
}
