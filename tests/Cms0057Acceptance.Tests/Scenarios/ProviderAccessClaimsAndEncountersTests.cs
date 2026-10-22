using System.Net;
using System.Text;
using ClaimsService.Fhir;
using ClaimsService.Repositories;
using EncounterService.Fhir;
using EncounterService.Repositories;
using FhirService.Controllers;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.ChoNative;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ChoClaim = ClaimsService.Models.Claim;
using ChoClaimFhirSearchQuery = ClaimsService.Models.ClaimFhirSearchQuery;
using ChoClaimLine = ClaimsService.Models.ClaimLine;
using ChoClaimStatus = ClaimsService.Models.ClaimStatus;
using ChoDiagnosisCode = ClaimsService.Models.DiagnosisCode;
using ChoEncounter = EncounterService.Models.Encounter;
using ChoEncounterDiagnosis = EncounterService.Models.EncounterDiagnosis;
using ChoEncounterFhirSearchQuery = EncounterService.Models.EncounterFhirSearchQuery;
using ChoEncounterServiceLine = EncounterService.Models.EncounterServiceLine;
using ChoEncounterStatus = EncounterService.Models.EncounterStatus;
using ChoSubmissionType = EncounterService.Models.SubmissionType;
using FhirClaim = Hl7.Fhir.Model.Claim;
using FhirEncounter = Hl7.Fhir.Model.Encounter;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// PROV-01 — an attributed provider reads the member's claims and encounters
/// through Provider Access, on the path the running services use:
///
///   fhir-service   ClaimController / EncounterController   (member confinement)
///        │         RoutingFhirDataAdapter → ChoNativeFhirDataAdapter
///        │ HTTP: GET /fhir/Claim[?…] · GET /api/encounters/fhir/Encounter[?…]
///   claims-service    FhirClaimController    → ClaimFhirProjector
///   encounter-service FhirEncounterController → EncounterFhirProjector
///
/// The hop between them is a real HTTP request from the CHO-native adapter,
/// answered by the owning service's controller, and the FHIR resource is
/// parsed back by the adapter's strict parser — so a projection the parser
/// rejects fails the test rather than reaching a caller. Only the Mongo
/// repositories are in-memory stand-ins; tenant scoping is in their filters
/// and is tested against them directly. Synthetic data only.
///
/// What this replaces: before ADR 024, Encounter and Claim came from
/// <c>MockFhirDataAdapter</c>, which has no tenant and no CHO data behind it
/// (G-30). These tests are what makes the Provider Access claims and encounter
/// data real rather than synthetic.
///
/// What this does not show: profile conformance. Neither projection declares a
/// profile, and neither has been validated against US Core (Encounter) or
/// CARIN Blue Button (Claim) — see the PROV-01 row in docs/epa-gap-matrix.md
/// §11 and G-14 / G-60.
/// </summary>
public class ProviderAccessClaimsAndEncountersTests
{
    private const string Tenant = "tenant-prov01";
    private const string Member = "MEM-PROV-1";
    private const string OtherMember = "MEM-PROV-2";

