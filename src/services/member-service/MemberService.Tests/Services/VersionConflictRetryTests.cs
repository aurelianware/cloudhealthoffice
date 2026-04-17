using MemberService.Models;
using MemberService.Services;
using MemberService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemberService.Tests.Services;

public class VersionConflictRetryTests
{
    [Fact]
    public async Task Publish_RetriesOnVersionConflict_ThenSucceeds()
    {
        var inner = new InMemoryMemberEventRepository();
        var racing = new ConflictingMemberEventRepository(inner, conflicts: 3);
        var pub = new CosmosMemberEventPublisher(racing, NullLogger<CosmosMemberEventPublisher>.Instance);

        var result = await pub.PublishAsync(new MemberEvent
        {
            TenantId = "t1",
            MemberId = "m1",
            EventId = "e1",
            EventType = MemberEventType.MemberCreated
        });

        result.Version.Should().Be(1);
        racing.AttemptsSeen.Should().Be(4); // 3 conflicts + 1 success
        inner.All.Should().ContainSingle();
    }

    [Fact]
    public async Task Publish_ExceedsRetryBudget_ThrowsConcurrencyException()
    {
        var inner = new InMemoryMemberEventRepository();
        var racing = new ConflictingMemberEventRepository(inner, conflicts: 10);
        var pub = new CosmosMemberEventPublisher(racing, NullLogger<CosmosMemberEventPublisher>.Instance);

        var act = async () => await pub.PublishAsync(new MemberEvent
        {
            TenantId = "t1",
            MemberId = "m1",
            EventId = "e1",
            EventType = MemberEventType.MemberCreated
        });

        await act.Should().ThrowAsync<ConcurrencyException>();
        inner.All.Should().BeEmpty();
    }
}
