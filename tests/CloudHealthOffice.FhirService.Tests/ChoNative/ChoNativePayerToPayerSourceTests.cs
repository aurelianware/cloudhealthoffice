using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FhirService.Models;
using FhirService.Models.PayerToPayer;
using FhirService.Services.ChoNative;
using FhirService.Services.PayerToPayer;
using FhirService.Services.PayerToPayer.ChoNative;
using FhirService.Services.PayerToPayer.Outbound;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.FhirService.Tests.ChoNative;

/// <summary>
/// The CHO-native Payer-to-Payer sources and the directory under them (ADR 020):
/// candidate completeness and its bound, SSN-only requests, subscriber ids on
/// coverages, the export's lookback and order, paging, and failure.
/// </summary>
public class ChoNativePayerToPayerSourceTests
{
    private const string Tenant = "tenant-a";

    private sealed class Upstream : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Json(Page(0));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(Uri.UnescapeDataString(request.RequestUri!.PathAndQuery));
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class Clients(Upstream upstream) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(upstream, disposeHandler: false)
        {
            BaseAddress = new Uri("http://" + name.ToLowerInvariant() + "/"),
        };
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Member(string id, string family = "Jones", string dob = "1962-03-22", string? subscriber = null,
        string gender = "female") =>
        "{\"memberId\":\"" + id + "\"," + (subscriber is null ? "" : "\"subscriberMemberId\":\"" + subscriber + "\",") +
        "\"resource\":{\"resourceType\":\"Patient\",\"id\":\"" + id + "\",\"name\":[{\"family\":\"" + family +
        "\",\"given\":[\"Pat\",\"Q\"]}],\"gender\":\"" + gender + "\",\"birthDate\":\"" + dob +
        "\",\"telecom\":[{\"system\":\"phone\",\"value\":\"214-555-0100\"},{\"system\":\"email\",\"value\":\"p@example.com\"}]," +
        "\"address\":[{\"line\":[\"1 Main\"],\"city\":\"Dallas\",\"state\":\"TX\",\"postalCode\":\"75201\"}]}}";

    private static string Coverage(string id, string member) =>
        "{\"resource\":{\"resourceType\":\"Coverage\",\"id\":\"" + id + "\",\"status\":\"active\",\"beneficiary\":{\"reference\":\"Patient/" +
        member + "\"},\"period\":{\"start\":\"2024-01-01\",\"end\":\"2024-12-31\"}}}";

    private static string Eob(string id, string member, string? start) =>
        "{\"resource\":{\"resourceType\":\"ExplanationOfBenefit\",\"id\":\"" + id + "\",\"patient\":{\"reference\":\"Patient/" + member + "\"}" +
        (start is null ? "" : ",\"billablePeriod\":{\"start\":\"" + start + "\"}") + "}}";

    private static string Page(long total, params string[] items) =>
        "{\"total\":" + total + ",\"items\":[" + string.Join(",", items) + "]}";

    private static string Bundle(long total, params string[] entries) =>
        "{\"resourceType\":\"Bundle\",\"total\":" + total + ",\"entry\":[" + string.Join(",", entries) + "]}";

    private static (ChoNativeMemberDirectory Directory, Upstream Upstream) Directory(string? requestTenant = Tenant)
    {
        var upstream = new Upstream();
        var http = new DefaultHttpContext();
        if (requestTenant is not null) http.Items["TenantId"] = requestTenant;
        return (new ChoNativeMemberDirectory(new Clients(upstream), new HttpContextAccessor { HttpContext = http },
            NullLogger<ChoNativeMemberDirectory>.Instance), upstream);
    }

    private static ChoNativePayerToPayerMemberMatchSource MatchSource(ChoNativeMemberDirectory directory) =>
        new(directory, Options.Create(new FhirAdapterOptions { TenantId = Tenant }),
            Options.Create(new PayerToPayerDirectoryOptions { LocalPayerId = "THIS-PLAN" }));

    private static MemberMatchCriteria Criteria(string? memberId = null, string? dob = null, string? family = null, string? ssn = null) =>
        MemberMatchCriteria.From(new MemberMatchRequest
        {
            TenantId = Tenant, MemberId = memberId, BirthDate = dob, FamilyName = family, Ssn = ssn,
        });

