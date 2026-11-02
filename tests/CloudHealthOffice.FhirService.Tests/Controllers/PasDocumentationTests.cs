using System.Net;
using CloudHealthOffice.FhirService.Tests.TestSupport;
using CloudHealthOffice.Testing.FhirService;
using FhirService.Controllers;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.Pas;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// G-16: a PAS request's DTR QuestionnaireResponses reach the record. The
/// mapper reads them from <c>Claim.supportingInfo</c>, <c>$submit</c> keeps
/// each in the QuestionnaireResponse store (reusing the one the provider
/// portal already saved) and the record names the stored ids.
/// </summary>
public class PasDocumentationTests
{
    private const string Tenant = "tenant-a";
    private const string MriCanonical = "https://cloudhealthoffice.com/fhir/Questionnaire/pa-imaging-mri|1.0.0";

    private readonly Mock<IPasAutoAdjudicator> _adjudicator = new();
    private readonly FakePasAuthorizationRecorder _recorder = new();
    private readonly DtrService _dtr = new(
        Options.Create(new DtrConfig()), new InMemoryDtrResourceStore(), NullLogger<DtrService>.Instance);

    // ── Mapping ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_request_without_documentation_maps_none_and_sends_none()
    {
        var bundle = PasBundles.Complete();

        var mapping = PasAuthorizationMapper.Map(bundle, PasBundles.ClaimOf(bundle), DateTime.UtcNow);

        mapping.IsValid.Should().BeTrue();
        mapping.Request!.Documentation.Should().BeNull();
        System.Text.Json.JsonSerializer.Serialize(mapping.Request).Should().NotContain("ocumentation",
            "a request without forms must hash exactly as it did before forms were recorded");
    }

