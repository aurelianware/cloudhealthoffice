using System.Net;
using System.Text;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.Fhir;
using FhirService.Controllers;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ChoAuthorization = AuthorizationService.Models.Authorization;
using PriorAuthController = AuthorizationService.Controllers.FhirPriorAuthorizationController;
using ChoDiagnosisCode = AuthorizationService.Models.DiagnosisCode;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// PAT-03 — a member reads their own prior-authorization data (drugs excluded)
/// through Patient Access, on the path the running services use:
///
///   fhir-service      ExplanationOfBenefitController   (SMART patient binding)
///        │ HTTP: GET /fhir/ExplanationOfBenefit/prior-authorization[/{id}]
///   authorization-service FhirPriorAuthorizationController
///        │                 → PriorAuthorizationApiScope (drug exclusion)
///        └───────────────→ PriorAuthorizationEobProjector
///
/// The hop between the two is a real HTTP request from fhir-service's proxy,
/// answered by authorization-service's own controller, and every resource this
/// suite asserts on is parsed back with the strict FHIR parser — so a
/// projection the parser rejects fails the test rather than reaching a member.
/// Only the Mongo repository is a stand-in; tenant scoping lives in its filter
/// and is tested against it directly. Synthetic data only.
///
/// What this replaces: the previous PAT-03 test in
/// <see cref="ProviderAndPatientAccessTests"/> asserted that "ClaimResponse" was
/// in a list of supported resource type names. That is true of the list, not of
/// the API, and it was recorded as a tautological test in
/// docs/epa-gap-matrix.md §6.1. These scenarios drive the real read and search.
///
/// What this does not show: profile conformance. The projection declares the
/// PDex prior-authorization profile as what CHO implements, and no validator
/// has been run against it (G-14); <c>insurer</c> and <c>provider</c> are not
/// US Core references, and the <c>denialreason</c> slice's required X12 CARC
/// binding is unmet because CHO's record holds 278 review codes (G-17). The
/// PAT-03 row in §11 says so.
/// </summary>
public class PatientAccessPriorAuthorizationTests
{
    private const string Member = "MEM-PAT03-1";
    private const string OtherMember = "MEM-PAT03-2";

    private static readonly FhirJsonParser Parser = new();