    // ── Mapping ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("female", "F")]
    [InlineData("male", "M")]
    [InlineData("other", "O")]
    [InlineData("unknown", "U")]
    public void A_projected_patient_maps_to_the_member_the_policies_read_without_an_SSN(string gender, string expected)
    {
        var json = JsonNode.Parse(Member("M-1", gender: gender))!.AsObject();
        var member = ChoNativePayerToPayerMapping.ToChoMember(
            new DirectoryMember("M-1", null, json["resource"]!.AsObject()));

        member.Should().BeEquivalentTo(new ChoMember
        {
            MemberId = "M-1", FirstName = "Pat", MiddleName = "Q", LastName = "Jones", Dob = "1962-03-22", Gender = expected,
            Ssn = null, Phone = "214-555-0100", Email = "p@example.com",
            Address = new ChoAddress { Street1 = "1 Main", City = "Dallas", State = "TX", Zip = "75201" },
        });
    }

    // ── Member-match candidates ───────────────────────────────────────────────

    [Fact]
    public async Task An_SSN_only_request_is_not_searchable_and_nothing_is_read()
    {
        var (directory, upstream) = Directory();

        var candidates = await MatchSource(directory).GetCandidatesAsync(Tenant, Criteria(ssn: "123-45-6789", family: "Jones"));

        candidates.Searchable.Should().BeFalse();
        upstream.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Candidates_are_the_member_its_dependents_and_everyone_born_that_day_once_each()
    {
        var (directory, upstream) = Directory();
        upstream.Respond = r =>
        {
            var q = Uri.UnescapeDataString(r.RequestUri!.Query);
            if (q.Contains("subscriberMemberId=S-1")) return Json(Page(1, Member("D-1", subscriber: "S-1")));
            if (q.Contains("memberId=S-1")) return Json(Page(1, Member("S-1")));
            if (q.Contains("birthDateFrom=1962-03-22")) return Json(Page(2, Member("S-1"), Member("B-2")));
            return Json(Page(0));
        };

        var candidates = await MatchSource(directory).GetCandidatesAsync(Tenant, Criteria(memberId: "S-1", dob: "1962-03-22"));

        candidates.Searchable.Should().BeTrue();
        candidates.Complete.Should().BeTrue();
        candidates.Members.Select(m => m.MemberId).Should().Equal("B-2", "D-1", "S-1");
        upstream.Requests.Should().Contain(r => r.Contains("memberId=S1"), "the normalized id is searched as well as the supplied one");
    }

    [Fact]
    public async Task More_birth_date_candidates_than_the_bound_are_reported_incomplete()
    {
        var (directory, upstream) = Directory();
        var page = Enumerable.Range(0, ChoNativeMemberDirectory.PageSize).Select(i => Member($"M{i:D4}")).ToArray();
        upstream.Respond = r => Uri.UnescapeDataString(r.RequestUri!.Query).Contains("birthDateFrom")
            ? Json(Page(ChoNativePayerToPayerMemberMatchSource.MaxCandidates + 1, page))
            : Json(Page(0));

        var candidates = await MatchSource(directory).GetCandidatesAsync(Tenant, Criteria(dob: "1962-03-22", family: "Jones"));

        candidates.Complete.Should().BeFalse();
        upstream.Requests.Count(r => r.Contains("birthDateFrom")).Should().Be(
            ChoNativePayerToPayerMemberMatchSource.MaxCandidates / ChoNativeMemberDirectory.PageSize);
    }

    [Fact]
    public async Task A_dependents_coverage_carries_its_subscribers_id_and_the_plan_as_payer()
    {
        var (directory, upstream) = Directory();
        upstream.Respond = r =>
        {
            var q = Uri.UnescapeDataString(r.RequestUri!.Query);
            if (r.RequestUri!.AbsolutePath.Contains("coverage")) return Json(Page(1, Coverage("cov-1", "D-1")));
            if (q.Contains("subscriberMemberId=S-1")) return Json(Page(1, Member("D-1", subscriber: "S-1")));
            return Json(Page(0));
        };
        var source = MatchSource(directory);
        await source.GetCandidatesAsync(Tenant, Criteria(memberId: "S-1"));

        var coverage = (await source.GetCoveragesAsync(Tenant, "D-1")).Single();

        coverage.Should().BeEquivalentTo(new ChoCoverage
        {
            MemberId = "D-1", CoverageId = "cov-1", Status = "active", PayerId = "THIS-PLAN", SubscriberId = "S-1",
            PeriodStart = "2024-01-01", PeriodEnd = "2024-12-31",
        });
    }

    [Fact]
    public async Task The_match_service_answers_insufficient_or_ambiguous_from_what_the_source_can_prove()
    {
        var notSearchable = new PayerToPayerMemberMatchService(new FixedCandidates(MemberMatchCandidates.NotSearchable),
            NullLogger<PayerToPayerMemberMatchService>.Instance);
        var incomplete = new PayerToPayerMemberMatchService(new FixedCandidates(new MemberMatchCandidates([], Complete: false)),
            NullLogger<PayerToPayerMemberMatchService>.Instance);
        var request = new MemberMatchRequest { TenantId = Tenant, FamilyName = "Jones", BirthDate = "1962-03-22", Ssn = "123456789" };

        (await notSearchable.MatchAsync(request)).Outcome.Should().Be(MemberMatchOutcome.InsufficientCriteria);
        (await incomplete.MatchAsync(request)).Outcome.Should().Be(MemberMatchOutcome.AmbiguousMatch);
    }

    [Fact]
    public async Task Another_tenant_gets_no_candidates_and_nothing_is_read()
    {
        var (directory, upstream) = Directory();

        var candidates = await MatchSource(directory).GetCandidatesAsync("tenant-b", Criteria(memberId: "S-1"));

        candidates.Members.Should().BeEmpty();
        upstream.Requests.Should().BeEmpty();
    }

    // ── Export ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_export_is_patient_coverages_then_in_window_claims_in_id_order_as_projected()
    {
        var (directory, upstream) = Directory();
        upstream.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.Contains("members") => Json(Page(1, Member("M-1"))),
            var p when p.Contains("coverage") => Json(Page(2, Coverage("cov-b", "M-1"), Coverage("cov-a", "M-1"))),
            _ => Json(Bundle(4, Eob("e-2", "M-1", "2026-01-01"), Eob("e-1", "M-1", "2022-01-01"),
                Eob("e-old", "M-1", "2019-01-01"), Eob("e-undated", "M-1", null))),
        };
        var member = new ChoMember { MemberId = "M-1", FirstName = "Pat", LastName = "Jones", Dob = "1962-03-22", Gender = "F" };

        var resources = await new ChoNativePayerToPayerExportSource(directory).GetResourcesAsync(member,
            new PayerToPayerExchangeRequest { TenantId = Tenant, ExchangeDateUtc = new DateTime(2026, 9, 1), LookbackYears = 5 });

        resources.Select(r => $"{r.ResourceType}/{r.Id}").Should().Equal(
            "Patient/M-1", "Coverage/cov-a", "Coverage/cov-b", "ExplanationOfBenefit/e-1", "ExplanationOfBenefit/e-2");
        resources.Should().AllBeOfType<FhirJsonResource>();
    }

