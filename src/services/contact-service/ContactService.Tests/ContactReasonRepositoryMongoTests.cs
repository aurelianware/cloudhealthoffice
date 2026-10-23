using CloudHealthOffice.Testing.Mongo;
using ContactService.Models;
using ContactService.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace ContactService.Tests;

/// <summary>
/// The reason taxonomy against a real MongoDB (EphemeralMongo). The code is
/// the identity — contacts carry it — so saving by code must update rather
/// than duplicate, and seeding must never overwrite a plan's own list.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ContactReasonRepositoryMongoTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-reasons";
    private const string OtherTenant = "tenant-other";

    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("contact_reasons");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private ContactReasonRepositoryMongo Repo(string tenant = Tenant)
    {
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = tenant;

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(http);

        return new ContactReasonRepositoryMongo(_db, new ConfigurationBuilder().Build(), accessor.Object);
    }

    private static ContactReason Reason(
        string code, string label, string? category = "Claims", bool active = true, int order = 0) => new()
    {
        Code = code, Label = label, Category = category, IsActive = active, Order = order,
    };

    [Fact]
    public async Task A_code_is_stored_upper_cased_and_matched_either_way()
    {
        await Repo().SaveAsync(Reason(" claim_status ", "Claim status"));

        (await Repo().GetByCodeAsync("CLAIM_STATUS"))!.Code.Should().Be("CLAIM_STATUS");
        (await Repo().GetByCodeAsync("claim_status"))!.Label.Should().Be("Claim status");
    }

    [Fact]
    public async Task Saving_the_same_code_again_renames_it_rather_than_duplicating_it()
    {
        var first = await Repo().SaveAsync(Reason("BENEFITS", "Benefits"));
        var second = await Repo().SaveAsync(Reason("BENEFITS", "Benefits and coverage"));

        // The identity and the creation time survive, because contacts
        // already carry this code.
        second.Id.Should().Be(first.Id);
        // Within a millisecond, which is all MongoDB stores of a DateTime.
        second.CreatedAt.Should().BeCloseTo(first.CreatedAt, TimeSpan.FromMilliseconds(1));
        (await Repo().ListAsync()).Should().ContainSingle()
            .Which.Label.Should().Be("Benefits and coverage");
    }

    [Fact]
    public async Task A_retired_reason_is_kept_but_not_offered()
    {
        await Repo().SaveAsync(Reason("BENEFITS", "Benefits"));
        await Repo().SaveAsync(Reason("OLD", "Retired last quarter", active: false));

        (await Repo().ListAsync(activeOnly: true)).Select(r => r.Code).Should().Equal("BENEFITS");
        // Still readable, so last quarter's report still adds up.
        (await Repo().ListAsync()).Select(r => r.Code).Should().BeEquivalentTo(["BENEFITS", "OLD"]);
    }

    [Fact]
    public async Task The_list_puts_active_reasons_first_then_category_and_order()
    {
        await Repo().SaveAsync(Reason("B2", "Second", category: "Benefits", order: 2));
        await Repo().SaveAsync(Reason("A1", "First", category: "Benefits", order: 1));
        await Repo().SaveAsync(Reason("Z9", "Retired", category: "Benefits", order: 0, active: false));

        (await Repo().ListAsync()).Select(r => r.Code).Should().Equal("A1", "B2", "Z9");
    }

    [Fact]
    public async Task Another_tenants_taxonomy_is_invisible()
    {
        await Repo().SaveAsync(Reason("BENEFITS", "Benefits"));

        (await Repo(OtherTenant).ListAsync()).Should().BeEmpty();
        (await Repo(OtherTenant).GetByCodeAsync("BENEFITS")).Should().BeNull();
    }

    [Fact]
    public async Task Two_tenants_can_hold_the_same_code()
    {
        await Repo().SaveAsync(Reason("BENEFITS", "Benefits"));
        await Repo(OtherTenant).SaveAsync(Reason("BENEFITS", "Cover questions"));

        (await Repo().GetByCodeAsync("BENEFITS"))!.Label.Should().Be("Benefits");
        (await Repo(OtherTenant).GetByCodeAsync("BENEFITS"))!.Label.Should().Be("Cover questions");
    }

    // ── Seeding ───────────────────────────────────────────────────

    [Fact]
    public async Task A_tenant_with_no_reasons_gets_the_starter_list()
    {
        var added = await Repo().SeedStarterReasonsAsync();

        added.Should().Be(StarterContactReasons.All.Count);
        var seeded = await Repo().ListAsync();
        seeded.Should().HaveCount(StarterContactReasons.All.Count);
        seeded.Should().OnlyContain(r => r.SystemKey != null && r.IsActive);
        seeded.Where(r => r.IsComplaintIndicator).Select(r => r.Code)
              .Should().BeEquivalentTo(["COMPLAINT", "ACCESS_TO_CARE"]);
    }

    [Fact]
    public async Task Seeding_twice_adds_nothing_the_second_time()
    {
        await Repo().SeedStarterReasonsAsync();

        (await Repo().SeedStarterReasonsAsync()).Should().Be(0);
        (await Repo().ListAsync()).Should().HaveCount(StarterContactReasons.All.Count);
    }

    [Fact]
    public async Task A_plan_that_pruned_the_starter_list_does_not_get_it_back()
    {
        // One reason of their own is enough to say the plan has configured
        // its taxonomy; re-seeding over it would undo a deliberate choice.
        await Repo().SaveAsync(Reason("OUR_OWN", "How we count calls"));

        (await Repo().SeedStarterReasonsAsync()).Should().Be(0);
        (await Repo().ListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Seeding_one_tenant_leaves_another_alone()
    {
        await Repo().SeedStarterReasonsAsync();

        (await Repo(OtherTenant).ListAsync()).Should().BeEmpty();
    }
}
