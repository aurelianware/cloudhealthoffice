using System.Net;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Security;
using MemberService.Models;
using MemberService.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MemberService.Tests.Integration;

/// <summary>
/// The FHIR Patient projection endpoints through the real pipeline (ADR 018):
/// search returns projected Patients whose id is the member id, runs in the
/// caller's tenant with the filters it was given, needs members:read, rejects
/// out-of-range input, and logs the members it returned; the read hides drafts.
/// Query semantics against real MongoDB are in MemberFhirSearchRepositoryTests.
/// </summary>
public class MemberFhirPatientSearchPipelineTests : IClassFixture<MemberFhirSmokeTests.Factory>
{
    private const string Tenant = "tenant-fhir-search";

    private readonly MemberFhirSmokeTests.Factory _factory;
    private readonly RecordingMemberFhirSearchRepository _search;
    private readonly RecordAccessStoreInMemory _log = new();
    private readonly WebApplicationFactory<Program> _host;

    public MemberFhirPatientSearchPipelineTests(MemberFhirSmokeTests.Factory factory)
    {
        _factory = factory;
        // The factory registers its recording repository last, so tests use that
        // instance; xUnit runs a class's tests one at a time, so reset it per test.
        _search = factory.FhirSearch;
        _search.Members.Clear();
        _search.Calls.Clear();
        _search.Members.Add(Member("FS-002", "Bravo"));
        _search.Members.Add(Member("FS-001", "Alpha"));
        _search.Members.Add(Member("FS-900", "Other", tenant: "another-tenant"));
        _host = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IRecordAccessStore>();
            services.AddSingleton<IRecordAccessStore>(_log);
        }));
    }

    private static Member Member(string memberId, string lastName, string tenant = Tenant, bool draft = false) => new()
    {
        TenantId = tenant,
        Id = "doc-" + memberId,
        MemberId = memberId,
        FirstName = "Pat",
        LastName = lastName,
        Gender = "F",
        DateOfBirth = new DateTime(1980, 2, 3, 0, 0, 0, DateTimeKind.Utc),
        EffectiveDate = new DateTime(2024, 1, 1),
        GroupNumber = "GRP",
        IsSubscriber = true,
        IsDraft = draft,
    };

    private HttpClient As(string role, string user = "")
    {
        var client = _host.CreateDefaultClient(new ChoDevelopmentTokenHandler(user == "" ? "user-" + role : user, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private static async Task<JsonObject> Json(HttpResponseMessage response)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    [Fact]
    public async Task Search_returns_projected_patients_whose_id_is_the_member_id_with_the_total()
    {
        var response = await As(ChoRolePermissions.MemberServices).GetAsync("/api/v1/members/fhir/Patient");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body["total"]!.GetValue<long>().Should().Be(2);
        var items = body["items"]!.AsArray();
        items.Select(i => i!["memberId"]!.ToString()).Should().Equal("FS-001", "FS-002");
        var patient = items[0]!["resource"]!.AsObject();
        patient["resourceType"]!.ToString().Should().Be("Patient");
        patient["id"]!.ToString().Should().Be("FS-001");
        patient.ToJsonString().Should().NotContain("doc-FS-001", "the internal document id is never exposed");
    }

    [Fact]
    public async Task Search_passes_the_subscriber_filter_and_returns_each_members_subscriber()
    {
        var dependent = Member("FS-003", "Charlie");
        dependent.IsSubscriber = false;
#pragma warning disable CS0618
        dependent.SubscriberMemberId = "FS-001";
#pragma warning restore CS0618
        _search.Members.Add(dependent);

        var response = await As(ChoRolePermissions.MemberServices)
            .GetAsync("/api/v1/members/fhir/Patient?subscriberMemberId=FS-001&limit=50");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _search.Calls.Single().Query.SubscriberMemberId.Should().Be("FS-001");
        var items = (await Json(response))["items"]!.AsArray();
        items.Single(i => i!["memberId"]!.ToString() == "FS-003")!["subscriberMemberId"]!.ToString().Should().Be("FS-001");
        items.Single(i => i!["memberId"]!.ToString() == "FS-001")!["subscriberMemberId"].Should().BeNull();
    }

    [Fact]
    public async Task Search_runs_in_the_callers_tenant_with_the_filters_it_was_given()
    {
        var response = await As(ChoRolePermissions.MemberServices).GetAsync(
            "/api/v1/members/fhir/Patient?memberId=FS-001&family=alp&given=pa&name=al&gender=female"
            + "&birthDateFrom=1980-01-01&birthDateTo=1981-01-01"
            + "&lastUpdatedFrom=2026-01-01T00:00:00Z&lastUpdatedTo=2026-02-01T00:00:00Z&offset=5&limit=7");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (tenant, query) = _search.Calls.Should().ContainSingle().Subject;
        tenant.Should().Be(Tenant);
        query.MemberId.Should().Be("FS-001");
        query.Family.Should().Be("alp");
        query.Given.Should().Be("pa");
        query.Name.Should().Be("al");
        query.Gender.Should().Be("female");
        query.BirthDateFrom.Should().Be(new DateTime(1980, 1, 1));
        query.BirthDateTo.Should().Be(new DateTime(1981, 1, 1));
        query.LastUpdatedFrom!.Value.ToUniversalTime().Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        query.LastUpdatedTo!.Value.ToUniversalTime().Should().Be(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        query.Offset.Should().Be(5);
        query.Limit.Should().Be(7);
    }

    [Fact]
    public async Task Search_needs_members_read()
    {
        var response = await As(ChoRolePermissions.Finance).GetAsync("/api/v1/members/fhir/Patient");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _search.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("gender=nonbinary")]
    [InlineData("limit=101")]
    [InlineData("offset=-1")]
    [InlineData("offset=10001")]
    [InlineData("birthDateFrom=not-a-date")]
    public async Task Out_of_range_input_is_rejected_before_any_query(string query)
    {
        var response = await As(ChoRolePermissions.MemberServices).GetAsync("/api/v1/members/fhir/Patient?" + query);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _search.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_logs_the_members_it_returned()
    {
        var response = await As(ChoRolePermissions.MemberServices, "csr-fhir").GetAsync("/api/v1/members/fhir/Patient?family=Al");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = _log.All.Should().ContainSingle(x => x.ActorId == "csr-fhir").Subject;
        e.Action.Should().Be(RecordAccessActions.Search);
        e.RecordIds.Should().BeEquivalentTo("FS-001", "FS-002");
        e.Route.Should().NotContain("Al", "search terms are not written to the access log");
    }

    [Fact]
    public async Task Read_projects_the_member_with_the_member_id_and_hides_drafts()
    {
        _factory.MemberRepo.Members.RemoveAll(m => m.TenantId == Tenant);
        _factory.MemberRepo.Members.Add(Member("FR-001", "Reader"));
        _factory.MemberRepo.Members.Add(Member("FR-DRAFT", "Drafty", draft: true));
        var client = As(ChoRolePermissions.MemberServices);

        var read = await client.GetAsync("/api/v1/members/FR-001/fhir");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(read))["id"]!.ToString().Should().Be("FR-001");

        (await client.GetAsync("/api/v1/members/FR-DRAFT/fhir")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
