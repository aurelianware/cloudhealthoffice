using System.Text.Json.Nodes;
using MemberService.Models;
using MemberService.Services;
using MemberService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemberService.Tests.Services;

public class MemberEventPublisherTests
{
    private static CosmosMemberEventPublisher NewPublisher(InMemoryMemberEventRepository repo)
        => new(repo, NullLogger<CosmosMemberEventPublisher>.Instance);

    [Fact]
    public async Task Publish_StampsVersionAndPartitionKey()
    {
        var repo = new InMemoryMemberEventRepository();
        var pub = NewPublisher(repo);

        var result = await pub.PublishAsync(new MemberEvent
        {
            TenantId = "t1",
            MemberId = "m1",
            EventId = "e1",
            EventType = MemberEventType.MemberCreated,
            Payload = new JsonObject { ["firstName"] = "Alice" }
        });

        result.Version.Should().Be(1);
        result.PartitionKey.Should().Be("t1:m1");
        result.OccurredAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
        repo.All.Should().ContainSingle();
    }

    [Fact]
    public async Task Publish_IsIdempotentOnEventId()
    {
        var repo = new InMemoryMemberEventRepository();
        var pub = NewPublisher(repo);
        var evt = new MemberEvent
        {
            TenantId = "t1", MemberId = "m1",
            EventId = "same",
            EventType = MemberEventType.AddressChanged,
            Payload = new JsonObject { ["city"] = "Austin" }
        };
        await pub.PublishAsync(evt);
        await pub.PublishAsync(evt);
        await pub.PublishAsync(evt);

        repo.All.Should().ContainSingle();
        repo.All[0].Version.Should().Be(1);
    }

    [Fact]
    public async Task Publish_IncrementsVersionPerAggregate()
    {
        var repo = new InMemoryMemberEventRepository();
        var pub = NewPublisher(repo);

        await pub.PublishAsync(new MemberEvent { TenantId="t",MemberId="m",EventId="1",EventType=MemberEventType.MemberCreated });
        await pub.PublishAsync(new MemberEvent { TenantId="t",MemberId="m",EventId="2",EventType=MemberEventType.MemberUpdated });
        await pub.PublishAsync(new MemberEvent { TenantId="t",MemberId="m",EventId="3",EventType=MemberEventType.MemberTerminated });

        var list = await repo.ListByMemberAsync("t", "m");
        list.Select(e => e.Version).Should().ContainInOrder(1, 2, 3);
    }

    [Fact]
    public async Task Publish_MissingEventId_Throws()
    {
        var pub = NewPublisher(new InMemoryMemberEventRepository());
        var act = () => pub.PublishAsync(new MemberEvent
        {
            TenantId = "t", MemberId = "m", EventType = MemberEventType.MemberCreated
        });
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Publish_DifferentMembers_HaveIndependentVersionStreams()
    {
        var repo = new InMemoryMemberEventRepository();
        var pub = NewPublisher(repo);
        await pub.PublishAsync(new MemberEvent { TenantId="t",MemberId="m1",EventId="a",EventType=MemberEventType.MemberCreated });
        await pub.PublishAsync(new MemberEvent { TenantId="t",MemberId="m2",EventId="b",EventType=MemberEventType.MemberCreated });

        repo.All.Single(e => e.MemberId == "m1").Version.Should().Be(1);
        repo.All.Single(e => e.MemberId == "m2").Version.Should().Be(1);
    }
}