    // ── PAT-03: the read ─────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-03")]
    [Trait("Backend", "Replace")]
    public async Task PAT03_Replace_AMemberReadsTheirOwnPriorAuthorizationAsAPreauthorizationEob()
    {
        var path = new Path(Approved(Member));

        var result = await path.ReadAsMember(Member, "pa-AUTH-1");

        Status(result).Should().Be(HttpStatusCode.OK);
        var eob = Parser.Parse<ExplanationOfBenefit>(Body(result));

        // What makes this a prior authorization rather than a claim, and whose it is.
        eob.Use.Should().Be(ClaimUseCode.Preauthorization);
        eob.Patient.Reference.Should().Be("Patient/" + Member);
        eob.Id.Should().Be("pa-AUTH-1");
        eob.Identifier.Should().ContainSingle().Which.Value.Should().Be("AUTH-1");
        eob.Insurance.Should().ContainSingle().Which.PreAuthRef.Should().Equal(["AUTH-1"]);

        // The decision, coded and in words — the status CMS-0057-F asks for.
        eob.Status.Should().Be(ExplanationOfBenefit.ExplanationOfBenefitStatus.Active);
        eob.Outcome.Should().Be(ClaimProcessingCodes.Complete);
        eob.Disposition.Should().Be("Approved");

        // The dates: requested, and what the approval actually granted.
        eob.Created.Should().StartWith("2026-09-10");
        eob.BillablePeriod.Start.Should().Be("2026-09-20");
        eob.BillablePeriod.End.Should().Be("2026-10-20");
        eob.BenefitPeriod.Start.Should().Be("2026-09-22");
        eob.BenefitPeriod.End.Should().Be("2026-10-22");

        // The item, the quantity requested, and the quantity approved.
        var item = eob.Item.Should().ContainSingle().Subject;
        item.ProductOrService.Coding[0].Code.Should().Be("27447");
        item.Quantity.Value.Should().Be(2m);
        Adjudication(item, PriorAuthorizationEobProjector.AllowedUnitsCode)
            .Value.Should().Be(1m, "the approval was for fewer units than were requested");

        // And it came over HTTP from authorization-service, not from fhir-service.
        path.Upstream.Should().ContainSingle()
            .Which.Should().Be("/fhir/ExplanationOfBenefit/prior-authorization/pa-AUTH-1");
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    [Trait("Backend", "Replace")]
    public async Task PAT03_Replace_ADenialTravelsCodedAndInWordsInPdexsOwnAdjudicationSlices()
    {
        var path = new Path(Denied(Member));

        var result = await path.ReadAsMember(Member, "pa-AUTH-9");
        var body = Body(result);
        var eob = Parser.Parse<ExplanationOfBenefit>(body);

        eob.Outcome.Should().Be(ClaimProcessingCodes.Complete);
        eob.Disposition.Should().Be("Denied");

        var denial = Adjudication(eob.Item[0], PriorAuthorizationEobProjector.DenialReasonCode);
        denial.Category.Coding[0].System.Should()
            .Be(PriorAuthorizationEobProjector.PdexAdjudicationDiscriminatorSystem);
        denial.Reason.Coding[0].Code.Should().Be("NOTMEDNEC");
        denial.Reason.Text.Should().Be("Not medically necessary for this indication");

        // The member can also read the reason as a sentence at header level.
        eob.ProcessNote.Select(n => n.Text)
            .Should().Contain("NOTMEDNEC: Not medically necessary for this indication");

        // Every adjudication category is a slice the PDex profile's closed
        // slicing admits — a CHO-private category would be rejected by it.
        body.Should().NotContain("urn:cho:pa-unit-adjudication");
        eob.Item.SelectMany(i => i.Adjudication).Select(a => a.Category.Coding[0].System)
            .Should().AllBe(PriorAuthorizationEobProjector.PdexAdjudicationDiscriminatorSystem);
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    [Trait("Backend", "Replace")]
    public async Task PAT03_Replace_ADrugPriorAuthorizationIsAnswered404NotForbidden()
    {
        var path = new Path(Approved(Member), Drug(Member));

        var medical = await path.ReadAsMember(Member, "pa-AUTH-1");
        Status(medical).Should().Be(HttpStatusCode.OK);

        // CMS-0057-F leaves drugs out of the PA-data APIs. A 403 would tell the
        // caller the record exists, so an out-of-scope record is indistinguishable
        // from an absent one.
        var drug = await path.ReadAsMember(Member, "pa-AUTH-DRUG");
        Status(drug).Should().Be(HttpStatusCode.NotFound);
        Body(drug).Should().NotContain("J1745");
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    [Trait("Backend", "Replace")]
    public async Task PAT03_Replace_AnotherMembersPriorAuthorizationIs404ForAPatientBoundCaller()
    {
        var path = new Path(Approved(Member), Approved(OtherMember, "AUTH-OTHER"));

        // The record exists upstream and the read is by authorization number,
        // so it is fhir-service's patient binding that withholds it.
        var result = await path.ReadAsMember(Member, "pa-AUTH-OTHER");

        Status(result).Should().Be(HttpStatusCode.NotFound);
        Body(result).Should().NotContain(OtherMember);
    }

    // ── PAT-03: the search ───────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-03")]
    [Trait("Backend", "Replace")]
    public async Task PAT03_Replace_TheMembersSearchReturnsTheirOwnInScopeAuthorizationsOnly()
    {
        var path = new Path(
            Approved(Member), Denied(Member), Drug(Member), Approved(OtherMember, "AUTH-OTHER"));

        var result = await path.SearchAsMember(Member, "?use=preauthorization&_count=10");

        Status(result).Should().Be(HttpStatusCode.OK);
        var bundle = Parser.Parse<Bundle>(Body(result));

        // Their two medical authorizations: not the drug one, not anyone else's.
        bundle.Total.Should().Be(2);
        bundle.Entry.Select(e => ((ExplanationOfBenefit)e.Resource).Id)
            .Should().BeEquivalentTo(["pa-AUTH-1", "pa-AUTH-9"]);
        bundle.Entry.Select(e => ((ExplanationOfBenefit)e.Resource).Patient.Reference)
            .Should().AllBe("Patient/" + Member);
        bundle.Entry.Select(e => ((ExplanationOfBenefit)e.Resource).Use)
            .Should().AllBeEquivalentTo(ClaimUseCode.Preauthorization);

        // The member id was never typed by the caller: the patient binding
        // supplied it, and it reached authorization-service as the filter.
        path.Upstream.Should().ContainSingle()
            .Which.Should().StartWith("/fhir/ExplanationOfBenefit/prior-authorization?")
            .And.Contain("patient=" + Member);
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    [Trait("Backend", "Replace")]
    public async Task PAT03_Replace_AnExplicitPatientParameterCannotWidenAPatientBoundSearch()
    {
        var path = new Path(Approved(Member), Approved(OtherMember, "AUTH-OTHER"));

        var result = await path.SearchAsMember(Member, "?use=preauthorization&patient=" + OtherMember);

        var bundle = Parser.Parse<Bundle>(Body(result));
        bundle.Entry.Select(e => ((ExplanationOfBenefit)e.Resource).Patient.Reference)
            .Should().AllBe("Patient/" + Member);
        path.Upstream.Should().ContainSingle().Which.Should().Contain("patient=" + Member);
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    [Trait("Backend", "Replace")]
    public async Task PAT03_Replace_AStoreThatIgnoresTheMemberFilterIsCaughtByTheOwningServiceItself()
    {
        // The store ignores the member filter it was given — a contract break,
        // not something a caller can cause. authorization-service re-checks
        // every row against the member it was asked for, so nothing foreign
        // leaves it and fhir-service's own check never has to fire.
        var path = new Path(Approved(Member), Approved(OtherMember, "AUTH-OTHER"))
        {
            IgnoreMemberFilter = true,
        };

        var result = await path.SearchAsMember(Member, "?use=preauthorization");

        Status(result).Should().Be(HttpStatusCode.OK);
        Body(result).Should().NotContain(OtherMember).And.NotContain("AUTH-OTHER");
        Parser.Parse<Bundle>(Body(result)).Entry.Select(e => ((ExplanationOfBenefit)e.Resource).Id)
            .Should().Equal(["pa-AUTH-1"]);
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    [Trait("Backend", "Replace")]
    public async Task PAT03_Replace_AnUpstreamAnswerNamingAnotherMemberIsWithheldWholeRatherThanTrimmed()
    {
        // Defence in depth at the façade: whatever the upstream answers, a
        // page holding anyone but the bound patient is withheld entirely.
        // Trimming it would answer a total its entries contradict.
        var path = new Path(Approved(Member))
        {
            UpstreamSearchBody = """
            {"resourceType":"Bundle","type":"searchset","total":2,"entry":[
              {"resource":{"resourceType":"ExplanationOfBenefit","id":"pa-AUTH-1","status":"active",
                "type":{"coding":[{"code":"other"}]},"use":"preauthorization","outcome":"complete",
                "patient":{"reference":"Patient/MEM-PAT03-1"},"created":"2026-09-10",
                "insurer":{"display":"Cloud Health Office"},"provider":{"display":"Example Orthopaedics"}}},
              {"resource":{"resourceType":"ExplanationOfBenefit","id":"pa-AUTH-OTHER","status":"active",
                "type":{"coding":[{"code":"other"}]},"use":"preauthorization","outcome":"complete",
                "patient":{"reference":"Patient/MEM-PAT03-2"},"created":"2026-09-10",
                "insurer":{"display":"Cloud Health Office"},"provider":{"display":"Example Orthopaedics"}}}]}
            """,
        };

        var result = await path.SearchAsMember(Member, "?use=preauthorization");

        Status(result).Should().Be(HttpStatusCode.BadGateway);
        Body(result).Should().NotContain(OtherMember).And.NotContain("pa-AUTH-1");
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    [Trait("Backend", "Replace")]
    public async Task PAT03_Replace_ASearchWithoutAPatientContextIsRefusedRatherThanAnsweredTenantWide()
    {
        var path = new Path(Approved(Member), Approved(OtherMember, "AUTH-OTHER"));

        // No patient binding and no patient parameter: there is no tenant-wide
        // prior-auth listing on this route.
        var result = await path.SearchAsAnonymousCaller("?use=preauthorization");

        Status(result).Should().Be(HttpStatusCode.BadRequest);
        path.Upstream.Should().BeEmpty("nothing is asked of the upstream");
    }

    // ── PAT-03: freshness ────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-03")]
    [Trait("Backend", "Replace")]
    public async Task PAT03_Replace_AStatusChangeIsVisibleOnTheNextReadWithNoPropagationDelay()
    {
        // The other half of PAT-03: PA information must be current within one
        // business day. The projection is built from the authoritative record at
        // read time, so the interval is zero — and this is what demonstrates it,
        // rather than the absence of a synchronisation job.
        var authorization = Approved(Member);
        var path = new Path(authorization);

        var before = Parser.Parse<ExplanationOfBenefit>(Body(await path.ReadAsMember(Member, "pa-AUTH-1")));
        before.Disposition.Should().Be("Approved");

        authorization.Status = AuthorizationStatus.Expired;
        authorization.LastUpdatedDate = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);

        var after = Parser.Parse<ExplanationOfBenefit>(Body(await path.ReadAsMember(Member, "pa-AUTH-1")));
        after.Disposition.Should().Be("Approval period has ended");
        after.Meta.LastUpdated.Should().Be(new DateTimeOffset(authorization.LastUpdatedDate));
    }

    // ── synthetic records ────────────────────────────────────────────────────

    private static ChoAuthorization Approved(string member, string number = "AUTH-1") => new()
    {
        TenantId = Tenant,
        AuthorizationNumber = number,
        MemberId = member,
        CoverageId = "cov-1",
        LineOfBusiness = LineOfBusiness.Medicaid,
        RequestingProviderNPI = "1234567893",
        RequestingProviderName = "Example Orthopaedics",
        ServiceTypeCode = "2",
        Status = AuthorizationStatus.Approved,
        SubmittedDate = new DateTime(2026, 9, 10, 14, 30, 0, DateTimeKind.Utc),
        LastUpdatedDate = new DateTime(2026, 9, 12, 9, 0, 0, DateTimeKind.Utc),
        RequestedServiceDateFrom = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
        RequestedServiceDateTo = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc),
        ApprovedServiceDateFrom = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc),
        ApprovedServiceDateTo = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc),
        DiagnosisCodes = [new ChoDiagnosisCode { Code = "M17.11", Description = "Osteoarthritis, right knee" }],
        RequestedServices =
        [
            new RequestedService
            {
                LineNumber = 1,
                ProcedureCode = "27447",
                ProcedureDescription = "Total knee arthroplasty",
                RequestedUnits = 2,
                ApprovedUnits = 1,
                UnitType = "visits",
            },
        ],
    };

    private static ChoAuthorization Denied(string member)
    {
        var authorization = Approved(member, "AUTH-9");
        authorization.Status = AuthorizationStatus.Denied;
        authorization.DenialReasonCode = "NOTMEDNEC";
        authorization.DenialReason = "Not medically necessary for this indication";
        authorization.ApprovedServiceDateFrom = null;
        authorization.ApprovedServiceDateTo = null;
        authorization.RequestedServices[0].ApprovedUnits = null;
        authorization.RequestedServices[0].DenialReasonCode = "NOTMEDNEC";
        authorization.RequestedServices[0].DenialReason = "Not medically necessary for this indication";
        return authorization;
    }

    /// <summary>A drug authorization — an HCPCS J-code line. Out of CMS-0057-F's PA-data APIs.</summary>
    private static ChoAuthorization Drug(string member)
    {
        var authorization = Approved(member, "AUTH-DRUG");
        authorization.RequestedServices[0].ProcedureCode = "J1745";
        authorization.RequestedServices[0].ProcedureDescription = "Infliximab injection";
        return authorization;
    }

    private const string Tenant = "tenant-pat03";

    /// <summary>The named HttpClient fhir-service resolves for authorization-service (UpstreamClientNames).</summary>
    private const string AuthorizationServiceClientName = "AuthorizationService";

    // ── The path under test ──────────────────────────────────────────────────

    private sealed class Path
    {
        private readonly List<ChoAuthorization> _authorizations;

        public Path(params ChoAuthorization[] authorizations) => _authorizations = [.. authorizations];

        /// <summary>The store ignores the member filter it is given (a contract break).</summary>
        public bool IgnoreMemberFilter { get; init; }

        /// <summary>
        /// A search answer from the upstream verbatim, standing in for an
        /// upstream that does not apply the member filter at all — which the
        /// real controller cannot be made to do, and which fhir-service's own
        /// confinement check exists to catch.
        /// </summary>
        public string? UpstreamSearchBody { get; init; }

        /// <summary>The upstream requests fhir-service made (path and query).</summary>
        public List<string> Upstream { get; } = [];

        public Task<IActionResult> ReadAsMember(string member, string id) =>
            Eob(member, string.Empty).ReadEob(id, CancellationToken.None);

        public Task<IActionResult> SearchAsMember(string member, string query) =>
            Eob(member, query).SearchEobs(CancellationToken.None);

        public Task<IActionResult> SearchAsAnonymousCaller(string query) =>
            Eob(member: null, query).SearchEobs(CancellationToken.None);

        private ExplanationOfBenefitController Eob(string? member, string query)
        {
            var http = FhirRequest(member, query);
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(AuthorizationServiceClientName))
                .Returns(() => new HttpClient(new AuthorizationServiceHandler(this))
                {
                    BaseAddress = new Uri("http://authorization-service.test/"),
                });
            // A prior-authorization request must not reach claims-service: the
            // handler fails the test rather than answering.
            factory.Setup(f => f.CreateClient(ExplanationOfBenefitController.ClaimsServiceClientName))
                .Returns(() => new HttpClient(new UnexpectedUpstreamHandler("claims-service"))
                {
                    BaseAddress = new Uri("http://claims-service.test/"),
                });

            return new ExplanationOfBenefitController(
                factory.Object, NullLogger<ExplanationOfBenefitController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
        }

        private static HttpContext FhirRequest(string? member, string query)
        {
            var http = new DefaultHttpContext();
            http.Items["TenantId"] = Tenant;
            // What the SMART scope middleware sets for a patient-scoped token.
            if (member is not null) http.Items["SmartPatientId"] = member;
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("fhir.test");
            http.Request.QueryString = new QueryString(query);
            return http;
        }

        /// <summary>authorization-service's own controller over the record set.</summary>
        internal PriorAuthController Authorizations()
        {
            var repository = new Mock<IAuthorizationRepository>();
            repository.Setup(r => r.GetByAuthorizationNumberAsync(It.IsAny<string>()))
                .ReturnsAsync((string number) => _authorizations
                    .FirstOrDefault(a => string.Equals(a.AuthorizationNumber, number, StringComparison.Ordinal)));

            repository.Setup(r => r.SearchAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                    It.IsAny<AuthorizationStatus?>(), It.IsAny<LineOfBusiness?>(), It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync((string? memberId, string? _, DateTime? __, DateTime? ___,
                    AuthorizationStatus? status, LineOfBusiness? ____, int _____, int ______) =>
                    _authorizations
                        .Where(a => IgnoreMemberFilter || memberId is null || a.MemberId == memberId)
                        .Where(a => status is null || a.Status == status)
                        .ToList());

            var http = new DefaultHttpContext();
            http.Items["TenantId"] = Tenant;
            return new PriorAuthController(
                repository.Object, new PriorAuthorizationEobProjector(),
                NullLogger<PriorAuthController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
        }
    }

    /// <summary>Answers fhir-service's proxy hop with authorization-service's controller.</summary>
    private sealed class AuthorizationServiceHandler(Path path) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            path.Upstream.Add(uri.PathAndQuery);
            var controller = path.Authorizations();
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);

            if (segments.Length == 3 && path.UpstreamSearchBody is { } canned)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(canned, Encoding.UTF8, "application/fhir+json"),
                };
            }

            // .../prior-authorization/{id} is the read; .../prior-authorization is the search.
            IActionResult result = segments.Length == 4
                ? await controller.ReadPriorAuthorization(Uri.UnescapeDataString(segments[3]), ct)
                : await controller.SearchPriorAuthorizations(
                    query.TryGetValue("patient", out var patient) ? patient.ToString() : null,
                    query.TryGetValue("status", out var status) ? status.ToString() : null,
                    count: query.TryGetValue("_count", out var count) ? int.Parse(count!) : 50,
                    page: query.TryGetValue("_page", out var page) ? int.Parse(page!) : 1,
                    ct: ct);

            var content = (ContentResult)result;
            return new HttpResponseMessage((HttpStatusCode)(content.StatusCode ?? 200))
            {
                Content = new StringContent(content.Content ?? string.Empty, Encoding.UTF8, "application/fhir+json"),
            };
        }
    }

    /// <summary>An upstream this request has no business calling.</summary>
    private sealed class UnexpectedUpstreamHandler(string upstream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException(
                $"A prior-authorization request reached {upstream}: {request.RequestUri}");
    }

    // ── reading the answer ───────────────────────────────────────────────────

    private static ExplanationOfBenefit.AdjudicationComponent Adjudication(
        ExplanationOfBenefit.ItemComponent item, string code)
        => item.Adjudication.Should().ContainSingle(a => a.Category.Coding[0].Code == code).Subject;

    private static HttpStatusCode Status(IActionResult result) => result switch
    {
        ContentResult c => (HttpStatusCode)(c.StatusCode ?? 200),
        ObjectResult o => (HttpStatusCode)(o.StatusCode ?? 200),
        NotFoundResult => HttpStatusCode.NotFound,
        _ => throw new InvalidOperationException(result.GetType().Name),
    };

    private static string Body(IActionResult result) => result switch
    {
        ContentResult c => c.Content ?? string.Empty,
        ObjectResult o => new FhirJsonSerializer().SerializeToString((Base)o.Value!),
        _ => throw new InvalidOperationException(result.GetType().Name),
    };
}
