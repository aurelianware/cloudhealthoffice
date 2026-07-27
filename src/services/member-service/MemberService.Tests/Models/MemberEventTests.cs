using MemberService.Models;

namespace MemberService.Tests.Models;

public class MemberEventTests
{
    [Fact]
    public void BuildPartitionKey_Composes_Tenant_Member()
    {
        MemberEvent.BuildPartitionKey("t1", "m1").Should().Be("t1:m1");
    }

    [Fact]
    public void BuildMongoDocumentId_ScopesEventIdByTenantAndMember()
    {
        var first = MemberEvent.BuildMongoDocumentId("tenant-a", "member-1", "same-event");
        var repeat = MemberEvent.BuildMongoDocumentId("tenant-a", "member-1", "same-event");
        var otherTenant = MemberEvent.BuildMongoDocumentId("tenant-b", "member-1", "same-event");
        var otherMember = MemberEvent.BuildMongoDocumentId("tenant-a", "member-2", "same-event");

        first.Should().Be(repeat);
        first.Should().NotBe(otherTenant);
        first.Should().NotBe(otherMember);
        first.Should().HaveLength(64);
    }

    [Fact]
    public void Event_Defaults_SchemaVersion_One_And_OccurredAt_Recent()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var evt = new MemberEvent();
        evt.SchemaVersion.Should().Be(1);
        evt.OccurredAt.Should().BeOnOrAfter(before);
    }
}
