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
    public async Task With_a_member_id_candidates_are_the_members_bearing_it_in_any_form_and_birth_date_is_not_read()
    {
        var (directory, upstream) = Directory();
        upstream.Respond = r =>
        {
            var q = Uri.UnescapeDataString(r.RequestUri!.Query);
            if (q.Contains("subscriberMemberId=s-1"))
                return Json(Page(2, Member("D-1", subscriber: "S-1"), Member("D-9", subscriber: "OTHER")));
            if (q.Contains("memberId=s-1")) return Json(Page(2, Member("S-1"), Member("S1")));
            return Json(Page(1, Member("B-2")));
        };

        var candidates = await MatchSource(directory).GetCandidatesAsync(Tenant, Criteria(memberId: "s-1", dob: "1962-03-22"));

        candidates.Searchable.Should().BeTrue();
        candidates.Complete.Should().BeTrue();
        candidates.SsnVerifiable.Should().BeFalse();
        // D-9 matched the subscriber filter on another family edge, but its
        // derived subscriber does not bear the id: the policy would rule it out.
        candidates.Members.Select(m => m.MemberId).Should().Equal("D-1", "S-1", "S1");
        upstream.Requests.Should().OnlyContain(r => r.Contains("anyIdentifierForm=true"));
        upstream.Requests.Should().NotContain(r => r.Contains("birthDate"));
    }

    [Fact]
    public async Task Without_a_member_id_candidates_are_everyone_born_that_day()
    {
        var (directory, upstream) = Directory();
        upstream.Respond = r => Uri.UnescapeDataString(r.RequestUri!.Query).Contains("birthDateFrom=1962-03-22&birthDateTo=1962-03-23")
            ? Json(Page(2, Member("B-2"), Member("A-1")))
            : Json(Page(0));

        var candidates = await MatchSource(directory).GetCandidatesAsync(Tenant, Criteria(dob: "1962-03-22", family: "Jones"));

        candidates.Members.Select(m => m.MemberId).Should().Equal("A-1", "B-2");
        upstream.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Member_paging_that_changes_under_the_read_is_reported_incomplete()
    {
        var full = Enumerable.Range(0, ChoNativeMemberDirectory.PageSize).Select(i => Member($"M{i:D4}")).ToArray();

        var (moving, movingUpstream) = Directory();
        movingUpstream.Respond = r => Uri.UnescapeDataString(r.RequestUri!.Query).Contains("offset=0")
            ? Json(Page(150, full))
            : Json(Page(149, Member("Z-1")));
        (await moving.FindByBirthDateAsync(Tenant, new DateOnly(1962, 3, 22), 500)).Complete.Should().BeFalse();

        var (shortened, shortUpstream) = Directory();
        shortUpstream.Respond = r => Uri.UnescapeDataString(r.RequestUri!.Query).Contains("offset=0")
            ? Json(Page(150, full))
            : Json(Page(150));
        (await shortened.FindByBirthDateAsync(Tenant, new DateOnly(1962, 3, 22), 500)).Complete.Should().BeFalse();
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
    public async Task A_supplied_SSN_the_candidates_cannot_verify_is_never_ignored()
    {
        var jones = new ChoMember { MemberId = "M-1", FirstName = "Pat", LastName = "Jones", Dob = "1962-03-22", Gender = "F" };
        var unverifiable = new PayerToPayerMemberMatchService(
            new FixedCandidates(new MemberMatchCandidates([jones], SsnVerifiable: false)),
            NullLogger<PayerToPayerMemberMatchService>.Instance);
        var withSsn = new MemberMatchRequest { TenantId = Tenant, FamilyName = "Jones", BirthDate = "1962-03-22", Ssn = "123456789" };
        var withoutSsn = new MemberMatchRequest { TenantId = Tenant, FamilyName = "Jones", BirthDate = "1962-03-22" };

        // The SSN may belong to someone else who shares the name and birth date.
        (await unverifiable.MatchAsync(withSsn)).Outcome.Should().Be(MemberMatchOutcome.AmbiguousMatch);
        (await unverifiable.MatchAsync(withoutSsn)).Outcome.Should().NotBe(MemberMatchOutcome.AmbiguousMatch);
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
    public async Task Exported_claims_carry_no_remittance_or_cost_sharing()
    {
        var (directory, upstream) = Directory();
        const string money = "{\"value\":10,\"currency\":\"USD\"}";
        const string adjudication = "[{\"category\":{\"coding\":[{\"code\":\"copay\"}]},\"amount\":" + money +
                                    ",\"reason\":{\"coding\":[{\"code\":\"CO-45\"}]}}]";
        var eob = "{\"resource\":{\"resourceType\":\"ExplanationOfBenefit\",\"id\":\"e-1\",\"patient\":{\"reference\":\"Patient/M-1\"}," +
                  "\"billablePeriod\":{\"start\":\"2026-01-01\"},\"total\":[{\"amount\":" + money + "}],\"payment\":{\"amount\":" + money + "}," +
                  "\"payee\":{\"type\":{\"text\":\"provider\"}},\"adjudication\":" + adjudication + ",\"benefitBalance\":[{}],\"processNote\":[{\"text\":\"n\"}]," +
                  "\"addItem\":[{\"adjudication\":" + adjudication + "}]," +
                  "\"item\":[{\"sequence\":1,\"productOrService\":{\"coding\":[{\"code\":\"99213\"}]},\"unitPrice\":" + money + ",\"net\":" + money +
                  ",\"adjudication\":" + adjudication + ",\"detail\":[{\"sequence\":1,\"net\":" + money + ",\"adjudication\":" + adjudication +
                  ",\"subDetail\":[{\"sequence\":1,\"adjudication\":" + adjudication + "}]}]}]}}";
        upstream.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.Contains("members") => Json(Page(1, Member("M-1"))),
            var p when p.Contains("coverage") => Json(Page(0)),
            _ => Json(Bundle(1, eob)),
        };
        var member = new ChoMember { MemberId = "M-1", FirstName = "Pat", LastName = "Jones", Dob = "1962-03-22", Gender = "F" };

        var resources = await new ChoNativePayerToPayerExportSource(directory).GetResourcesAsync(member,
            new PayerToPayerExchangeRequest { TenantId = Tenant, ExchangeDateUtc = new DateTime(2026, 9, 1), LookbackYears = 5 });

        var resource = resources.Single(r => r.ResourceType == "ExplanationOfBenefit");
        var exported = System.Text.Json.JsonSerializer.Serialize(resource, resource.GetType());
        exported.Should().NotContainAny("\"total\"", "\"payment\"", "\"payee\"", "\"adjudication\"", "\"benefitBalance\"",
            "\"processNote\"", "\"addItem\"", "\"unitPrice\"", "\"net\"", "CO-45", "USD");
        exported.Should().Contain("99213").And.Contain("\"subDetail\"").And.Contain("2026-01-01");
    }

    [Fact]
    public void The_mock_export_sends_no_paid_amount()
    {
        var eob = PatientAccessMapperPaymentEob();
        eob.Payment.Should().BeNull();
    }

    private static FhirExplanationOfBenefit PatientAccessMapperPaymentEob()
    {
        var provider = new FixedPayments();
        var member = new ChoMember { MemberId = "M-1", FirstName = "Pat", LastName = "Jones", Dob = "1962-03-22", Gender = "F" };
        var resources = new PatientAccessPayerToPayerExportSource(provider).GetResourcesAsync(member,
            new PayerToPayerExchangeRequest { TenantId = Tenant, ExchangeDateUtc = new DateTime(2026, 9, 1), LookbackYears = 5 }).Result;
        return resources.OfType<FhirExplanationOfBenefit>().Single();
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

    [Fact]
    public async Task Claims_beyond_what_one_read_can_carry_fail_rather_than_truncate()
    {
        var (directory, upstream) = Directory();
        upstream.Respond = _ => Json(Bundle((long)ChoNativeMemberDirectory.MaxEobPages * ChoNativeMemberDirectory.EobPageSize + 1,
            Eob("e-1", "M-1", "2026-01-01")));

        var act = () => directory.GetExplanationsOfBenefitAsync(Tenant, "M-1");

        await act.Should().ThrowAsync<FhirUpstreamException>();
        upstream.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Claim_or_coverage_paging_that_changes_under_the_read_fails()
    {
        var fullEobs = Enumerable.Range(0, ChoNativeMemberDirectory.EobPageSize).Select(i => Eob($"e{i}", "M-1", "2026-01-01")).ToArray();
        var (eobDirectory, eobUpstream) = Directory();
        eobUpstream.Respond = r => Uri.UnescapeDataString(r.RequestUri!.Query).Contains("_page=1")
            ? Json(Bundle(250, fullEobs))
            : Json(Bundle(250));
        await ((Func<Task>)(() => eobDirectory.GetExplanationsOfBenefitAsync(Tenant, "M-1"))).Should().ThrowAsync<FhirUpstreamException>();

        var fullCoverages = Enumerable.Range(0, ChoNativeMemberDirectory.PageSize).Select(i => Coverage($"c{i}", "M-1")).ToArray();
        var (covDirectory, covUpstream) = Directory();
        covUpstream.Respond = r => Uri.UnescapeDataString(r.RequestUri!.Query).Contains("offset=0")
            ? Json(Page(150, fullCoverages))
            : Json(Page(151, Coverage("c-x", "M-1")));
        await ((Func<Task>)(() => covDirectory.GetCoveragesAsync(Tenant, "M-1"))).Should().ThrowAsync<FhirUpstreamException>();
    }

    [Fact]
    public async Task A_read_outside_a_tenant_resolved_request_is_refused()
    {
        var (directory, upstream) = Directory(requestTenant: null);

        var act = () => directory.FindByMemberIdAsync(Tenant, "M-1");

        await act.Should().ThrowAsync<InvalidOperationException>();
        upstream.Requests.Should().BeEmpty();
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

    private sealed class FixedPayments : global::FhirService.Services.IPatientAccessDataProvider
    {
        public Task<ChoMember?> GetMemberAsync(string memberId, CancellationToken ct = default) => Task.FromResult<ChoMember?>(null);
        public Task<IReadOnlyList<ChoMember>> GetMembersByPatientIdAsync(string patientId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ChoMember>>([]);
        public Task<IReadOnlyList<ChoPaymentDocument>> GetPaymentsByPatientIdAsync(string patientId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ChoPaymentDocument>>(
                [new ChoPaymentDocument { PaymentId = "p-1", ClaimId = "c-1", MemberId = "M-1", PaymentDate = "2026-01-01", TotalPaid = 125m }]);
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
