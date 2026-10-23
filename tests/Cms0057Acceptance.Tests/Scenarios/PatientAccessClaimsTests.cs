using System.Net;
using System.Text;
using System.Text.Json;
using ClaimsService.Controllers;
using ClaimsService.Fhir;
using ClaimsService.Repositories;
using ClaimsService.Services;
using FhirService.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ChoClaim = ClaimsService.Models.Claim;
using ChoClaimLine = ClaimsService.Models.ClaimLine;
using ChoClaimStatus = ClaimsService.Models.ClaimStatus;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// PAT-01 — a member reads their own claims as ExplanationOfBenefit through
/// Patient Access, on the path the running services use:
///
///   fhir-service   ExplanationOfBenefitController  (SMART patient binding, confinement)
///        │ HTTP: GET fhir/ExplanationOfBenefit[/{id}][?patient=…]
///   claims-service FhirExplanationOfBenefitController → IClaimRepository → ExplanationOfBenefitProjector
///
/// The hop between them is a real HTTP request from fhir-service's client,
/// answered by the claims-service controller; only the repository is an
/// in-memory stand-in (tenant scoping is the Mongo repository's and is tested
/// there). Synthetic data only.
///
/// What this does and does not show about CARIN Blue Button conformance: the
/// projection now declares the CARIN BB 2.2.0 financial profile for its claim
/// type and satisfies it structurally — the HL7 validator judges that over
/// <c>conformance/golden/</c>, which is where cardinality, slicing and
/// invariants belong rather than in an assertion written against the same
/// understanding that produced the resource. What is asserted here is the one
/// thing a validator run cannot show: that the resource a member actually
/// receives through the real path carries the profile, so the validated shape
/// and the served shape are the same resource.
///
/// The scenario stays PARTIAL on what neither can close: no external
/// conformance suite has judged CHO as a server (gap G-60) — see the PAT-01 row
/// in docs/epa-gap-matrix.md §11.
/// </summary>
public class PatientAccessClaimsTests
{
    private const string Tenant = "tenant-pat01";
    private const string Member = "MEM-PAT-1";
    private const string OtherMember = "MEM-PAT-2";

