using System.Net;
using System.Text;
using System.Text.Json;
using ClaimsService.Controllers;
using ClaimsService.Fhir;
using ClaimsService.Repositories;
using ClaimsService.Services;
using FhirService.Controllers;
using ProviderService.Controllers;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ChoClaim = ClaimsService.Models.Claim;
using ChoClaimLine = ClaimsService.Models.ClaimLine;
using ChoClaimStatus = ClaimsService.Models.ClaimStatus;
using ChoClaimType = ClaimsService.Models.ClaimType;
using ChoDiagnosisCode = ClaimsService.Models.DiagnosisCode;
using ChoInstitutionalDetails = ClaimsService.Models.InstitutionalClaimDetails;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// PROV-01 — what an attributed provider actually receives for a claim.
///
/// <para>
/// CMS-0057-F's Provider Access API is expected to carry the CARIN Blue Button
/// <b>non-financial</b> <c>ExplanationOfBenefit</c> profiles, which PDex points
/// it at: the clinical and administrative record of the encounter without the
/// plan's financial terms. Those are between the plan and the member.
/// </para>
///
/// <para>
/// So these scenarios test two things at once, and the second matters as much
/// as the first: a provider gets the non-financial payload, and a member still
/// gets the adjudicated one. One pair of controller actions serves both, and a
/// change that crossed the wires would either hide a member's own money from
/// them or disclose the plan's terms to a provider.
/// </para>
///
/// <para>
/// The path is the one the running services use — fhir-service's EOB
/// controller, a real HTTP hop, claims-service's EOB controller and projector —
/// and every resource is parsed back with the strict FHIR parser, so a
/// projection the parser rejects fails the test rather than reaching a caller.
/// </para>
/// </summary>
public class ProviderAccessNonFinancialEobTests
{
    private const string Member = "MEM-PROV-1";
    private const string OtherMember = "MEM-PROV-2";

