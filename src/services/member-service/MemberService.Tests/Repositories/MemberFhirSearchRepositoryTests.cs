using CloudHealthOffice.Testing.Mongo;
using MemberService.Models;
using MemberService.Repositories;
using MongoDB.Driver;

namespace MemberService.Tests.Repositories;

/// <summary>
/// FHIR Patient search semantics over real MongoDB (EphemeralMongo), ADR 018:
/// tenant-only results, drafts excluded before paging, a stable order and an
/// exact total, case-insensitive literal prefixes for names, half-open date
/// ranges, and the gender mapping the Patient projection uses.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MemberFhirSearchRepositoryTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "t1";
    private IMongoDatabase _db = null!;
    private MemberFhirSearchRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("members_fhir_search");
        _repo = new MemberFhirSearchRepository(_db);
        await _db.GetCollection<Member>("Members").InsertManyAsync(new[]
        {
            Member("M01", "Smith", "Anna", null, new DateTime(1980, 5, 1), "F", Updated(1)),
            Member("M02", "smithers", "Bob", "Lee", new DateTime(1980, 5, 2), "M", Updated(2)),
            Member("M03", "Goldsmith", "Carla", "Anne", new DateTime(1990, 1, 1), "f", Updated(3)),
            Member("M04", "Jones", "Dee", null, new DateTime(1979, 12, 31), "U", Updated(4)),
            Member("M05", "Jones", "Eve", null, new DateTime(2001, 7, 4), null, Updated(5)),
            Member("M06", ".*Literal", "Fay", null, new DateTime(1985, 1, 1), "F", Updated(6)),
            Member("M00", "Draft", "Gus", null, new DateTime(1980, 5, 1), "M", Updated(7), draft: true),
            Member("M01", "Smith", "Anna", null, new DateTime(1980, 5, 1), "F", Updated(1), tenant: "t2"),
            Member("X99", "Smith", "Zed", null, new DateTime(1980, 5, 1), "M", Updated(1), tenant: "t2"),
        });
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static DateTime Updated(int day) => new(2026, 3, day, 12, 0, 0, DateTimeKind.Utc);

    private static Member Member(
        string memberId, string last, string first, string? middle, DateTime dob, string? gender,
        DateTime updated, string tenant = Tenant, bool draft = false) => new()
    {
        TenantId = tenant,
        MemberId = memberId,
        LastName = last,
        FirstName = first,
        MiddleName = middle,
        DateOfBirth = DateTime.SpecifyKind(dob, DateTimeKind.Utc),
        Gender = gender,
        LastUpdatedDate = updated,
        GroupNumber = "G1",
        IsDraft = draft,
    };

    private async Task<(string[] Ids, long Total)> Search(MemberFhirSearchQuery query, string tenant = Tenant)
    {
        var (items, total) = await _repo.SearchAsync(tenant, query);
        return (items.Select(m => m.MemberId).ToArray(), total);
    }

    [Fact]
    public async Task No_filters_returns_the_tenants_non_draft_members_in_member_id_order_with_the_total()
    {
        var (ids, total) = await Search(new MemberFhirSearchQuery { Limit = 100 });

        ids.Should().Equal("M01", "M02", "M03", "M04", "M05", "M06");
        total.Should().Be(6);
    }

    [Fact]
    public async Task Another_tenants_members_are_never_returned_even_by_exact_id()
    {
        (await Search(new MemberFhirSearchQuery { MemberId = "X99" })).Ids.Should().BeEmpty();
        (await Search(new MemberFhirSearchQuery { Limit = 100 }, tenant: "t2")).Ids.Should().Equal("M01", "X99");
    }

    [Fact]
    public async Task Drafts_are_excluded_before_paging_so_pages_are_full_and_contiguous()
    {
        var first = await Search(new MemberFhirSearchQuery { Offset = 0, Limit = 4 });
        var second = await Search(new MemberFhirSearchQuery { Offset = 4, Limit = 4 });

        first.Ids.Should().Equal("M01", "M02", "M03", "M04");
        second.Ids.Should().Equal("M05", "M06");
        first.Total.Should().Be(6);
        second.Total.Should().Be(6);
    }

    [Fact]
    public async Task Limit_zero_returns_only_the_total()
    {
        var (ids, total) = await Search(new MemberFhirSearchQuery { Family = "jones", Limit = 0 });

        ids.Should().BeEmpty();
        total.Should().Be(2);
    }

    [Fact]
    public async Task Family_is_a_case_insensitive_prefix_of_the_literal_text()
    {
        (await Search(new MemberFhirSearchQuery { Family = "SMITH" })).Ids.Should().Equal("M01", "M02");
        (await Search(new MemberFhirSearchQuery { Family = ".*" })).Ids.Should().Equal("M06");
        (await Search(new MemberFhirSearchQuery { Family = "(a+)+$" })).Ids.Should().BeEmpty();
    }

    [Fact]
    public async Task Given_matches_first_or_middle_name_and_name_matches_any_part()
    {
        (await Search(new MemberFhirSearchQuery { Given = "an" })).Ids.Should().Equal("M01", "M03");
        (await Search(new MemberFhirSearchQuery { Given = "lee" })).Ids.Should().Equal("M02");
        (await Search(new MemberFhirSearchQuery { Name = "gold" })).Ids.Should().Equal("M03");
        (await Search(new MemberFhirSearchQuery { Name = "an" })).Ids.Should().Equal("M01", "M03");
    }

    [Fact]
    public async Task Birth_date_range_is_inclusive_from_and_exclusive_to()
    {
        var oneDay = await Search(new MemberFhirSearchQuery
        {
            BirthDateFrom = new DateTime(1980, 5, 1),
            BirthDateTo = new DateTime(1980, 5, 2),
        });
        oneDay.Ids.Should().Equal("M01");

        var before1980 = await Search(new MemberFhirSearchQuery { BirthDateTo = new DateTime(1980, 1, 1) });
        before1980.Ids.Should().Equal("M04");

        var from1990 = await Search(new MemberFhirSearchQuery { BirthDateFrom = new DateTime(1990, 1, 1) });
        from1990.Ids.Should().Equal("M03", "M05");
    }

    [Fact]
    public async Task Gender_follows_the_projection_mapping()
    {
        (await Search(new MemberFhirSearchQuery { Gender = "female" })).Ids.Should().Equal("M01", "M03", "M06");
        (await Search(new MemberFhirSearchQuery { Gender = "male" })).Ids.Should().Equal("M02");
        (await Search(new MemberFhirSearchQuery { Gender = "unknown" })).Ids.Should().Equal("M04", "M05");
        (await Search(new MemberFhirSearchQuery { Gender = "other" })).Ids.Should().BeEmpty();
    }

    [Fact]
    public async Task Last_updated_range_filters_on_the_record_timestamp()
    {
        var (ids, _) = await Search(new MemberFhirSearchQuery
        {
            LastUpdatedFrom = Updated(2),
            LastUpdatedTo = Updated(4),
        });

        ids.Should().Equal("M02", "M03");
    }

    [Fact]
    public async Task Filters_combine_with_and()
    {
        var (ids, total) = await Search(new MemberFhirSearchQuery { Family = "jones", Gender = "unknown", Given = "e" });

        ids.Should().Equal("M05");
        total.Should().Be(1);
    }
}
