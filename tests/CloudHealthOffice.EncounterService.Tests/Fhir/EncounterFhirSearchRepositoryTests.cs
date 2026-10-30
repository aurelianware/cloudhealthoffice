using CloudHealthOffice.Testing.Mongo;
using EncounterService.Fhir;
using EncounterService.Models;
using EncounterService.Repositories;
using MongoDB.Driver;

namespace CloudHealthOffice.EncounterService.Tests.Fhir;

/// <summary>
/// The FHIR Encounter search query against a real mongod (ADR 024). The parts
/// worth proving on the store rather than in a fake: tenant isolation, period
/// overlap rather than containment, and the status filter being the exact
/// inverse of what the projection emits — a filter that disagreed with the
/// projector would answer records whose status is not the one asked for.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class EncounterFhirSearchRepositoryTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-enc-fhir";
    private const string OtherTenant = "tenant-enc-fhir-other";
    private const string Member = "MEM-1";

    private IMongoDatabase _db = null!;
    private EncounterFhirSearchRepositoryMongo _repository = null!;

    public async Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("enc_fhir_search");
        _repository = new EncounterFhirSearchRepositoryMongo(_db);
        await _db.GetCollection<Encounter>("encounters").InsertManyAsync(Seed());
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static Encounter Encounter(
        string id, string tenant, string member, DateTime from, DateTime to,
        EncounterType type = EncounterType.Professional) => new()
    {
        Id = id,
        TenantId = tenant,
        MemberId = member,
        EncounterControlNumber = "ECN-" + id,
        ClaimId = "C-" + id,
        BillingProviderNPI = "1234567893",
        PayerId = "PAYER-1",
        EncounterType = type,
        ServiceDateFrom = from,
        ServiceDateTo = to,
        LastUpdatedDate = from,
        TotalChargeAmount = 100m,
    };

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static IEnumerable<Encounter> Seed()
    {
        yield return Encounter("E1", Tenant, Member, Day(1, 10), Day(1, 12));
        var inpatient = Encounter("E2", Tenant, Member, Day(3, 1), Day(3, 1), EncounterType.Institutional);
        inpatient.PlaceOfServiceCode = "21";
        yield return inpatient;
        yield return Encounter("E3", Tenant, "MEM-2", Day(3, 1), Day(3, 1));

        var voidedBySubmissionType = Encounter("E4", Tenant, Member, Day(4, 1), Day(4, 1));
        voidedBySubmissionType.SubmissionType = SubmissionType.Void;
        yield return voidedBySubmissionType;

        var voidedByStatus = Encounter("E5", Tenant, Member, Day(4, 2), Day(4, 2));
        voidedByStatus.Status = EncounterStatus.Voided;
        yield return voidedByStatus;

        var voidedByFrequencyCode = Encounter("E6", Tenant, Member, Day(4, 3), Day(4, 3));
        voidedByFrequencyCode.ClaimFrequencyCode = "8";
        yield return voidedByFrequencyCode;

        // Another tenant's encounter for the same member id.
        yield return Encounter("E7", OtherTenant, Member, Day(1, 10), Day(1, 12));
    }

    private async Task<string[]> Ids(EncounterFhirSearchQuery query)
    {
        var (items, total) = await _repository.SearchAsync(Tenant, query);
        total.Should().Be(items.Count, "these fixtures all fit on one page");
        return items.Select(e => e.Id).ToArray();
    }

    [Fact]
    public async Task A_search_never_leaves_its_tenant()
    {
        var ids = await Ids(new EncounterFhirSearchQuery { MemberId = Member, Limit = 50 });

        ids.Should().NotContain("E7");
        var (_, otherTotal) = await _repository.SearchAsync(OtherTenant, new EncounterFhirSearchQuery { MemberId = Member, Limit = 50 });
        otherTotal.Should().Be(1, "the other tenant sees only its own");
    }

    [Fact]
    public async Task A_date_filter_matches_an_overlapping_period_not_only_a_contained_one()
    {
        // E1 runs 10–12 January. A window starting on the 11th overlaps it,
        // which is what FHIR date search on a period means.
        var ids = await Ids(new EncounterFhirSearchQuery
        {
            MemberId = Member,
            ServiceDateFrom = Day(1, 11),
            ServiceDateTo = Day(1, 12),
            Limit = 50,
        });

        ids.Should().Equal("E1");
    }

    [Fact]
    public async Task Finished_is_every_encounter_that_was_not_voided()
    {
        var ids = await Ids(new EncounterFhirSearchQuery
        {
            MemberId = Member,
            Status = FhirEncounterStatusCodes.Finished,
            Limit = 50,
        });

        ids.Should().BeEquivalentTo(["E1", "E2"]);
    }

    [Fact]
    public async Task Entered_in_error_is_every_void_however_it_was_recorded()
    {
        var ids = await Ids(new EncounterFhirSearchQuery
        {
            MemberId = Member,
            Status = FhirEncounterStatusCodes.EnteredInError,
            Limit = 50,
        });

        ids.Should().BeEquivalentTo(["E4", "E5", "E6"]);
    }

    [Fact]
    public async Task The_status_filter_is_the_exact_inverse_of_what_the_projection_emits()
    {
        // Whatever the filter returns for a status, the projector must agree
        // on every one of those records — otherwise a caller gets a resource
        // whose status is not the one it searched for.
        foreach (var status in new[] { FhirEncounterStatusCodes.Finished, FhirEncounterStatusCodes.EnteredInError })
        {
            var (items, _) = await _repository.SearchAsync(
                Tenant, new EncounterFhirSearchQuery { Status = status, Limit = 50 });

            items.Should().NotBeEmpty();
            items.Select(EncounterFhirProjector.StatusOf)
                .Should().AllBe(status, $"every record the filter returned for {status} must project as it");
        }
    }

    [Fact]
    public async Task A_status_outside_the_projected_set_matches_nothing()
    {
        var (items, total) = await _repository.SearchAsync(
            Tenant, new EncounterFhirSearchQuery { Status = "planned", Limit = 50 });

        items.Should().BeEmpty();
        total.Should().Be(0);
    }

    [Fact]
    public async Task An_encounter_type_filter_selects_that_transaction_variant()
    {
        var ids = await Ids(new EncounterFhirSearchQuery
        {
            MemberId = Member,
            EncounterType = EncounterType.Institutional,
            Limit = 50,
        });

        ids.Should().Equal("E2");
    }

    [Fact]
    public async Task The_total_counts_every_match_across_pages_and_the_order_is_stable()
    {
        var (firstPage, total) = await _repository.SearchAsync(
            Tenant, new EncounterFhirSearchQuery { MemberId = Member, Limit = 2 });
        var (secondPage, _) = await _repository.SearchAsync(
            Tenant, new EncounterFhirSearchQuery { MemberId = Member, Offset = 2, Limit = 2 });

        total.Should().Be(5, "the member has five encounters in this tenant");
        firstPage.Should().HaveCount(2);
        // Newest service start first, so the April encounters lead.
        firstPage.Select(e => e.Id).Should().Equal("E6", "E5");
        secondPage.Select(e => e.Id).Should().Equal("E4", "E2");
    }

    [Fact]
    public async Task A_zero_page_still_reports_the_total()
    {
        var (items, total) = await _repository.SearchAsync(
            Tenant, new EncounterFhirSearchQuery { MemberId = Member, Limit = 0 });

        items.Should().BeEmpty();
        total.Should().Be(5);
    }

    [Fact]
    public async Task An_identifier_filter_selects_the_encounter_with_that_control_number()
    {
        var ids = await Ids(new EncounterFhirSearchQuery { ControlNumber = "ECN-E1", Limit = 50 });

        ids.Should().Equal("E1");
    }

    [Fact]
    public async Task A_class_filter_selects_by_the_place_of_service_the_class_is_derived_from()
    {
        (await Ids(new EncounterFhirSearchQuery { MemberId = Member, Classes = ["IMP"], Limit = 50 }))
            .Should().Equal("E2");
        (await Ids(new EncounterFhirSearchQuery { MemberId = Member, Classes = ["AMB"], Limit = 50 }))
            .Should().BeEquivalentTo(["E1", "E4", "E5", "E6"]);
        (await Ids(new EncounterFhirSearchQuery { MemberId = Member, Classes = ["AMB", "IMP"], Limit = 50 }))
            .Should().HaveCount(5, "class codes OR");
    }

    [Fact]
    public async Task The_class_filter_is_the_exact_inverse_of_what_the_projection_emits()
    {
        var projector = new EncounterFhirProjector();
        foreach (var code in new[] { "AMB", "IMP" })
        {
            var (items, _) = await _repository.SearchAsync(
                Tenant, new EncounterFhirSearchQuery { Classes = [code], Limit = 50 });

            items.Should().NotBeEmpty();
            items.Select(e => projector.Project(e)["class"]!["code"]!.GetValue<string>())
                .Should().AllBe(code, $"every record the filter returned for {code} must project as it");
        }
    }

    [Fact]
    public async Task A_class_the_projection_never_emits_matches_nothing()
    {
        var (items, total) = await _repository.SearchAsync(
            Tenant, new EncounterFhirSearchQuery { Classes = ["VR"], Limit = 50 });

        items.Should().BeEmpty();
        total.Should().Be(0);
    }
}
