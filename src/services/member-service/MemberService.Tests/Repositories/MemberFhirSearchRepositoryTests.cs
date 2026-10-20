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

    // ── Subscriber (ADR 020): the family graph first, the legacy field as fallback ──

    private static Member Dependent(string memberId, string? legacySubscriber, string tenant = Tenant, bool draft = false)
    {
        var member = Member(memberId, "Kin", "Dee", null, new DateTime(2010, 1, 1), "F", Updated(9), tenant, draft);
#pragma warning disable CS0618
        member.SubscriberMemberId = legacySubscriber;
#pragma warning restore CS0618
        member.IsSubscriber = false;
        return member;
    }

    private static FamilyRelationship Edge(string subject, string related, string code = "19", DateTime? start = null,
        DateTime? end = null, bool deleted = false, string tenant = Tenant) => new()
    {
        TenantId = tenant,
        SubjectMemberId = subject,
        RelatedMemberId = related,
        RelationshipCode = code,
        PairId = Guid.NewGuid().ToString("N"),
        StartDate = start ?? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = end,
        DeletedAt = deleted ? DateTime.UtcNow : null,
    };

    private async Task SeedFamilyAsync()
    {
        await _db.GetCollection<Member>("Members").InsertManyAsync(new[]
        {
            Dependent("D01", legacySubscriber: null),      // graph only
            Dependent("D02", legacySubscriber: "M02"),     // legacy only
            Dependent("D03", legacySubscriber: "M04"),     // another subscriber
            Dependent("D04", legacySubscriber: "M02", tenant: "t2"),
            Dependent("D05", legacySubscriber: "M02", draft: true),
            Dependent("D06", legacySubscriber: null),      // ended edge
            Dependent("D07", legacySubscriber: null),      // deleted edge
            Dependent("D08", legacySubscriber: null),      // edge with a non-dependent code (18, self)
        });
        await _db.GetCollection<FamilyRelationship>(FamilyRelationshipRepositoryMongo.CollectionName).InsertManyAsync(new[]
        {
            Edge("D01", "M02"),
            Edge("D06", "M02", end: new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Edge("D07", "M02", deleted: true),
            Edge("D08", "M02", code: "18"),
            Edge("D01", "M02", tenant: "t2"),
        });
    }

    [Fact]
    public async Task SubscriberMemberId_finds_dependents_by_the_active_graph_and_by_the_legacy_field_in_the_tenant()
    {
        await SeedFamilyAsync();

        var (ids, total) = await Search(new MemberFhirSearchQuery { SubscriberMemberId = " M02 ", Limit = 100 });

        ids.Should().Equal("D01", "D02");
        total.Should().Be(2);
    }

    [Fact]
    public async Task Any_identifier_form_matches_every_spelling_that_normalizes_alike_and_nothing_else()
    {
        await _db.GetCollection<Member>("Members").InsertManyAsync(new[]
        {
            Member("AB12", "Lee", "A", null, new DateTime(1990, 1, 1), "F", Updated(1)),
            Member("ab-12", "Lee", "B", null, new DateTime(1990, 1, 1), "F", Updated(1)),
            Member("A B 1\u20112", "Lee", "C", null, new DateTime(1990, 1, 1), "F", Updated(1)),
            Member("AB123", "Lee", "D", null, new DateTime(1990, 1, 1), "F", Updated(1)),
            Member("XAB12", "Lee", "E", null, new DateTime(1990, 1, 1), "F", Updated(1)),
            Member("ab.12", "Lee", "F", null, new DateTime(1990, 1, 1), "F", Updated(1)),
            // Unicode spaces .NET trims, as an import might leave them.
            Member("\u00A0AB12\u3000", "Lee", "G", null, new DateTime(1990, 1, 1), "F", Updated(1)),
        });

        var (anyForm, _) = await Search(new MemberFhirSearchQuery { MemberId = "AB12", AnyIdentifierForm = true, Limit = 100 });
        var (exact, _) = await Search(new MemberFhirSearchQuery { MemberId = "AB12", Limit = 100 });

        anyForm.Should().BeEquivalentTo("AB12", "ab-12", "A B 1\u20112", "\u00A0AB12\u3000");
        exact.Should().Equal("AB12");
    }

    [Fact]
    public async Task Any_identifier_form_applies_to_the_subscriber_in_the_graph_and_the_legacy_field()
    {
        await SeedFamilyAsync();
        await _db.GetCollection<Member>("Members").InsertOneAsync(Dependent("D10", legacySubscriber: "m-02"));

        var (ids, _) = await Search(new MemberFhirSearchQuery { SubscriberMemberId = "M02", AnyIdentifierForm = true, Limit = 100 });

        ids.Should().Equal("D01", "D02", "D10");
    }

    [Fact]
    public void Any_form_of_nothing_but_separators_is_no_filter()
    {
        MemberFhirSearchRepository.AnyForm(" - ").Should().BeNull();
    }

    [Fact]
    public async Task Subscribers_prefer_the_graph_then_the_legacy_field_and_are_null_for_subscribers()
    {
        await SeedFamilyAsync();
        var members = _db.GetCollection<Member>("Members");
        // A dependent whose graph says M04 although its legacy field says M02:
        // the graph wins, so it is not M02's dependent.
        await members.InsertOneAsync(Dependent("D09", legacySubscriber: "M02"));
        await _db.GetCollection<FamilyRelationship>(FamilyRelationshipRepositoryMongo.CollectionName).InsertManyAsync(new[]
        {
            Edge("D09", "M04", start: new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Edge("D09", "M02", start: new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        });
        var page = await members.Find(m => m.TenantId == Tenant && (m.MemberId == "D01" || m.MemberId == "D02"
            || m.MemberId == "D06" || m.MemberId == "D09" || m.MemberId == "M01")).ToListAsync();
        page.Single(m => m.MemberId == "M01").IsSubscriber = true;

        var subscribers = await _repo.SubscribersAsync(Tenant, page);

        subscribers["D01"].Should().Be("M02", "the active graph edge");
        subscribers["D02"].Should().Be("M02", "no graph edge: the legacy value");
        subscribers["D06"].Should().BeNull("an ended edge and no legacy value");
        subscribers["D09"].Should().Be("M04", "the earliest active edge, as the member read derives it");
        subscribers["M01"].Should().BeNull("a subscriber has no subscriber");
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
