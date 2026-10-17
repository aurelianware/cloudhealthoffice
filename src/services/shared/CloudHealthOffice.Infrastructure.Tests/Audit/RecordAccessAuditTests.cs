using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Audit;

public class RecordAccessAuditTests
{
    private sealed record Row(string Id, string? MemberId);
    private sealed record Page(List<Row> Items, int TotalCount);

    [Fact]
    public void Subjects_come_from_one_record_a_list_or_a_page()
    {
        RecordSubjects.From(new Row("a", "M1")).Select(x => (x.RecordId, x.MemberId)).Should().Equal(("a", "M1"));
        RecordSubjects.From(new[] { new Row("a", "M1"), new Row("b", null) }).Select(s => s.RecordId).Should().Equal("a", "b");
        RecordSubjects.From(new Page([new Row("c", "M2")], 1)).Select(x => (x.RecordId, x.MemberId)).Should().Equal(("c", "M2"));
        RecordSubjects.From("not a record").Should().BeEmpty();
        RecordSubjects.From(null).Should().BeEmpty();
    }

    [Fact]
    public async Task The_in_memory_log_stays_inside_the_tenant_and_finds_records_in_searches()
    {
        var store = new RecordAccessStoreInMemory();
        await store.AppendAsync(new RecordAccessEvent { TenantId = "t1", RecordType = "Member", RecordId = "M1", Action = "View" });
        await store.AppendAsync(new RecordAccessEvent { TenantId = "t1", RecordType = "Member", Action = "Search", RecordIds = ["M1", "M2"] });
        await store.AppendAsync(new RecordAccessEvent { TenantId = "t2", RecordType = "Member", RecordId = "M1", Action = "View" });

        var found = await store.QueryAsync(new RecordAccessQuery("t1", RecordType: "Member", RecordId: "M1"));

        found.Select(e => e.Action).Should().BeEquivalentTo(["View", "Search"]);
        found.Should().OnlyContain(e => e.TenantId == "t1");
    }

    [Theory]
    [InlineData(200, "Allowed")]
    [InlineData(404, "NotFound")]
    [InlineData(403, "Denied")]
    [InlineData(401, "Denied")]
    public void Outcome_follows_the_status(int status, string outcome) =>
        RecordAccessOutcomes.For(status).Should().Be(outcome);

    private sealed class HangingStore : IRecordAccessStore
    {
        public Task AppendAsync(RecordAccessEvent accessEvent, CancellationToken ct = default) => Task.Delay(Timeout.Infinite, ct);
        public Task<IReadOnlyList<RecordAccessEvent>> QueryAsync(RecordAccessQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecordAccessEvent>>([]);
    }

    [Fact]
    public async Task An_unresponsive_store_holds_the_read_no_longer_than_the_append_timeout()
    {
        var writer = new RecordAccessWriter(new HangingStore(), NullLogger<RecordAccessWriter>.Instance);
        var actor = Mock.Of<ICurrentActor>(a => a.UserId == "u1" && a.TenantId == "t1" && a.Roles == Array.Empty<string>());
        var started = DateTime.UtcNow;

        await writer.AppendAsync(new DefaultHttpContext(), actor, new RecordAccessEvent { RecordType = "Member", Action = "View" });

        (DateTime.UtcNow - started).Should().BeLessThan(RecordAccessWriter.AppendTimeout + TimeSpan.FromSeconds(2));
    }

    private sealed record Line(string ClaimId, string? MemberId);
    private sealed record PaymentLike(string Id, List<Line> ClaimPayments);

    [Fact]
    public void Members_named_inside_a_record_are_collected()
    {
        var subjects = RecordSubjects.From(
            new PaymentLike("pay-1", [new("c1", "M1"), new("c2", "M2"), new("c3", "M1"), new("c4", null)]),
            membersCollection: "ClaimPayments");

        subjects.Should().ContainSingle().Which.NestedMemberIds.Should().Equal("M1", "M2");
    }

    [Fact]
    public async Task A_member_search_matches_events_listing_the_member_among_several()
    {
        var store = new RecordAccessStoreInMemory();
        await store.AppendAsync(new RecordAccessEvent { TenantId = "t1", RecordType = "Payment", RecordId = "pay-1", MemberIds = ["M1", "M2"] });
        await store.AppendAsync(new RecordAccessEvent { TenantId = "t1", RecordType = "Payment", RecordId = "pay-2", MemberIds = ["M3", "M4"] });

        var found = await store.QueryAsync(new RecordAccessQuery("t1", MemberId: "M2"));

        found.Should().ContainSingle().Which.RecordId.Should().Be("pay-1");
    }
}
