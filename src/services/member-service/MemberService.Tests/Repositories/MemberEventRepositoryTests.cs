using System.Text.Json.Nodes;
using MemberService.Models;
using MemberService.Tests.Fakes;

namespace MemberService.Tests.Repositories;

public class MemberEventRepositoryTests
{
    [Fact]
    public async Task Append_AssignsVersionAndReturnsAppended()
    {
        var repo = new InMemoryMemberEventRepository();
        var version = await repo.GetNextVersionAsync("t1", "m1");
        version.Should().Be(1);

        var evt = new MemberEvent
        {
            TenantId = "t1",
            MemberId = "m1",
            EventId = "evt-1",
            EventType = MemberEventType.MemberCreated,
            Version = version,
            Payload = new JsonObject { ["x"] = 1 }
        };

        var result = await repo.AppendAsync(evt);
        result.Appended.Should().BeTrue();
        (await repo.GetNextVersionAsync("t1", "m1")).Should().Be(2);
    }

    [Fact]
    public async Task Append_DuplicateEventId_IsNoOp()
    {
        var repo = new InMemoryMemberEventRepository();
        var evt = new MemberEvent
        {
            TenantId = "t1", MemberId = "m1",
            EventId = "same", EventType = MemberEventType.MemberCreated, Version = 1
        };
        (await repo.AppendAsync(evt)).Appended.Should().BeTrue();

        var dup = new MemberEvent
        {
            TenantId = "t1", MemberId = "m1",
            EventId = "same", EventType = MemberEventType.MemberUpdated, Version = 2
        };
        var second = await repo.AppendAsync(dup);
        second.Appended.Should().BeFalse();

        var list = await repo.ListByMemberAsync("t1", "m1");
        list.Should().HaveCount(1);
        list[0].EventType.Should().Be(MemberEventType.MemberCreated);
    }

    [Fact]
    public async Task ListByMember_OrdersByVersionAscending()
    {
        var repo = new InMemoryMemberEventRepository();
        await repo.AppendAsync(new MemberEvent { TenantId="t",MemberId="m",EventId="c",EventType=MemberEventType.MemberTerminated,Version=3 });
        await repo.AppendAsync(new MemberEvent { TenantId="t",MemberId="m",EventId="a",EventType=MemberEventType.MemberCreated,Version=1 });
        await repo.AppendAsync(new MemberEvent { TenantId="t",MemberId="m",EventId="b",EventType=MemberEventType.MemberUpdated,Version=2 });

        var list = await repo.ListByMemberAsync("t", "m");
        list.Select(e => e.Version).Should().ContainInOrder(1, 2, 3);
    }

    [Fact]
    public async Task ListByMember_IsTenantScoped()
    {
        var repo = new InMemoryMemberEventRepository();
        await repo.AppendAsync(new MemberEvent { TenantId="t1",MemberId="m",EventId="a",Version=1 });
        await repo.AppendAsync(new MemberEvent { TenantId="t2",MemberId="m",EventId="b",Version=1 });

        (await repo.ListByMemberAsync("t1", "m")).Should().ContainSingle();
        (await repo.ListByMemberAsync("t2", "m")).Should().ContainSingle();
    }
}