    // ── PROV-01: claims ──────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task PROV01_Replace_AnAttributedProviderReadsTheMembersClaimsFromClaimsService()
    {
        var path = new Path(claims: [Claim("C1", Member), Claim("C2", Member), Claim("C3", OtherMember)]);

        var bundle = Bundle(await path.SearchClaimsAsProvider(Member, "?_count=10"));

        // Only the confined member's claims, and the total is the upstream's.
        bundle.Total.Should().Be(2);
        bundle.Entry.Select(e => ((FhirClaim)e.Resource).Patient.Reference)
            .Should().AllBe("Patient/" + Member);

        // The data is claims-service's, not synthetic: the projection carries
        // the billed line and charge this test stored.
        var first = (FhirClaim)bundle.Entry[0].Resource;
        first.Item.Should().ContainSingle()
            .Which.ProductOrService.Coding[0].Code.Should().Be("99214");
        first.Total.Value.Should().Be(180m);
        first.Identifier.Should().ContainSingle(i => i.System == ClaimFhirProjector.ClaimNumberSystem);

        // And the adapter really went over HTTP to claims-service, carrying the
        // member as the upstream filter.
        path.Upstream.Should().ContainSingle()
            .Which.Should().StartWith("/fhir/Claim?").And.Contain("memberId=" + Member);
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task PROV01_Replace_AClaimReadOutsideTheConfinedMemberIs404()
    {
        var path = new Path(claims: [Claim("C1", Member), Claim("C3", OtherMember)]);

        var own = await path.ReadClaimAsProvider(Member, "C1");
        Status(own).Should().Be(HttpStatusCode.OK);

        // The other member's claim exists upstream and is still answered 404,
        // so the provider cannot learn that it exists.
        var other = await path.ReadClaimAsProvider(Member, "C3");
        Status(other).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task PROV01_Replace_AnExplicitPatientParameterCannotWidenAConfinedClaimSearch()
    {
        var path = new Path(claims: [Claim("C1", Member), Claim("C3", OtherMember)]);

        var bundle = Bundle(await path.SearchClaimsAsProvider(Member, "?patient=" + OtherMember));

        // The controller pins the search to the authorized member, so asking
        // for someone else's returns the authorized member's claims.
        bundle.Entry.Select(e => ((FhirClaim)e.Resource).Patient.Reference)
            .Should().AllBe("Patient/" + Member);
        path.Upstream.Should().ContainSingle().Which.Should().Contain("memberId=" + Member);
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task PROV01_Replace_ClaimResultsOutsideTheConfinementAreWithheldNotTrimmed()
    {
        // The upstream ignores the member filter it was given — a contract
        // break, not something a caller can cause. The page is withheld
        // whole: trimming it would answer a total the entries contradict.
        var path = new Path(claims: [Claim("C1", Member), Claim("C3", OtherMember)]) { IgnoreMemberFilter = true };

        var result = await path.SearchClaimsAsProvider(Member, "?_count=10");

        Status(result).Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task PROV01_Replace_AVoidedClaimIsCancelledAndAnAdjudicatedOneIsStillActive()
    {
        // FHIR Claim.status is about the resource, not the adjudication
        // outcome: a denied claim still stands, and only a void cancels it.
        var denied = Claim("C1", Member, ChoClaimStatus.Denied);
        var voided = Claim("C2", Member, ChoClaimStatus.Voided);
        var path = new Path(claims: [denied, voided]);

        var statuses = Bundle(await path.SearchClaimsAsProvider(Member, "?_count=10"))
            .Entry.ToDictionary(e => e.Resource.Id, e => ((FhirClaim)e.Resource).Status);

        statuses["C1"].Should().Be(FinancialResourceStatusCodes.Active);
        statuses["C2"].Should().Be(FinancialResourceStatusCodes.Cancelled);
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task PROV01_Replace_APriorAuthorizationSearchDoesNotReturnClaims()
    {
        // use=preauthorization names the prior-authorization records
        // authorization-service projects as PDex EOBs, not claims. Answering
        // claims for it would misreport them as authorizations.
        var path = new Path(claims: [Claim("C1", Member)]);

        var bundle = Bundle(await path.SearchClaimsAsProvider(Member, "?use=preauthorization"));

        bundle.Total.Should().Be(0);
        bundle.Entry.Should().BeEmpty();
        path.Upstream.Should().BeEmpty("the adapter answers without calling claims-service");
    }

    // ── PROV-01: encounters ──────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task PROV01_Replace_AnAttributedProviderReadsTheMembersEncountersFromEncounterService()
    {
        var path = new Path(encounters:
            [Encounter("E1", Member), Encounter("E2", Member), Encounter("E3", OtherMember)]);

        var bundle = Bundle(await path.SearchEncountersAsProvider(Member, "?_count=10"));

        bundle.Total.Should().Be(2);
        bundle.Entry.Select(e => ((FhirEncounter)e.Resource).Subject.Reference)
            .Should().AllBe("Patient/" + Member);

        var first = (FhirEncounter)bundle.Entry[0].Resource;
        first.Status.Should().Be(FhirEncounter.EncounterStatus.Finished);
        first.Class.Code.Should().Be("AMB", "place of service 11 is an office visit");
        first.ReasonCode.Should().ContainSingle()
            .Which.Coding[0].Code.Should().Be("E11.9");
        first.Period.Start.Should().NotBeNull();

        path.Upstream.Should().ContainSingle()
            .Which.Should().StartWith("/api/encounters/fhir/Encounter?").And.Contain("memberId=" + Member);
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task PROV01_Replace_AnEncounterReadOutsideTheConfinedMemberIs404()
    {
        var path = new Path(encounters: [Encounter("E1", Member), Encounter("E3", OtherMember)]);

        Status(await path.ReadEncounterAsProvider(Member, "E1")).Should().Be(HttpStatusCode.OK);
        Status(await path.ReadEncounterAsProvider(Member, "E3")).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task PROV01_Replace_EncounterStatusIsTheContactNotTheEdiSubmissionState()
    {
        // An encounter still queued for dispatch describes a visit that
        // already happened, so it is finished; a void withdraws the contact.
        var queued = Encounter("E1", Member);
        queued.Status = ChoEncounterStatus.Queued;
        var voided = Encounter("E2", Member);
        voided.SubmissionType = ChoSubmissionType.Void;
        var path = new Path(encounters: [queued, voided]);

        var statuses = Bundle(await path.SearchEncountersAsProvider(Member, "?_count=10"))
            .Entry.ToDictionary(e => e.Resource.Id, e => ((FhirEncounter)e.Resource).Status);

        statuses["E1"].Should().Be(FhirEncounter.EncounterStatus.Finished);
        statuses["E2"].Should().Be(FhirEncounter.EncounterStatus.EnteredInError);
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task PROV01_Replace_AnEncounterStatusTheProjectionNeverEmitsSelectsNothing()
    {
        // "planned" is a valid R4 Encounter status that no CHO record projects
        // as, so it answers an empty bundle rather than refusing the query —
        // and without troubling the upstream.
        var path = new Path(encounters: [Encounter("E1", Member)]);

        var bundle = Bundle(await path.SearchEncountersAsProvider(Member, "?status=planned"));

        bundle.Total.Should().Be(0);
        path.Upstream.Should().BeEmpty();
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task PROV01_Replace_EncounterResultsOutsideTheConfinementAreWithheldNotTrimmed()
    {
        var path = new Path(encounters: [Encounter("E1", Member), Encounter("E3", OtherMember)])
        {
            IgnoreMemberFilter = true,
        };

        Status(await path.SearchEncountersAsProvider(Member, "?_count=10")).Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task PROV01_Replace_AnUnsupportedSearchParameterIsRefusedRatherThanIgnored()
    {
        // The bundle's self link echoes the query as given, so applying less
        // than it asked for would describe a result it is not.
        var path = new Path(encounters: [Encounter("E1", Member)]);

        var act = async () => await path.SearchEncountersAsProvider(Member, "?participant=1234567893");

        await act.Should().ThrowAsync<FhirSearchNotSupportedException>();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static ChoClaim Claim(string id, string member, ChoClaimStatus status = ChoClaimStatus.Paid) => new()
    {
        Id = id,
        ClaimVersionId = id,
        TenantId = Tenant,
        ClaimNumber = "CLM-" + id,
        MemberId = member,
        CoverageId = "cov-1",
        Status = status,
        BillingProviderNPI = "1234567893",
        TotalChargeAmount = 180m,
        ServiceDateFrom = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        SubmittedDate = new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc),
        DiagnosisCodes = [new ChoDiagnosisCode { Code = "E11.9", PointerNumber = 1, CodeQualifier = "ABK" }],
        ClaimLines =
        [
            new ChoClaimLine
            {
                LineNumber = 1,
                ProcedureCode = "99214",
                Units = 1,
                ChargeAmount = 180m,
                DiagnosisPointers = [1],
                ServiceDateFrom = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
                ServiceDateTo = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
            },
        ],
    };

    private static ChoEncounter Encounter(string id, string member) => new()
    {
        Id = id,
        TenantId = Tenant,
        EncounterControlNumber = "ECN-" + id,
        ClaimId = "C-" + id,
        MemberId = member,
        BillingProviderNPI = "1234567893",
        BillingProviderName = "Example Clinic",
        RenderingProviderNPI = "1245319599",
        PayerId = "PAYER-1",
        PlaceOfServiceCode = "11",
        TotalChargeAmount = 180m,
        ServiceDateFrom = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        DiagnosisCodes = [new ChoEncounterDiagnosis { Code = "E11.9", PointerNumber = 1, CodeQualifier = "ABK" }],
        ServiceLines =
        [
            new ChoEncounterServiceLine
            {
                LineNumber = 1,
                ProcedureCode = "99214",
                Units = 1,
                ChargeAmount = 180m,
                ServiceDateFrom = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
                ServiceDateTo = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
            },
        ],
    };

    // ── The path under test ──────────────────────────────────────────────────

    private sealed class Path
    {
        private readonly Dictionary<string, ChoClaim> _claims;
        private readonly Dictionary<string, ChoEncounter> _encounters;

        public Path(IReadOnlyList<ChoClaim>? claims = null, IReadOnlyList<ChoEncounter>? encounters = null)
        {
            _claims = (claims ?? []).ToDictionary(c => c.Id, StringComparer.Ordinal);
            _encounters = (encounters ?? []).ToDictionary(e => e.Id, StringComparer.Ordinal);
        }

        /// <summary>The upstream ignores the member filter it is given (a contract break).</summary>
        public bool IgnoreMemberFilter { get; init; }

        /// <summary>The upstream requests the CHO-native adapter made (path and query).</summary>
        public List<string> Upstream { get; } = [];

        public Task<IActionResult> SearchClaimsAsProvider(string member, string query)
        {
            var (controller, http) = Claim(member, query);
            return controller.Search(Bind<ClaimSearchParams>(http), CancellationToken.None);
        }

        public Task<IActionResult> ReadClaimAsProvider(string member, string id) =>
            Claim(member, string.Empty).Controller.Read(id, CancellationToken.None);

        public Task<IActionResult> SearchEncountersAsProvider(string member, string query)
        {
            var (controller, http) = Encounter(member, query);
            return controller.Search(Bind<EncounterSearchParams>(http), CancellationToken.None);
        }

        public Task<IActionResult> ReadEncounterAsProvider(string member, string id) =>
            Encounter(member, string.Empty).Controller.Read(id, CancellationToken.None);

        /// <summary>
        /// The search parameters the model binder would produce from the query
        /// string. The adapter reads the raw query itself (repeated parameters
        /// AND, which a bound string cannot carry), so only the fields the
        /// controllers use are bound here.
        /// </summary>
        private static T Bind<T>(HttpContext http) where T : FhirSearchParamsBase, new()
        {
            var query = http.Request.Query;
            var search = new T
            {
                Id = query["_id"].FirstOrDefault(),
                LastUpdated = query["_lastUpdated"].FirstOrDefault(),
            };
            if (int.TryParse(query["_count"].FirstOrDefault(), out var count)) search.Count = count;
            if (int.TryParse(query["_page"].FirstOrDefault(), out var page)) search.Page = page;

            switch (search)
            {
                case ClaimSearchParams claim:
                    claim.Patient = query["patient"].FirstOrDefault();
                    claim.Created = query["created"].FirstOrDefault();
                    claim.Status = query["status"].FirstOrDefault();
                    claim.Use = query["use"].FirstOrDefault();
                    break;
                case EncounterSearchParams encounter:
                    encounter.Patient = query["patient"].FirstOrDefault();
                    encounter.Date = query["date"].FirstOrDefault();
                    encounter.Status = query["status"].FirstOrDefault();
                    encounter.Type = query["type"].FirstOrDefault();
                    break;
            }
            return search;
        }

        private (ClaimController Controller, HttpContext Http) Claim(string member, string query)
        {
            var http = FhirRequest(member, query);
            var controller = new ClaimController(Adapter(http), BundleBuilder())
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
            return (controller, http);
        }

        private (EncounterController Controller, HttpContext Http) Encounter(string member, string query)
        {
            var http = FhirRequest(member, query);
            var controller = new EncounterController(Adapter(http), BundleBuilder())
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
            return (controller, http);
        }

        /// <summary>
        /// The router with Encounter and Claim bound to ChoNative — the binding
        /// the Kubernetes manifest sets — over the real CHO-native adapter.
        /// </summary>
        private IFhirDataAdapter Adapter(HttpContext http)
        {
            var accessor = new Mock<IHttpContextAccessor>();
            accessor.Setup(a => a.HttpContext).Returns(http);

            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(ChoNativeFhirDataAdapter.ClaimsServiceClient))
                .Returns(() => new HttpClient(new ClaimsServiceHandler(this)) { BaseAddress = new Uri("http://claims-service.test/") });
            factory.Setup(f => f.CreateClient(ChoNativeFhirDataAdapter.EncounterServiceClient))
                .Returns(() => new HttpClient(new EncounterServiceHandler(this)) { BaseAddress = new Uri("http://encounter-service.test/") });

            var native = new ChoNativeFhirDataAdapter(
                factory.Object, accessor.Object, NullLogger<ChoNativeFhirDataAdapter>.Instance);

            var bindings = new FhirDataBindings(
                FhirAdapterSources.Mock, FhirAdapterSources.Mock,
                FhirAdapterSources.ChoNative, FhirAdapterSources.ChoNative);

            return new RoutingFhirDataAdapter(bindings, new MockFhirDataAdapter(), native);
        }

        private static FhirBundleBuilder BundleBuilder() =>
            new(new ConfigurationBuilder().AddInMemoryCollection([]).Build());

        private static HttpContext FhirRequest(string member, string query)
        {
            var http = new DefaultHttpContext();
            http.Items["TenantId"] = Tenant;
            // What ProviderAccessAuthorizationFilter sets once it has allowed a
            // provider for one member (attribution + no opt-out, ADR 021).
            http.Items[FhirService.Services.ProviderAccess.ProviderAccessAuthorizationFilter.AuthorizedMemberItemKey] = member;
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("fhir.test");
            http.Request.QueryString = new QueryString(query);
            return http;
        }

        internal ClaimsService.Controllers.FhirClaimController Claims()
        {
            var repository = new Mock<IClaimRepository>();
            repository.Setup(r => r.GetLatestVersionAsync(It.IsAny<string>(), It.IsAny<DateTime>()))
                .ReturnsAsync((string id, DateTime _) => _claims.GetValueOrDefault(id));

            var search = new Mock<IClaimFhirSearchRepository>();
            search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<ChoClaimFhirSearchQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string tenantId, ChoClaimFhirSearchQuery q, CancellationToken _) =>
                {
                    tenantId.Should().Be(Tenant, "the upstream filters by the token tenant");
                    var matches = _claims.Values
                        .Where(c => IgnoreMemberFilter || q.MemberId is null || c.MemberId == q.MemberId)
                        .ToList();
                    return ((IReadOnlyList<ChoClaim>)matches, (long)matches.Count);
                });

            var http = new DefaultHttpContext();
            http.Items["TenantId"] = Tenant;
            return new ClaimsService.Controllers.FhirClaimController(
                repository.Object, search.Object, new ClaimFhirProjector(),
                NullLogger<ClaimsService.Controllers.FhirClaimController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
        }

        internal EncounterService.Controllers.FhirEncounterController Encounters()
        {
            var repository = new Mock<IEncounterRepository>();
            repository.Setup(r => r.GetByIdAsync(It.IsAny<string>()))
                .ReturnsAsync((string id) => _encounters.GetValueOrDefault(id));

            var search = new Mock<IEncounterFhirSearchRepository>();
            search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<ChoEncounterFhirSearchQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string tenantId, ChoEncounterFhirSearchQuery q, CancellationToken _) =>
                {
                    tenantId.Should().Be(Tenant, "the upstream filters by the token tenant");
                    var matches = _encounters.Values
                        .Where(e => IgnoreMemberFilter || q.MemberId is null || e.MemberId == q.MemberId)
                        .ToList();
                    return ((IReadOnlyList<ChoEncounter>)matches, (long)matches.Count);
                });

            var http = new DefaultHttpContext();
            http.Items["TenantId"] = Tenant;
            return new EncounterService.Controllers.FhirEncounterController(
                repository.Object, search.Object, new EncounterFhirProjector(),
                NullLogger<EncounterService.Controllers.FhirEncounterController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
        }
    }

    /// <summary>Answers the adapter's HTTP request with the claims-service controller.</summary>
    private sealed class ClaimsServiceHandler(Path path) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            path.Upstream.Add(uri.PathAndQuery);
            var claims = path.Claims();
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);

            IActionResult result = segments.Length == 3
                ? await claims.ReadClaim(Uri.UnescapeDataString(segments[2]), ct)
                : await claims.SearchClaims(
                    query.TryGetValue("claimVersionId", out var cid) ? cid.ToString() : null,
                    query.TryGetValue("memberId", out var m) ? m.ToString() : null,
                    createdFrom: null, createdTo: null,
                    status: query.TryGetValue("status", out var s) ? s.ToString() : null,
                    claimType: null, lastUpdatedFrom: null, lastUpdatedTo: null,
                    offset: query.TryGetValue("offset", out var o) ? int.Parse(o!) : 0,
                    limit: query.TryGetValue("limit", out var l) ? int.Parse(l!) : 20,
                    ct: ct);

            return Respond(result);
        }
    }

    /// <summary>Answers the adapter's HTTP request with the encounter-service controller.</summary>
    private sealed class EncounterServiceHandler(Path path) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            path.Upstream.Add(uri.PathAndQuery);
            var encounters = path.Encounters();
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);

            // /api/encounters/{id}/fhir is the read; /api/encounters/fhir/Encounter is the search.
            IActionResult result = segments is [_, _, _, "fhir"]
                ? await encounters.GetFhirEncounter(Uri.UnescapeDataString(segments[2]))
                : await encounters.SearchFhirEncounters(
                    query.TryGetValue("encounterId", out var eid) ? eid.ToString() : null,
                    query.TryGetValue("memberId", out var m) ? m.ToString() : null,
                    serviceDateFrom: null, serviceDateTo: null,
                    status: query.TryGetValue("status", out var s) ? s.ToString() : null,
                    encounterType: null, lastUpdatedFrom: null, lastUpdatedTo: null,
                    offset: query.TryGetValue("offset", out var o) ? int.Parse(o!) : 0,
                    limit: query.TryGetValue("limit", out var l) ? int.Parse(l!) : 20);

            return Respond(result);
        }
    }

    /// <summary>
    /// The owning service's answer as an HTTP response. The FHIR read returns a
    /// ContentResult; the search returns the JSON envelope
    /// (<c>total</c> + <c>items[].resource</c>) the adapter parses.
    /// </summary>
    private static HttpResponseMessage Respond(IActionResult result) => result switch
    {
        ContentResult content => new HttpResponseMessage((HttpStatusCode)(content.StatusCode ?? 200))
        {
            Content = new StringContent(content.Content ?? string.Empty, Encoding.UTF8, "application/fhir+json"),
        },
        NotFoundResult => new HttpResponseMessage(HttpStatusCode.NotFound),
        ObjectResult obj => new HttpResponseMessage((HttpStatusCode)(obj.StatusCode ?? 200))
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(obj.Value, SearchEnvelopeJson),
                Encoding.UTF8, "application/json"),
        },
        _ => throw new InvalidOperationException(result.GetType().Name),
    };

    /// <summary>
    /// camelCase, as the services' AddCloudHealthOfficeJsonOptions produces and
    /// as the adapter's parser expects (<c>total</c>, <c>items</c>,
    /// <c>resource</c>).
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions SearchEnvelopeJson = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    private static HttpStatusCode Status(IActionResult result) => result switch
    {
        ContentResult c => (HttpStatusCode)(c.StatusCode ?? 200),
        ObjectResult o => (HttpStatusCode)(o.StatusCode ?? 200),
        NotFoundResult => HttpStatusCode.NotFound,
        _ => throw new InvalidOperationException(result.GetType().Name),
    };

    private static Bundle Bundle(IActionResult result)
    {
        Status(result).Should().Be(HttpStatusCode.OK);
        return (Bundle)((ObjectResult)result).Value!;
    }
}
