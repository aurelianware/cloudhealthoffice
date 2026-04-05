using FhirService.Controllers;
using FhirService.Models;
using FhirService.Services;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using FluentAssertions;
namespace CloudHealthOffice.FhirService.Tests.Controllers;

public class DtrControllerTests
{
    private readonly DtrService _dtrService;
    private readonly FhirBundleBuilder _bundleBuilder;
    private readonly DtrController _controller;

    public DtrControllerTests()
    {
        var config = Options.Create(new DtrConfig { Enabled = true });
        var loggerService = new Mock<ILogger<DtrService>>();
        var loggerController = new Mock<ILogger<DtrController>>();
        var fhirConfig = new Mock<IConfiguration>();
        fhirConfig.Setup(c => c["Fhir:ServerBaseUrl"]).Returns("https://test.example.com/fhir/r4");

        var appConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MongoDb:ConnectionString"] = "" })
            .Build();
        _dtrService = new DtrService(config, loggerService.Object, appConfig);
        _bundleBuilder = new FhirBundleBuilder(fhirConfig.Object);

        _controller = new DtrController(
            _dtrService,
            _bundleBuilder,
            loggerController.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = "test-tenant";
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("test.example.com");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
    }

    // ── Questionnaire Read ───────────────────────────────────────────────────

    [Fact]
    public async Task GetQuestionnaire_ExistingId_Returns200()
    {
        var result = await _controller.GetQuestionnaire("q-imaging-mri", CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var q = okResult.Value.Should().BeOfType<Questionnaire>().Subject;
        q.Id.Should().Be("q-imaging-mri");
        q.Title.Should().Contain("MRI");
        q.Status.Should().Be(PublicationStatus.Active);
    }

    [Fact]
    public async Task GetQuestionnaire_UnknownId_Returns404()
    {
        var result = await _controller.GetQuestionnaire("nonexistent", CancellationToken.None);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(404);
        statusResult.Value.Should().BeOfType<OperationOutcome>();
    }

    // ── Questionnaire Search ─────────────────────────────────────────────────

    [Fact]
    public async Task SearchQuestionnaires_ReturnsBundle()
    {
        var search = new QuestionnaireSearchParams { Count = 20, Page = 1 };
        var result = await _controller.SearchQuestionnaires(search, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var bundle = okResult.Value.Should().BeOfType<Bundle>().Subject;
        bundle.Type.Should().Be(Bundle.BundleType.Searchset);
        bundle.Entry.Should().HaveCount(5); // 5 seed questionnaires
    }

    [Fact]
    public async Task SearchQuestionnaires_ByStatus_FiltersCorrectly()
    {
        var search = new QuestionnaireSearchParams { Status = "active", Count = 20, Page = 1 };
        var result = await _controller.SearchQuestionnaires(search, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var bundle = okResult.Value.Should().BeOfType<Bundle>().Subject;
        bundle.Entry.Should().HaveCount(4); // 4 active, 1 draft excluded
    }

    // ── Questionnaire Create ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateQuestionnaire_ValidResource_Returns201()
    {
        var questionnaire = new Questionnaire
        {
            Status = PublicationStatus.Draft,
            Title = "Test Questionnaire",
            Item = new List<Questionnaire.ItemComponent>
            {
                new() { LinkId = "1", Text = "Test question", Type = Questionnaire.QuestionnaireItemType.String },
            },
        };

        var result = await _controller.CreateQuestionnaire(questionnaire, CancellationToken.None);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(201);
        var created = statusResult.Value.Should().BeOfType<Questionnaire>().Subject;
        created.Id.Should().NotBeNullOrEmpty();
        created.Meta.Should().NotBeNull();
        created.Meta.VersionId.Should().Be("1");
    }

    [Fact]
    public async Task CreateQuestionnaire_MissingStatus_Returns400()
    {
        var questionnaire = new Questionnaire
        {
            Title = "No Status",
            Item = new List<Questionnaire.ItemComponent>
            {
                new() { LinkId = "1", Text = "Q", Type = Questionnaire.QuestionnaireItemType.String },
            },
        };

        var result = await _controller.CreateQuestionnaire(questionnaire, CancellationToken.None);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateQuestionnaire_NoItems_Returns400()
    {
        var questionnaire = new Questionnaire
        {
            Status = PublicationStatus.Draft,
            Title = "No Items",
        };

        var result = await _controller.CreateQuestionnaire(questionnaire, CancellationToken.None);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(400);
    }

    // ── QuestionnaireResponse ────────────────────────────────────────────────

    [Fact]
    public async Task GetQuestionnaireResponse_ExistingId_Returns200()
    {
        // First submit a response
        var response = CreateValidResponse();
        var submitResult = await _controller.SubmitQuestionnaireResponse(response, CancellationToken.None);
        var submitted = (submitResult as ObjectResult)!.Value as QuestionnaireResponse;

        var result = await _controller.GetQuestionnaireResponse(submitted!.Id, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().BeOfType<QuestionnaireResponse>();
    }

    [Fact]
    public async Task SubmitQuestionnaireResponse_ValidCompleted_Returns201()
    {
        var response = CreateValidResponse();
        var result = await _controller.SubmitQuestionnaireResponse(response, CancellationToken.None);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(201);
        var submitted = statusResult.Value.Should().BeOfType<QuestionnaireResponse>().Subject;
        submitted.Id.Should().NotBeNullOrEmpty();
        submitted.Meta.Should().NotBeNull();
    }

    [Fact]
    public async Task SubmitQuestionnaireResponse_StatusInProgress_Returns400()
    {
        var response = CreateValidResponse();
        response.Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress;

        var result = await _controller.SubmitQuestionnaireResponse(response, CancellationToken.None);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task SubmitQuestionnaireResponse_InvalidQuestionnaireRef_Returns400()
    {
        var response = new QuestionnaireResponse
        {
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
            Questionnaire = "Questionnaire/nonexistent",
            Subject = new ResourceReference("Patient/456"),
            Item = new List<QuestionnaireResponse.ItemComponent>
            {
                new() { LinkId = "1", Answer = new List<QuestionnaireResponse.AnswerComponent>
                    { new() { Value = new FhirString("test") } } },
            },
        };

        var result = await _controller.SubmitQuestionnaireResponse(response, CancellationToken.None);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(400);
    }

    // ── $questionnaire-package ───────────────────────────────────────────────

    [Fact]
    public async Task QuestionnairePackage_ExistingId_ReturnsBundleWithQuestionnaire()
    {
        var parameters = new Parameters();
        parameters.Parameter.Add(new Parameters.ParameterComponent
        {
            Name = "questionnaire",
            Value = new FhirString("q-imaging-mri"),
        });

        var result = await _controller.QuestionnairePackage(parameters, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var bundle = okResult.Value.Should().BeOfType<Bundle>().Subject;
        bundle.Type.Should().Be(Bundle.BundleType.Collection);
        bundle.Entry.Should().Contain(e => e.Resource is Questionnaire);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static QuestionnaireResponse CreateValidResponse() => new()
    {
        Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
        Questionnaire = "Questionnaire/q-imaging-mri",
        Subject = new ResourceReference("Patient/456"),
        Item = new List<QuestionnaireResponse.ItemComponent>
        {
            new()
            {
                LinkId = "1",
                Answer = new List<QuestionnaireResponse.AnswerComponent>
                {
                    new() { Value = new FhirString("Lower back pain") },
                },
            },
        },
    };
}
