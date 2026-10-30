using ClaimsService.Fhir;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using MongoDB.Driver;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

/// <summary>
/// The FHIR Claim search query against a real mongod (ADR 024). What is worth
/// proving on the store: tenant isolation, that only current chain heads come
/// back (the FHIR id is the head, so a superseded version would answer with a
/// resource whose own id reads back as something else), that the status filter
/// is the exact inverse of what the projection emits, and that a legacy row
/// with no chain id is still findable by the id the projection gives it.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ClaimFhirSearchRepositoryTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-claim-fhir";
    private const string OtherTenant = "tenant-claim-fhir-other";
    private const string Member = "MEM-1";

    private IMongoDatabase _database = null!;
    private ClaimFhirSearchRepositoryMongo _repository = null!;

    public async Task InitializeAsync()
    {
        _database = mongo.CreateDatabase("claim_fhir_search");
        _repository = new ClaimFhirSearchRepositoryMongo(_database);
        await _database.GetCollection<Claim>("Claims").InsertManyAsync(Seed());
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_database);

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static Claim Claim(string id, string tenant, string member, DateTime submitted) => new()
    {
        Id = id,
        ClaimVersionId = id,
        TenantId = tenant,
        ClaimNumber = "CLM-" + id,
        MemberId = member,
        Status = ClaimStatus.Paid,
        VersionState = ClaimVersionState.Paid,
        BillingProviderNPI = "1234567893",
        TotalChargeAmount = 100m,
        ServiceDateFrom = submitted,
        ServiceDateTo = submitted,
        SubmittedDate = submitted,
        LastUpdatedDate = submitted,
    };

    private static IEnumerable<Claim> Seed()
    {
        yield return Claim("C1", Tenant, Member, Day(1, 10));
        yield return Claim("C2", Tenant, Member, Day(2, 10));
        yield return Claim("C3", Tenant, "MEM-2", Day(2, 10));

        var voided = Claim("C4", Tenant, Member, Day(3, 10));
        voided.Status = ClaimStatus.Voided;
        yield return voided;

        // History: superseded by an adjustment, and a draft. Neither is a
        // current version, so neither is a FHIR resource.
        var superseded = Claim("C5", Tenant, Member, Day(4, 10));
        superseded.SupersededAt = Day(4, 20);
        yield return superseded;

        var draft = Claim("C6", Tenant, Member, Day(5, 10));
        draft.VersionState = ClaimVersionState.Draft;
        yield return draft;

        // A legacy row predating the version chain: no ClaimVersionId, and
        // VersionState Unknown. The projection gives it its own Id.
        var legacy = Claim("C7", Tenant, Member, Day(6, 10));
        legacy.ClaimVersionId = string.Empty;
        legacy.VersionState = ClaimVersionState.Unknown;
        yield return legacy;

        yield return Claim("C8", OtherTenant, Member, Day(1, 10));
    }

    private async Task<string[]> Ids(ClaimFhirSearchQuery query)
    {
        var (items, _) = await _repository.SearchAsync(Tenant, query);
        return items.Select(c => c.Id).ToArray();
    }

    [Fact]
    public async Task A_search_never_leaves_its_tenant()
    {
        var ids = await Ids(new ClaimFhirSearchQuery { MemberId = Member, Limit = 50 });

        ids.Should().NotContain("C8");
        var (_, otherTotal) = await _repository.SearchAsync(
            OtherTenant, new ClaimFhirSearchQuery { MemberId = Member, Limit = 50 });
        otherTotal.Should().Be(1);
    }

    [Fact]
    public async Task Only_current_chain_heads_are_returned()
    {
        var ids = await Ids(new ClaimFhirSearchQuery { MemberId = Member, Limit = 50 });

        ids.Should().NotContain("C5", "a superseded version is history");
        ids.Should().NotContain("C6", "a draft is not a claim yet");
        ids.Should().BeEquivalentTo(["C1", "C2", "C4", "C7"]);
    }

    [Fact]
    public async Task A_legacy_row_is_found_by_the_id_the_projection_gives_it()
    {
        // The projection falls back to Id when there is no chain id, so an
        // _id search for that value has to find the row.
        var ids = await Ids(new ClaimFhirSearchQuery { ClaimVersionId = "C7", Limit = 50 });

        ids.Should().Equal("C7");
    }

    [Fact]
    public async Task Active_is_every_claim_that_was_not_voided()
    {
        var ids = await Ids(new ClaimFhirSearchQuery
        {
            MemberId = Member,
            Status = FhirClaimStatusCodes.Active,
            Limit = 50,
        });

        ids.Should().BeEquivalentTo(["C1", "C2", "C7"]);
    }

    [Fact]
    public async Task Cancelled_is_the_voided_claim()
    {
        var ids = await Ids(new ClaimFhirSearchQuery
        {
            MemberId = Member,
            Status = FhirClaimStatusCodes.Cancelled,
            Limit = 50,
        });

        ids.Should().Equal("C4");
    }

    [Fact]
    public async Task The_status_filter_is_the_exact_inverse_of_what_the_projection_emits()
    {
        foreach (var status in new[] { FhirClaimStatusCodes.Active, FhirClaimStatusCodes.Cancelled })
        {
            var (items, _) = await _repository.SearchAsync(
                Tenant, new ClaimFhirSearchQuery { Status = status, Limit = 50 });

            items.Should().NotBeEmpty();
            items.Select(c => ClaimFhirProjector.StatusOf(c.Status))
                .Should().AllBe(status, $"every record the filter returned for {status} must project as it");
        }
    }

    [Fact]
    public async Task A_status_outside_the_projected_set_matches_nothing()
    {
        var (items, total) = await _repository.SearchAsync(
            Tenant, new ClaimFhirSearchQuery { Status = "entered-in-error", Limit = 50 });

        items.Should().BeEmpty();
        total.Should().Be(0);
    }

    [Fact]
    public async Task A_created_window_filters_on_submission_and_excludes_its_upper_bound()
    {
        var ids = await Ids(new ClaimFhirSearchQuery
        {
            MemberId = Member,
            CreatedFrom = Day(1, 10),
            CreatedTo = Day(2, 10),
            Limit = 50,
        });

        ids.Should().Equal(["C1"], "the upper bound is exclusive, so the 10 February claim is out");
    }

    [Fact]
    public async Task The_total_counts_every_match_across_pages_and_the_order_is_stable()
    {
        var (firstPage, total) = await _repository.SearchAsync(
            Tenant, new ClaimFhirSearchQuery { MemberId = Member, Limit = 2 });
        var (secondPage, _) = await _repository.SearchAsync(
            Tenant, new ClaimFhirSearchQuery { MemberId = Member, Offset = 2, Limit = 2 });

        total.Should().Be(4);
        // Newest submission first.
        firstPage.Select(c => c.Id).Should().Equal("C7", "C4");
        secondPage.Select(c => c.Id).Should().Equal("C2", "C1");
    }

    // ── ExplanationOfBenefit filters (CARIN BB nightly 2026-10-30) ───────────

    [Fact]
    public async Task Claim_types_match_any_of_them_and_the_claim_number_matches_exactly()
    {
        var dental = Claim("D1", Tenant, Member, Day(7, 1));
        dental.ClaimType = ClaimType.Dental;
        await _database.GetCollection<Claim>("Claims").InsertOneAsync(dental);

        (await Ids(new ClaimFhirSearchQuery { MemberId = Member, ClaimTypes = [ClaimType.Dental], Limit = 50 }))
            .Should().Equal("D1");
        (await Ids(new ClaimFhirSearchQuery { MemberId = Member, ClaimTypes = [ClaimType.Dental, ClaimType.Institutional], Limit = 50 }))
            .Should().Equal("D1");
        (await Ids(new ClaimFhirSearchQuery { MemberId = Member, ClaimNumber = "CLM-C2", Limit = 50 }))
            .Should().Equal("C2");
    }

    [Fact]
    public async Task A_service_window_matches_a_claim_whose_service_days_overlap_it()
    {
        var span = Claim("S1", Tenant, Member, Day(8, 1));
        span.ServiceDateTo = Day(8, 5);
        await _database.GetCollection<Claim>("Claims").InsertOneAsync(span);

        // A window opening a second into the claim's last day still overlaps it.
        (await Ids(new ClaimFhirSearchQuery { MemberId = Member, ServiceFrom = Day(8, 5).AddSeconds(1), Limit = 50 }))
            .Should().Equal("S1");
        (await Ids(new ClaimFhirSearchQuery { MemberId = Member, ServiceFrom = Day(8, 6), Limit = 50 }))
            .Should().BeEmpty();
        (await Ids(new ClaimFhirSearchQuery { MemberId = Member, ServiceFrom = Day(7, 31), ServiceTo = Day(8, 2), Limit = 50 }))
            .Should().Equal("S1");
    }

    [Fact]
    public async Task A_service_start_window_matches_the_first_day_of_service_only()
    {
        var span = Claim("S2", Tenant, Member, Day(9, 1));
        span.ServiceDateTo = Day(9, 5);
        await _database.GetCollection<Claim>("Claims").InsertOneAsync(span);

        (await Ids(new ClaimFhirSearchQuery { MemberId = Member, ServiceStartFrom = Day(9, 1), ServiceStartTo = Day(9, 2), Limit = 50 }))
            .Should().Equal("S2");
        (await Ids(new ClaimFhirSearchQuery { MemberId = Member, ServiceStartFrom = Day(9, 3), Limit = 50 }))
            .Should().BeEmpty("the claim's service ran to the 5th but started on the 1st");
    }
}