    [Fact]
    public async Task Claims_are_read_page_by_page_until_the_total()
    {
        var (directory, upstream) = Directory();
        upstream.Respond = r =>
        {
            var page = Uri.UnescapeDataString(r.RequestUri!.Query).Contains("_page=1") ? "a" : "b";
            var entries = Enumerable.Range(0, ChoNativeMemberDirectory.EobPageSize).Select(i => Eob($"{page}{i}", "M-1", "2026-01-01")).ToArray();
            return Json(Bundle(ChoNativeMemberDirectory.EobPageSize + 1, page == "a" ? entries : [Eob("last", "M-1", "2026-01-01")]));
        };

        var eobs = await directory.GetExplanationsOfBenefitAsync(Tenant, "M-1");

        eobs.Should().HaveCount(ChoNativeMemberDirectory.EobPageSize + 1);
        upstream.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, null)]
    [InlineData(HttpStatusCode.NotFound, null)]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, "{\"items\":[]}")]
    public async Task A_failed_or_unreadable_answer_is_an_upstream_failure_never_none(HttpStatusCode status, string? body)
    {
        var (directory, upstream) = Directory();
        upstream.Respond = _ => body is null ? new HttpResponseMessage(status) : Json(body, status);

        var act = () => directory.FindByMemberIdAsync(Tenant, "M-1");

        await act.Should().ThrowAsync<FhirUpstreamException>();
    }

    [Fact]
    public async Task A_tenant_argument_other_than_the_requests_is_refused()
    {
        var (directory, upstream) = Directory(requestTenant: "tenant-a");

        var act = () => directory.GetCoveragesAsync("tenant-b", "M-1");

        await act.Should().ThrowAsync<InvalidOperationException>();
        upstream.Requests.Should().BeEmpty();
    }

    private sealed class FixedCandidates(MemberMatchCandidates candidates) : IPayerToPayerMemberMatchSource
    {
        public string ServedTenantId => Tenant;
        public Task<MemberMatchCandidates> GetCandidatesAsync(string tenantId, MemberMatchCriteria criteria, CancellationToken ct = default) =>
            Task.FromResult(candidates);
        public Task<IReadOnlyList<ChoCoverage>> GetCoveragesAsync(string tenantId, string memberId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ChoCoverage>>([]);
    }
}