    /// <summary>The rendering provider on every synthetic claim here.</summary>
    private const string RenderingNpi = "1245319599";

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task A_provider_receives_the_carin_non_financial_profile()
    {
        var path = new Path();
        path.Add(ProfessionalClaim("clm-1", Member));

        var eob = await path.ReadAsProvider(Member, "clm-1");

        eob.Meta!.Profile.Should().ContainSingle()
            .Which.Should().Be(CarinNonFinancialEobProjector.ProfessionalBasisProfile,
                "an 837P is served on the CARIN BB Professional NonClinician Basis profile");
        eob.Type.Coding.Should().ContainSingle().Which.Code.Should().Be("professional");
        eob.Patient.Reference.Should().Be($"Patient/{Member}");
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task The_provider_payload_carries_no_financial_terms()
    {
        var path = new Path();
        path.Add(ProfessionalClaim("clm-1", Member));

        var body = await path.ReadAsProviderRaw(Member, "clm-1");

        // Asserted on the JSON rather than on the parsed resource: the question
        // is whether an amount reached the wire at all, including under an
        // element this test did not think to name.
        // Parsed once so a malformed payload fails here rather than on a
        // substring that happened not to match.
        JsonDocument.Parse(body).Dispose();
        var text = body;
        foreach (var forbidden in new[] { "\"total\"", "\"net\"", "\"unitPrice\"", "\"amount\"", "\"currency\"" })
        {
            text.Should().NotContain(forbidden,
                "the Provider Access payload is non-financial by requirement, not by omission");
        }

        // And the figures themselves, in case an element name changes.
        text.Should().NotContain("430");
        text.Should().NotContain("180");
    }

    [Fact]
    [Trait("Scenario", "PAT-01")]
    [Trait("Backend", "Replace")]
    public async Task The_member_still_receives_their_own_adjudicated_eob()
    {
        var path = new Path();
        path.Add(ProfessionalClaim("clm-1", Member));

        var body = await path.ReadAsPatientRaw(Member, "clm-1");

        body.Should().Contain("\"unitPrice\"").And.Contain("\"net\"",
            "Patient Access is unchanged: a member may see what was charged and what their plan paid");
        path.Upstream.Should().NotContain(u => u.Contains("ProviderAccess", StringComparison.Ordinal),
            "a patient-bound token must not be served the Provider Access projection");
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task An_institutional_claim_is_served_on_the_profile_its_bill_type_selects()
    {
        var path = new Path();
        path.Add(InstitutionalClaim("clm-ip", Member, facilityTypeCode: "11"));
        path.Add(InstitutionalClaim("clm-op", Member, facilityTypeCode: "13"));

        var inpatient = await path.ReadAsProvider(Member, "clm-ip");
        var outpatient = await path.ReadAsProvider(Member, "clm-op");

        inpatient.Meta!.Profile.Should().ContainSingle()
            .Which.Should().Be(CarinNonFinancialEobProjector.InpatientInstitutionalBasisProfile);
        outpatient.Meta!.Profile.Should().ContainSingle()
            .Which.Should().Be(CarinNonFinancialEobProjector.OutpatientInstitutionalBasisProfile);

        // The bill classification digit is the only difference between the two
        // records, so this pins the rule rather than the claim.
        inpatient.SubType.Coding.Should().ContainSingle().Which.Code.Should().Be("inpatient");
        outpatient.SubType.Coding.Should().ContainSingle().Which.Code.Should().Be("outpatient");
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task Another_members_claim_is_not_disclosed_through_the_provider_route()
    {
        var path = new Path();
        path.Add(ProfessionalClaim("clm-other", OtherMember));

        var result = await path.ReadAsProviderResult(Member, "clm-other");

        // 404, not 403: a provider authorized for one member must not learn
        // that another member's claim exists. The façade answers its own
        // refusals as an ObjectResult, so the status is read off the interface
        // both shapes implement rather than off one of them.
        ((IStatusCodeActionResult)result).StatusCode.Should().Be(404);
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task A_provider_search_returns_only_the_member_they_are_authorized_for()
    {
        var path = new Path();
        path.Add(ProfessionalClaim("clm-1", Member));
        path.Add(ProfessionalClaim("clm-2", Member));
        path.Add(ProfessionalClaim("clm-other", OtherMember));

        var bundle = await path.SearchAsProvider(Member, $"?patient={Member}");

        bundle.Entry.Should().HaveCount(2);
        foreach (var entry in bundle.Entry)
        {
            var eob = (ExplanationOfBenefit)entry.Resource;
            eob.Patient.Reference.Should().Be($"Patient/{Member}");
            eob.Meta!.Profile.Should().Contain(CarinNonFinancialEobProjector.ProfessionalBasisProfile);
        }
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task A_provider_cannot_widen_the_search_past_the_member_they_were_authorized_for()
    {
        var path = new Path();
        path.Add(ProfessionalClaim("clm-other", OtherMember));

        // The explicit parameter names someone else. The authorization was for
        // Member, so that is who the upstream is asked about, and the page is
        // empty rather than someone else's.
        var bundle = await path.SearchAsProvider(Member, $"?patient={OtherMember}");

        bundle.Entry.Should().BeEmpty();
        path.Upstream.Should().Contain(u => u.Contains($"patient={Member}", StringComparison.Ordinal));
        path.Upstream.Should().NotContain(u => u.Contains($"patient={OtherMember}", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task The_care_team_reference_resolves_to_a_practitioner_the_directory_serves()
    {
        // CARIN BB's care-team invariant says a rendering role must resolve to
        // a Practitioner. Declaring the reference is the easy half; this walks
        // it. The EOB is read through the Provider Access path, the literal
        // reference is taken off the payload rather than assumed, and the same
        // string is handed to the service that owns the Practitioner
        // projection — so a reference that pointed at a resource CHO does not
        // serve would fail here instead of in a caller's validator.
        var path = new Path();
        path.Add(ProfessionalClaim("clm-1", Member));

        var eob = await path.ReadAsProvider(Member, "clm-1");

        var reference = eob.CareTeam.Should().ContainSingle().Which.Provider.Reference;
        reference.Should().Be($"Practitioner/{RenderingNpi}",
            "the 837's rendering provider is an individual, so its type is known");

        var practitioner = await ProviderDirectory(RenderingNpi).ReadPractitioner(
            reference!.Split('/')[1], CancellationToken.None);

        var body = JsonDocument.Parse(((ContentResult)practitioner).Content!).RootElement;
        ((ContentResult)practitioner).StatusCode.Should().Be(200);
        body.GetProperty("resourceType").GetString().Should().Be("Practitioner");
        body.GetProperty("id").GetString().Should().Be(RenderingNpi,
            "the reference is relative, so the id it names has to be the NPI");
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Replace")]
    public async Task The_billing_provider_is_named_but_not_typed()
    {
        // The header provider comes from the claim's billing NPI, which may
        // belong to a group. C4BB allows either an Organization or a
        // Practitioner there, and CHO cannot tell which from the claim alone,
        // so it says what it knows — the identifier — and asserts no type. A
        // literal Practitioner reference here would be a guess that 404s for
        // every group practice.
        var path = new Path();
        path.Add(ProfessionalClaim("clm-1", Member));

        var eob = await path.ReadAsProvider(Member, "clm-1");

        eob.Provider.Reference.Should().BeNull();
        eob.Provider.Identifier.Value.Should().Be("1234567893");
    }

    /// <summary>
    /// provider-service's Practitioner endpoint over one synthetic individual
    /// provider — the service fhir-service proxies <c>fhir/r4/Practitioner</c>
    /// to (capability 5.7).
    /// </summary>
    private static FhirPractitionerController ProviderDirectory(string npi)
    {
        var provider = new ProviderService.Models.Provider
        {
            NPI = npi,
            ProviderType = ProviderService.Models.ProviderType.Individual,
            Status = ProviderService.Models.ProviderStatus.Active,
            VersionState = ProviderService.Models.ProviderVersionState.Active,
            FirstName = "Dana",
            LastName = "Okafor",
        };

        var repository = new Mock<ProviderService.Repositories.IProviderRepository>();
        repository.Setup(r => r.GetByNPIAsync(npi)).ReturnsAsync(provider);

        return new FhirPractitionerController(
            repository.Object,
            new ProviderService.Services.FhirPractitionerProjector(),
            NullLogger<FhirPractitionerController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    // ── the path under test ──────────────────────────────────────────────────

    private sealed class Path
    {
        private readonly Dictionary<string, ChoClaim> _claims = [];

        public List<string> Upstream { get; } = [];

        public void Add(ChoClaim claim) => _claims[claim.ClaimVersionId] = claim;

        public async Task<ExplanationOfBenefit> ReadAsProvider(string member, string id)
            => Parse(await ReadAsProviderRaw(member, id));

        public async Task<string> ReadAsProviderRaw(string member, string id)
            => Body(await ReadAsProviderResult(member, id), expected: 200);

        public async Task<IActionResult> ReadAsProviderResult(string member, string id)
            => await AsProvider(member, string.Empty).ReadEob(id, CancellationToken.None);

        public async Task<string> ReadAsPatientRaw(string member, string id)
            => Body(await AsPatient(member, string.Empty).ReadEob(id, CancellationToken.None), expected: 200);

        public async Task<Bundle> SearchAsProvider(string member, string query)
        {
            var result = await AsProvider(member, query).SearchEobs(CancellationToken.None);
            return new FhirJsonParser().Parse<Bundle>(Body(result, expected: 200));
        }

        private static ExplanationOfBenefit Parse(string body)
            => new FhirJsonParser().Parse<ExplanationOfBenefit>(body);

        private static string Body(IActionResult result, int expected)
        {
            var content = (ContentResult)result;
            content.StatusCode.Should().Be(expected, "got: {0}", content.Content);
            return content.Content!;
        }


        /// <summary>
        /// fhir-service's controller as an attributed provider reaches it:
        /// no patient-bound token, and the member recorded by
        /// <c>ProviderAccessAuthorizationFilter</c> once it has allowed the
        /// provider for that member (attribution plus no opt-out, ADR 021).
        /// </summary>
        private ExplanationOfBenefitController AsProvider(string member, string query)
            => Fhir(query, http => http.Items[
                FhirService.Services.ProviderAccess.ProviderAccessAuthorizationFilter.AuthorizedMemberItemKey]
                = member);

        /// <summary>The same controller as the member reaches it.</summary>
        private ExplanationOfBenefitController AsPatient(string member, string query)
            => Fhir(query, http => http.Items["SmartPatientId"] = member);

        private ExplanationOfBenefitController Fhir(string query, Action<HttpContext> bind)
        {
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(ExplanationOfBenefitController.ClaimsServiceClientName))
                .Returns(() => new HttpClient(new ClaimsServiceHandler(this))
                {
                    BaseAddress = new Uri("http://claims-service.test/"),
                });

            var http = new DefaultHttpContext();
            bind(http);
            http.Request.QueryString = new QueryString(query);

            return new ExplanationOfBenefitController(
                factory.Object, NullLogger<ExplanationOfBenefitController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
        }

        /// <summary>
        /// claims-service's own controller, with the request path it was called
        /// on — which is what selects the projection, so the test cannot pick
        /// the payload itself.
        /// </summary>
        internal FhirExplanationOfBenefitController Claims(string requestPath)
        {
            var repository = new Mock<IClaimRepository>();
            repository.Setup(r => r.GetLatestVersionAsync(It.IsAny<string>(), It.IsAny<DateTime>()))
                .ReturnsAsync((string id, DateTime _) => _claims.GetValueOrDefault(id));
            repository.Setup(r => r.SearchForMemberAsync(
                    It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<ChoClaimStatus?>(),
                    It.IsAny<string?>(), It.IsAny<ChoClaimType?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
                    It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync((string memberId, DateTime? _, DateTime? _, ChoClaimStatus? _, string? _,
                    ChoClaimType? _, decimal? _, decimal? _, int _, int _) =>
                {
                    var matches = _claims.Values.Where(c => c.MemberId == memberId).ToList();
                    return ((IReadOnlyList<ChoClaim>)matches, matches.Count);
                });

            var http = new DefaultHttpContext();
            http.Items["TenantId"] = "tenant-prov";
            http.Request.Path = requestPath;

            return new FhirExplanationOfBenefitController(
                repository.Object,
                new ExplanationOfBenefitProjector(),
                new CarinNonFinancialEobProjector(),
                new Mock<IClaimDiagnosisMetadataEnricher>().Object,
                NullLogger<FhirExplanationOfBenefitController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
        }
    }

    /// <summary>Answers fhir-service's HTTP request with the claims-service controller.</summary>
    private sealed class ClaimsServiceHandler(Path path) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            path.Upstream.Add(uri.PathAndQuery);

            var claims = path.Claims(uri.AbsolutePath);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);

            // A read ends in an id, a search ends in the resource type. Matching
            // on the last segment keeps this right for both the Patient Access
            // and the Provider Access route, which differ in depth.
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            var isRead = !string.Equals(segments[^1], "ExplanationOfBenefit", StringComparison.Ordinal);

            IActionResult result = isRead
                ? await claims.ReadEob(Uri.UnescapeDataString(segments[^1]), ct)
                : await claims.SearchEobs(
                    query.TryGetValue("patient", out var p) ? p.ToString() : null,
                    query.TryGetValue("_id", out var id) ? id.ToString() : null,
                    query.TryGetValue("_count", out var count) ? int.Parse(count!) : 50,
                    query.TryGetValue("_page", out var page) ? int.Parse(page!) : 1,
                    ct);

            var content = (ContentResult)result;
            return new HttpResponseMessage((HttpStatusCode)content.StatusCode!.Value)
            {
                Content = new StringContent(
                    content.Content ?? string.Empty, Encoding.UTF8, "application/fhir+json"),
            };
        }
    }

    // ── synthetic records ────────────────────────────────────────────────────

    private static ChoClaim ProfessionalClaim(string id, string member) => new()
    {
        Id = id,
        ClaimVersionId = id,
        TenantId = "tenant-prov",
        ClaimNumber = "CLM-" + id,
        MemberId = member,
        CoverageId = "cov-1",
        Status = ChoClaimStatus.Paid,
        ClaimType = ChoClaimType.Professional,
        BillingProviderNPI = "1234567893",
        BillingProviderName = "Example Clinic",
        RenderingProviderNPI = RenderingNpi,
        PlaceOfServiceCode = "11",
        TotalChargeAmount = 430m,
        ServiceDateFrom = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        SubmittedDate = new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc),
        LastUpdatedDate = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc),
        DiagnosisCodes =
        [
            new ChoDiagnosisCode { Code = "E11.9", PointerNumber = 1, CodeQualifier = "ABK" },
        ],
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

    private static ChoClaim InstitutionalClaim(string id, string member, string facilityTypeCode)
    {
        var claim = ProfessionalClaim(id, member);
        claim.ClaimType = ChoClaimType.Institutional;
        claim.Institutional = new ChoInstitutionalDetails
        {
            FacilityTypeCode = facilityTypeCode,
            AdmissionDate = new DateTime(2026, 9, 16, 7, 30, 0, DateTimeKind.Utc),
            StatementToDate = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc),
        };
        claim.ClaimLines[0].RevenueCode = "0450";
        return claim;
    }
}
