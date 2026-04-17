using MemberService.Controllers;
using MemberService.Models;
using MemberService.Services;
using MemberService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MemberService.Tests.Controllers;

/// <summary>
/// Verifies that the retried PUT /members/{id} request (same EventId) produces
/// exactly ONE <c>MemberUpdated</c> and ONE <c>AddressChanged</c> in the event
/// stream. The sub-event id is derived as <c>{parentEventId}:address</c> so
/// retries collide on EventId and become idempotent no-ops.
/// </summary>
public class AddressChangedIdempotencyTests
{
    private const string Tenant = "tenant-test";

    private static (MembersController ctl, InMemoryMemberEventRepository events) Build()
    {
        var repo = new InMemoryMemberRepository();
        repo.Members.Add(new Member
        {
            TenantId = Tenant,
            MemberId = "M-001",
            Id = Guid.NewGuid().ToString(),
            GroupNumber = "G",
            IsSubscriber = true,
            FirstName = "Alice",
            LastName = "Example",
            DateOfBirth = new DateTime(1990, 1, 1),
            EffectiveDate = new DateTime(2024, 1, 1),
            Address = "1 Main",
            City = "Austin"
        });

        var events = new InMemoryMemberEventRepository();
        var publisher = new CosmosMemberEventPublisher(events, NullLogger<CosmosMemberEventPublisher>.Instance);
        var ctl = new MembersController(
            repo,
            publisher,
            events,
            new FhirPatientProjector(),
            new NoOpIdentifierEncryptor(),
            Mock.Of<ICoverageServiceClient>(),
            Mock.Of<IEnrollmentImportServiceClient>(),
            Mock.Of<IAccumulatorServiceClient>());

        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctl, events);
    }

    [Fact]
    public async Task UpdateMember_SameEventIdTwice_ProducesExactlyOneUpdateAndOneAddressChange()
    {
        var (ctl, events) = Build();
        var request = new UpdateMemberRequest
        {
            Address = "500 New St",
            City = "Dallas",
            EventId = "evt-client-42"
        };

        (await ctl.UpdateMember("M-001", request, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await ctl.UpdateMember("M-001", request, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        events.All.Count(e => e.EventType == MemberEventType.MemberUpdated).Should().Be(1);
        events.All.Count(e => e.EventType == MemberEventType.AddressChanged).Should().Be(1);

        events.All.Single(e => e.EventType == MemberEventType.MemberUpdated)
            .EventId.Should().Be("evt-client-42");
        events.All.Single(e => e.EventType == MemberEventType.AddressChanged)
            .EventId.Should().Be("evt-client-42:address");
    }

    [Fact]
    public async Task UpdateMember_DifferentEventIds_ProduceDistinctEvents()
    {
        var (ctl, events) = Build();

        await ctl.UpdateMember("M-001",
            new UpdateMemberRequest { Address = "A1", EventId = "evt-A" }, CancellationToken.None);
        await ctl.UpdateMember("M-001",
            new UpdateMemberRequest { Address = "A2", EventId = "evt-B" }, CancellationToken.None);

        events.All.Count(e => e.EventType == MemberEventType.MemberUpdated).Should().Be(2);
        events.All.Count(e => e.EventType == MemberEventType.AddressChanged).Should().Be(2);
    }
}
