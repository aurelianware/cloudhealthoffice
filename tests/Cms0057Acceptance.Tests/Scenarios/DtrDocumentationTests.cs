using FhirService.Controllers;
using FhirService.Models;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// PAS-02 — DTR (Documentation Templates &amp; Rules).
///
/// The payer returns the correct Questionnaire for the coverage/service; a
/// completed QuestionnaireResponse validates and is retrievable so it can be
/// carried into the PAS-03 submission bundle. Executed against the REAL
/// DtrService (in-memory, seeded) + DtrController in Demo/Cho mode.
///
/// Traceability:
///   controller  src/services/fhir-service/Controllers/DtrController.cs
///   service     src/services/fhir-service/Services/DtrService.cs
/// </summary>
public class DtrDocumentationTests
{
    private static DtrController BuildController()
    {
        var config = Options.Create(new DtrConfig
        {
            Enabled = true,
            MaxQuestionnaireItems = 500,
            MaxResponseSizeBytes = 1_048_576,
        });

        // Empty IConfiguration → DtrService uses in-memory storage + seed data.
        var service = new DtrService(config, AcceptanceContext.Logger<DtrService>(), AcceptanceContext.EmptyConfig());
        var bundleBuilder = new FhirBundleBuilder(AcceptanceContext.DemoConfig());

        return new DtrController(service, bundleBuilder, AcceptanceContext.Logger<DtrController>())
            .WithTenant();
    }

    [Fact]
    [Trait("Scenario", "PAS-02")]
    public async Task PAS02_GetQuestionnaire_ForCoveredService_ReturnsDtrQuestionnaire()
    {
        var controller = BuildController();

        var result = await controller.GetQuestionnaire("q-imaging-mri", CancellationToken.None);

        var q = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<Questionnaire>().Subject;
        q.Id.Should().Be("q-imaging-mri");
        q.Status.Should().Be(PublicationStatus.Active);
        q.Item.Should().NotBeEmpty();
        q.Meta!.Profile.Should().Contain(
            "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-std-questionnaire");
    }

    [Fact]
    [Trait("Scenario", "PAS-02")]
    public async Task PAS02_QuestionnairePackage_ReturnsBundleWithQuestionnaire()
    {
        var controller = BuildController();

        var parameters = new Parameters();
        parameters.Add("questionnaire", new FhirString("q-imaging-mri"));

        var result = await controller.QuestionnairePackage(parameters, CancellationToken.None);

        var bundle = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<Bundle>().Subject;
        bundle.Entry.Should().Contain(e => e.Resource is Questionnaire);
    }

    [Fact]
    [Trait("Scenario", "PAS-02")]
    public async Task PAS02_SubmitCompletedResponse_ValidatesAndIsConsumable()
    {
        var controller = BuildController();

        var response = new QuestionnaireResponse
        {
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
            Questionnaire = "Questionnaire/q-imaging-mri",
            Subject = new ResourceReference("Patient/pat-001"),
            Authored = "2026-09-01T00:00:00Z",
            Item = new List<QuestionnaireResponse.ItemComponent>
            {
                new()
                {
                    LinkId = "1",
                    Answer = new List<QuestionnaireResponse.AnswerComponent>
                    {
                        new() { Value = new FhirString("Chronic lower back pain, radiculopathy") },
                    },
                },
            },
        };

        var submit = await controller.SubmitQuestionnaireResponse(response, CancellationToken.None);

        var created = submit.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(201);
        var qr = created.Value.Should().BeOfType<QuestionnaireResponse>().Subject;
        qr.Id.Should().NotBeNullOrEmpty();

        // Consumable by PAS-03: the persisted response is retrievable and carries
        // the questionnaire + subject references PAS needs to attach it.
        var read = await controller.GetQuestionnaireResponse(qr.Id, CancellationToken.None);
        var readBack = read.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<QuestionnaireResponse>().Subject;
        readBack.Questionnaire.Should().Be("Questionnaire/q-imaging-mri");
        readBack.Subject!.Reference.Should().Be("Patient/pat-001");
    }

    // ── Negative paths ────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-02")]
    public async Task PAS02_Negative_InProgressResponse_Returns400()
    {
        var controller = BuildController();

        var response = new QuestionnaireResponse
        {
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress,
            Questionnaire = "Questionnaire/q-imaging-mri",
            Subject = new ResourceReference("Patient/pat-001"),
        };

        var result = await controller.SubmitQuestionnaireResponse(response, CancellationToken.None);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    [Trait("Scenario", "PAS-02")]
    public async Task PAS02_Negative_UnknownQuestionnaireReference_Returns400()
    {
        var controller = BuildController();

        var response = new QuestionnaireResponse
        {
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
            Questionnaire = "Questionnaire/does-not-exist",
            Subject = new ResourceReference("Patient/pat-001"),
        };

        var result = await controller.SubmitQuestionnaireResponse(response, CancellationToken.None);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }
}
