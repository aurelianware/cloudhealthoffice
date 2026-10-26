using CloudHealthOffice.Testing.Mongo;
using ContactService.Models;
using ContactService.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace ContactService.Tests;

/// <summary>
/// The contact store against a real MongoDB (EphemeralMongo): the tenant
/// boundary, the revision fence, the unique contact number, and the queries
/// the screens actually make.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ContactRepositoryMongoTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-contacts";
    private const string OtherTenant = "tenant-other";

    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("contacts");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private ContactRepositoryMongo Repo(string tenant = Tenant)
    {
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = tenant;

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(http);

        return new ContactRepositoryMongo(_db, new ConfigurationBuilder().Build(), accessor.Object);
    }

    private static Contact New(
        string number,
        string? memberId = "M1",
        string? npi = null,
        string agentId = "rep-1",
        ContactStatus status = ContactStatus.Open,
        DateTime? startedAt = null) => new()
    {
        ContactNumber = number,
        MemberId = memberId,
        ProviderNpi = npi,
        AgentId = agentId,
        Status = status,
        StartedAt = startedAt ?? new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc),
        Caller = new ContactCaller { Type = CallerType.Member, Name = "Ada" },
        CreatedBy = agentId,
    };

    // ── The tenant boundary ───────────────────────────────────────

    [Fact]
    public async Task A_contact_is_stamped_with_the_callers_tenant_not_the_bodys()
    {
        var created = await Repo().CreateAsync(New("C-1"));

        created.TenantId.Should().Be(Tenant);
        created.Revision.Should().Be(1);
    }

    [Fact]
    public async Task Another_tenant_cannot_read_it()
    {
        var created = await Repo().CreateAsync(New("C-1"));

        (await Repo(OtherTenant).GetByIdAsync(created.Id)).Should().BeNull();
        (await Repo(OtherTenant).GetByContactNumberAsync("C-1")).Should().BeNull();
        (await Repo(OtherTenant).ListByMemberAsync("M1")).Should().BeEmpty();
    }

    [Fact]
    public async Task Another_tenant_cannot_write_over_it()
    {
        var created = await Repo().CreateAsync(New("C-1"));
        created.Summary = "changed by the wrong tenant";

        await Repo(OtherTenant).Invoking(r => r.UpdateAsync(created))
              .Should().ThrowAsync<ContactConcurrencyException>();

        (await Repo().GetByIdAsync(created.Id))!.Summary.Should().BeNull();
    }

    [Fact]
    public async Task The_same_contact_number_can_exist_in_two_tenants()
    {
        await Repo().CreateAsync(New("C-SHARED"));

        await Repo(OtherTenant).Invoking(r => r.CreateAsync(New("C-SHARED")))
              .Should().NotThrowAsync();
    }

    [Fact]
    public async Task Two_contacts_in_one_tenant_cannot_share_a_number()
    {
        await Repo().CreateAsync(New("C-1"));

        (await Repo().Invoking(r => r.CreateAsync(New("C-1")))
              .Should().ThrowAsync<DuplicateContactNumberException>())
              .Which.ContactNumber.Should().Be("C-1");
    }

    // ── The revision fence ────────────────────────────────────────

    [Fact]
    public async Task A_write_bumps_the_revision()
    {
        var created = await Repo().CreateAsync(New("C-1"));
        created.Summary = "wrapped up";

        (await Repo().UpdateAsync(created)).Revision.Should().Be(2);
    }

    [Fact]
    public async Task Of_two_writes_from_the_same_read_the_later_one_is_refused()
    {
        var created = await Repo().CreateAsync(New("C-1"));

        var first = (await Repo().GetByIdAsync(created.Id))!;
        var second = (await Repo().GetByIdAsync(created.Id))!;

        first.Summary = "the rep's wrap-up";
        await Repo().UpdateAsync(first);

        // A supervisor who read before that write must not erase it.
        second.Summary = "the supervisor's note";
        await Repo().Invoking(r => r.UpdateAsync(second))
              .Should().ThrowAsync<ContactConcurrencyException>();

        (await Repo().GetByIdAsync(created.Id))!.Summary.Should().Be("the rep's wrap-up");
    }

    [Fact]
    public async Task A_refused_write_leaves_the_callers_revision_where_it_was()
    {
        // Otherwise a caller that retries from the same object races forward
        // past the revision it actually read.
        var created = await Repo().CreateAsync(New("C-1"));
        var stale = (await Repo().GetByIdAsync(created.Id))!;

        await Repo().UpdateAsync((await Repo().GetByIdAsync(created.Id))!);

        await Repo().Invoking(r => r.UpdateAsync(stale)).Should().ThrowAsync<ContactConcurrencyException>();

        stale.Revision.Should().Be(1);
    }

    // ── The queries the screens make ──────────────────────────────

    [Fact]
    public async Task A_members_history_comes_back_newest_first()
    {
        var day = new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);
        await Repo().CreateAsync(New("C-1", startedAt: day));
        await Repo().CreateAsync(New("C-2", startedAt: day.AddHours(2)));
        await Repo().CreateAsync(New("C-3", memberId: "M2", startedAt: day.AddHours(3)));

        (await Repo().ListByMemberAsync("M1")).Select(c => c.ContactNumber)
            .Should().Equal("C-2", "C-1");
    }

    [Fact]
    public async Task A_providers_history_is_found_by_npi()
    {
        await Repo().CreateAsync(New("C-1", memberId: null, npi: "1234567893"));
        await Repo().CreateAsync(New("C-2", memberId: null, npi: "9999999999"));

        (await Repo().ListByProviderAsync("1234567893")).Select(c => c.ContactNumber)
            .Should().Equal("C-1");
    }

    [Fact]
    public async Task Who_called_about_this_claim_finds_the_contact_by_its_subject()
    {
        var withClaim = New("C-1");
        withClaim.Subjects.Add(new ContactSubject
        {
            Type = ContactSubjectType.Claim, SubjectId = "claim-7", LinkedAt = DateTime.UtcNow,
        });
        await Repo().CreateAsync(withClaim);
        await Repo().CreateAsync(New("C-2"));

        (await Repo().ListBySubjectAsync(ContactSubjectType.Claim, "claim-7"))
            .Select(c => c.ContactNumber).Should().Equal("C-1");
    }

    [Fact]
    public async Task A_subject_id_matching_a_different_type_is_not_returned()
    {
        var withAuth = New("C-1");
        withAuth.Subjects.Add(new ContactSubject
        {
            Type = ContactSubjectType.Authorization, SubjectId = "shared-id", LinkedAt = DateTime.UtcNow,
        });
        await Repo().CreateAsync(withAuth);

        (await Repo().ListBySubjectAsync(ContactSubjectType.Claim, "shared-id")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_rep_can_find_what_they_left_open()
    {
        await Repo().CreateAsync(New("C-1", status: ContactStatus.Closed));
        await Repo().CreateAsync(New("C-2", status: ContactStatus.Open));
        await Repo().CreateAsync(New("C-3", agentId: "rep-2", status: ContactStatus.Open));

        (await Repo().ListByAgentAsync("rep-1", openOnly: true)).Select(c => c.ContactNumber)
            .Should().Equal("C-2");

        (await Repo().ListByAgentAsync("rep-1")).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_list_limit_is_clamped_rather_than_trusted()
    {
        await Repo().CreateAsync(New("C-1"));

        // 0 and negatives would otherwise mean "no limit" or throw.
        (await Repo().ListByMemberAsync("M1", limit: 0)).Should().HaveCount(1);
        (await Repo().ListByMemberAsync("M1", limit: -5)).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_contact_is_found_by_its_number()
    {
        await Repo().CreateAsync(New("C-20261022-AAAA1111"));

        (await Repo().GetByContactNumberAsync("C-20261022-AAAA1111"))!
            .ContactNumber.Should().Be("C-20261022-AAAA1111");
    }
    [Fact]
    public async Task Recovery_history_survives_reload_and_concurrent_retry_fences_amendment()
    {
        var repo = Repo();
        var c = await repo.CreateAsync(New("C-RECOVERY"));
        c.CaseIntent = new() { State = "CA", LineOfBusiness = "Medicaid" };
        c.CaseDeliveryStatus = "Rejected";
        await repo.UpdateAsync(c);
        var correction = (await repo.GetByIdAsync(c.Id))!;
        var retry = (await repo.GetByIdAsync(c.Id))!;
        retry.CaseDeliveryStatus = "Unknown";
        await repo.UpdateAsync(retry);
        correction.CaseAmendments.Add(new() { PreviousIntent = correction.CaseIntent!,
            By = "rep-1", Reason = "Correct jurisdiction", At = DateTime.UtcNow });
        correction.CaseIntent = new() { State = "TX" };
        await repo.Invoking(r => r.UpdateAsync(correction)).Should().ThrowAsync<ContactConcurrencyException>();
        var saved = (await Repo().GetByIdAsync(c.Id))!;
        saved.CaseIntent!.State.Should().Be("CA");
        saved.CaseDeliveryStatus.Should().Be("Unknown");
        saved.CaseAmendments.Should().BeEmpty();
        saved.CaseAmendments.Add(new() { PreviousIntent = saved.CaseIntent,
            By = "rep-1", Reason = "Retained history fixture", At = DateTime.UtcNow });
        await repo.UpdateAsync(saved);
        (await Repo().GetByIdAsync(c.Id))!.CaseAmendments.Should().ContainSingle()
            .Which.PreviousIntent.State.Should().Be("CA");
        (await Repo(OtherTenant).GetByIdAsync(c.Id)).Should().BeNull();
    }

}