    [Fact]
    public void A_completed_response_in_supporting_info_is_mapped()
    {
        var bundle = WithResponse(PasBundles.Complete(), Response());

        var mapping = PasAuthorizationMapper.Map(bundle, PasBundles.ClaimOf(bundle), DateTime.UtcNow);

        mapping.IsValid.Should().BeTrue();
        var doc = mapping.Request!.Documentation.Should().ContainSingle().Subject;
        doc.Questionnaire.Should().Be(MriCanonical);
        doc.Authored.Should().Be(new DateTime(2026, 11, 1, 15, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void A_response_about_another_patient_is_refused()
    {
        var response = Response();
        response.Subject = new ResourceReference("Patient/someone-else");
        var bundle = WithResponse(PasBundles.Complete(), response);

        var mapping = PasAuthorizationMapper.Map(bundle, PasBundles.ClaimOf(bundle), DateTime.UtcNow);

        mapping.IsValid.Should().BeFalse();
        mapping.Errors.Should().Contain(e => e.Expression.StartsWith("Claim.supportingInfo")
                                             && e.Message.Contains("patient"));
    }

    [Fact]
    public void An_in_progress_response_is_refused()
    {
        var response = Response();
        response.Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress;
        var bundle = WithResponse(PasBundles.Complete(), response);

        PasAuthorizationMapper.Map(bundle, PasBundles.ClaimOf(bundle), DateTime.UtcNow)
            .Errors.Should().Contain(e => e.Message.Contains("completed"));
    }

    // ── $submit stores and records ───────────────────────────────────────────

    [Fact]
    public async Task Submit_stores_a_response_it_has_not_seen_under_the_member_and_records_its_id()
    {
        var bundle = WithResponse(PasBundles.Complete(), Response(id: "ehr-qr-1"));

        var result = await Controller().ClaimSubmit(bundle);

        result.Should().BeOfType<OkObjectResult>();
        var doc = _recorder.Last!.Documentation.Should().ContainSingle().Subject;
        doc.QuestionnaireResponseId.Should().StartWith("qr-");
        doc.Title.Should().NotBeNullOrEmpty();

        var stored = await _dtr.GetResponseAsync(doc.QuestionnaireResponseId!, Tenant);
        stored.Should().NotBeNull();
        stored!.Subject.Reference.Should().Be($"Patient/{PasBundles.MemberId}",
            "QuestionnaireResponse reads are scoped by member, not by the EHR's Patient id");
        stored.Questionnaire.Should().Be(MriCanonical);
    }

    [Fact]
    public async Task Submit_reuses_the_response_the_portal_already_saved()
    {
        var saved = Response();
        saved.Subject = new ResourceReference($"Patient/{PasBundles.MemberId}");
        saved = await _dtr.SubmitResponseAsync(saved, Tenant);

        var inBundle = (QuestionnaireResponse)saved.DeepCopy();
        inBundle.Subject = new ResourceReference($"Patient/{PasBundles.PatientId}");
        var result = await Controller().ClaimSubmit(WithResponse(PasBundles.Complete(), inBundle));

        result.Should().BeOfType<OkObjectResult>();
        _recorder.Last!.Documentation!.Single().QuestionnaireResponseId.Should().Be(saved.Id);
        (await _dtr.SearchResponsesAsync(new QuestionnaireResponseSearchParams(), Tenant)).Total.Should().Be(1);
    }

    [Fact]
    public async Task Submit_does_not_reuse_another_members_response_by_id()
    {
        var other = Response();
        other.Subject = new ResourceReference("Patient/OTHER-MEMBER");
        other = await _dtr.SubmitResponseAsync(other, Tenant);

        var inBundle = (QuestionnaireResponse)other.DeepCopy();
        inBundle.Subject = new ResourceReference($"Patient/{PasBundles.PatientId}");
        await Controller().ClaimSubmit(WithResponse(PasBundles.Complete(), inBundle));

        var id = _recorder.Last!.Documentation!.Single().QuestionnaireResponseId;
        id.Should().NotBe(other.Id);
        (await _dtr.GetResponseAsync(id!, Tenant))!.Subject.Reference.Should().Be($"Patient/{PasBundles.MemberId}");
        (await _dtr.GetResponseAsync(other.Id, Tenant))!.Subject.Reference.Should().Be("Patient/OTHER-MEMBER");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static QuestionnaireResponse Response(string? id = null) => new()
    {
        Id = id,
        Questionnaire = MriCanonical,
        Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
        Subject = new ResourceReference($"Patient/{PasBundles.PatientId}"),
        Authored = "2026-11-01T15:30:00Z",
        Item =
        [
            new QuestionnaireResponse.ItemComponent
            {
                LinkId = "1",
                Answer = [new QuestionnaireResponse.AnswerComponent { Value = new FhirString("Low back pain, 8 weeks") }],
            },
        ],
    };

    private static Bundle WithResponse(Bundle bundle, QuestionnaireResponse response)
    {
        response.Id ??= "qr-in-bundle";
        bundle.Entry.Add(new Bundle.EntryComponent
        {
            FullUrl = $"urn:uuid:{Guid.NewGuid()}",
            Resource = response,
        });
        PasBundles.ClaimOf(bundle).SupportingInfo.Add(new Claim.SupportingInformationComponent
        {
            Sequence = 1,
            Category = new CodeableConcept(
                "http://hl7.org/fhir/us/davinci-pas/CodeSystem/PASTempCodes", "additionalInformation"),
            Value = new ResourceReference($"QuestionnaireResponse/{response.Id}"),
        });
        return bundle;
    }

    private PasController Controller()
    {
        _adjudicator
            .Setup(a => a.TryDecideAsync(
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<string>(), It.IsAny<PasDecisionBasis>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PasDecisionResult { HasDecision = false, RuleName = "NoRuleMatch" });

        var compliance = new Mock<ICms0057ComplianceChecker>();
        compliance.Setup(c => c.ValidateCompliance(It.IsAny<Resource>()))
            .Returns(new ComplianceResult(true, [], [],
                new ComplianceSummary("Claim", 10, 10, [], [], [], new TimelineCompliance(true))));

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new NotFoundHandler())
            {
                BaseAddress = new Uri("http://provider-verification-service.test/"),
            });

        var controller = new PasController(
            _adjudicator.Object,
            new PasResponseBuilder(),
            compliance.Object,
            new Mock<IPriorAuthorizationInquiryService>().Object,
            Options.Create(new PasAutoAdjudicationConfig { Enabled = true, MaxResponseMs = 12000 }),
            factory.Object,
            _recorder,
            NullLogger<PasController>.Instance,
            _dtr);

        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