    private static ChoClaim Claim(string id, string member, ChoClaimStatus status = ChoClaimStatus.Paid) => new()
    {
        Id = id,
        ClaimVersionId = id,
        TenantId = Tenant,
        ClaimNumber = "CLM-" + id,
        MemberId = member,
        Status = status,
        BillingProviderNPI = "1234567893",
        TotalChargeAmount = 180m,
        ServiceDateFrom = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        SubmittedDate = new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc),
        AdjudicationResult = new ClaimsService.Models.AdjudicationResult
        {
            AllowedAmount = 120m,
            PayerPayment = 100m,
            PatientResponsibility = 20m,
        },
        ClaimLines =
        [
            new ChoClaimLine { LineNumber = 1, ProcedureCode = "99214", Units = 1, ChargeAmount = 180m },
        ],
    };

    // ── Read and search through the real path ──────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-01")]
    [Trait("Backend", "Replace")]
    public async Task PAT01_Replace_AMemberSearchesTheirOwnClaimsAsExplanationOfBenefit()
    {
        var path = new Path(Claim("C1", Member), Claim("C2", Member), Claim("C3", OtherMember));

        var result = await path.SearchAsPatient(Member, "?_count=10");

        var bundle = Body(result, HttpStatusCode.OK);
        bundle.GetProperty("resourceType").GetString().Should().Be("Bundle");
        bundle.GetProperty("total").GetInt32().Should().Be(2);
        var eobs = bundle.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource")).ToList();
        eobs.Should().HaveCount(2);
        eobs.Should().AllSatisfy(eob =>
        {
            eob.GetProperty("resourceType").GetString().Should().Be("ExplanationOfBenefit");
            eob.GetProperty("patient").GetProperty("reference").GetString().Should().Be($"Patient/{Member}");
            eob.GetProperty("outcome").GetString().Should().NotBeNullOrEmpty();
            eob.TryGetProperty("item", out _).Should().BeTrue("each EOB carries its service lines");
        });
        // The upstream request was pinned to the token's member.
        path.Upstream.Should().ContainSingle().Which.Should().Contain($"patient={Member}");
    }

    [Fact]
    [Trait("Scenario", "PAT-01")]
    [Trait("Backend", "Replace")]
    public async Task PAT01_Replace_AMemberReadsTheirOwnExplanationOfBenefitAndNotAnotherMembers()
    {
        var path = new Path(Claim("C1", Member), Claim("C3", OtherMember));

        var own = await path.ReadAsPatient(Member, "C1");
        var others = await path.ReadAsPatient(Member, "C3");

        Body(own, HttpStatusCode.OK).GetProperty("id").GetString().Should().Be("C1");
        Status(others).Should().Be(HttpStatusCode.NotFound, "another member's EOB is answered as not found, not forbidden");
    }

    [Fact]
    [Trait("Scenario", "PAT-01")]
    [Trait("Backend", "Replace")]
    public async Task PAT01_Replace_AnExplicitPatientParameterCannotWidenAPatientToken()
    {
        var path = new Path(Claim("C1", Member), Claim("C3", OtherMember));

        var result = await path.SearchAsPatient(Member, $"?patient={OtherMember}");

        var bundle = Body(result, HttpStatusCode.OK);
        bundle.GetProperty("entry").EnumerateArray()
            .Select(e => e.GetProperty("resource").GetProperty("patient").GetProperty("reference").GetString())
            .Should().AllBe($"Patient/{Member}");
        path.Upstream.Single().Should().NotContain(OtherMember);
    }

    [Fact]
    [Trait("Scenario", "PAT-01")]
    [Trait("Backend", "Replace")]
    public async Task PAT01_Replace_ResultsOutsideTheMembersScopeAreWithheld()
    {
        // Defence in depth: a data source that ignores the member filter
        // must not leak another member's claims through Patient Access.
        var path = new Path(Claim("C1", Member), Claim("C3", OtherMember)) { IgnoreMemberFilter = true };

        var result = await path.SearchAsPatient(Member, "?_count=10");

        Status(result).Should().Be(HttpStatusCode.BadGateway);
        Text(result).Should().NotContain(OtherMember);
    }

    [Fact]
    [Trait("Scenario", "PAT-01")]
    [Trait("Backend", "Replace")]
    public async Task PAT01_Replace_TheEobAMemberReceivesDeclaresTheCarinBlueButtonFinancialProfile()
    {
        var path = new Path(Claim("C1", Member));

        var eob = Body(await path.ReadAsPatient(Member, "C1"), HttpStatusCode.OK);

        // The profile the HL7 validator judges conformance against. An 837P
        // selects the Professional NonClinician profile; the institutional and
        // oral variants are exercised by their own goldens.
        eob.GetProperty("meta").GetProperty("profile").EnumerateArray()
            .Select(p => p.GetString())
            .Should().Equal(
                "http://hl7.org/fhir/us/carin-bb/StructureDefinition/C4BB-ExplanationOfBenefit-Professional-NonClinician");

        // Two elements the profile requires and the earlier projection did not
        // carry, read off the served resource rather than the projector: a
        // total whose category is a member of C4BB's own adjudication value set,
        // and the unique-claim-id identifier slice.
        eob.GetProperty("total").EnumerateArray()
            .Select(t => t.GetProperty("category").GetProperty("coding")[0].GetProperty("code").GetString())
            .Should().Contain("submitted");

        eob.GetProperty("identifier")[0].GetProperty("type").GetProperty("coding")[0]
            .GetProperty("code").GetString().Should().Be("uc");
    }

    // ── Harness ────────────────────────────────────────────────────────────

    /// <summary>fhir-service's EOB controller wired over HTTP to claims-service's.</summary>
    private sealed class Path
    {
        private readonly Dictionary<string, ChoClaim> _claims;

        public Path(params ChoClaim[] claims) => _claims = claims.ToDictionary(c => c.Id);

        public bool IgnoreMemberFilter { get; init; }

        /// <summary>The claims-service requests fhir-service made (path and query).</summary>
        public List<string> Upstream { get; } = [];

        public Task<IActionResult> SearchAsPatient(string member, string query) =>
            Fhir(member, query).SearchEobs(CancellationToken.None);

        public Task<IActionResult> ReadAsPatient(string member, string id) =>
            Fhir(member, string.Empty).ReadEob(id, CancellationToken.None);

        private ExplanationOfBenefitController Fhir(string member, string query)
        {
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(ExplanationOfBenefitController.ClaimsServiceClientName))
                .Returns(() => new HttpClient(new ClaimsServiceHandler(this)) { BaseAddress = new Uri("http://claims-service.test/") });

            var http = new DefaultHttpContext();
            // What SmartScopeEnforcementMiddleware sets for a patient-bound token.
            http.Items["SmartPatientId"] = member;
            http.Request.QueryString = new QueryString(query);
            return new ExplanationOfBenefitController(factory.Object, NullLogger<ExplanationOfBenefitController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
        }

        internal FhirExplanationOfBenefitController Claims()
        {
            var repository = new Mock<IClaimRepository>();
            repository.Setup(r => r.GetLatestVersionAsync(It.IsAny<string>(), It.IsAny<DateTime>()))
                .ReturnsAsync((string id, DateTime _) => _claims.GetValueOrDefault(id));
            repository.Setup(r => r.SearchForMemberAsync(
                    It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<ChoClaimStatus?>(),
                    It.IsAny<string?>(), It.IsAny<ClaimsService.Models.ClaimType?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
                    It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync((string memberId, DateTime? _, DateTime? _, ChoClaimStatus? _, string? _,
                    ClaimsService.Models.ClaimType? _, decimal? _, decimal? _, int _, int _) =>
                {
                    var matches = _claims.Values.Where(c => IgnoreMemberFilter || c.MemberId == memberId).ToList();
                    return ((IReadOnlyList<ChoClaim>)matches, matches.Count);
                });

            var enricher = new Mock<IClaimDiagnosisMetadataEnricher>();
            var http = new DefaultHttpContext();
            http.Items["TenantId"] = Tenant;
            return new FhirExplanationOfBenefitController(
                repository.Object, new ExplanationOfBenefitProjector(),
                new CarinNonFinancialEobProjector(), enricher.Object,
                NullLogger<FhirExplanationOfBenefitController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
        }
    }

    /// <summary>Answers fhir-service's HTTP request with the claims-service controller.</summary>
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
                ? await claims.ReadEob(Uri.UnescapeDataString(segments[2]), ct)
                : await claims.SearchEobs(
                    query.TryGetValue("patient", out var p) ? p.ToString() : null,
                    query.TryGetValue("_id", out var id) ? id.ToString() : null,
                    query.TryGetValue("_count", out var c) ? int.Parse(c!) : 50,
                    query.TryGetValue("_page", out var pg) ? int.Parse(pg!) : 1,
                    ct);

            var content = (ContentResult)result;
            return new HttpResponseMessage((HttpStatusCode)content.StatusCode!.Value)
            {
                Content = new StringContent(content.Content ?? string.Empty, Encoding.UTF8, "application/fhir+json"),
            };
        }
    }

    private static HttpStatusCode Status(IActionResult result) => result switch
    {
        ContentResult c => (HttpStatusCode)c.StatusCode!.Value,
        ObjectResult o => (HttpStatusCode)o.StatusCode!.Value,
        _ => throw new InvalidOperationException(result.GetType().Name),
    };

    private static string Text(IActionResult result) => result switch
    {
        ContentResult c => c.Content ?? string.Empty,
        ObjectResult o => JsonSerializer.Serialize(o.Value?.ToString()),
        _ => string.Empty,
    };

    private static JsonElement Body(IActionResult result, HttpStatusCode expected)
    {
        Status(result).Should().Be(expected);
        return JsonDocument.Parse(((ContentResult)result).Content!).RootElement;
    }
}
