using FhirService.Models;
using FhirService.Services;
using Hl7.Fhir.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.Services;

public class DtrServiceMongoTests
{
    private readonly DtrService _service;

    public DtrServiceMongoTests()
    {
        var config = Options.Create(new DtrConfig { Enabled = true });
        var logger = new Mock<ILogger<DtrService>>();

        // Empty MongoDB connection string → in-memory fallback
        var appConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDb:ConnectionString"] = "",
                ["MongoDb:DatabaseName"] = "test",
            })
            .Build();

        _service = new DtrService(config, logger.Object, appConfig);
    }

    [Fact]
    public async System.Threading.Tasks.Task CreateQuestionnaire_InMemory_StoresAndRetrieves()
    {
        var questionnaire = new Questionnaire
        {
            Status = PublicationStatus.Draft,
            Title = "Test Questionnaire",
            Item = new List<Questionnaire.ItemComponent>
            {
                new() { LinkId = "1", Text = "Q1", Type = Questionnaire.QuestionnaireItemType.String },
            },
        };

        var created = await _service.CreateQuestionnaireAsync(questionnaire, "test-tenant");
        created.Id.Should().NotBeNullOrEmpty();

        var retrieved = await _service.GetQuestionnaireAsync(created.Id, "test-tenant");
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(created.Id);
    }

    [Fact]
    public async System.Threading.Tasks.Task SeedData_LoadedOnConstruction()
    {
        var q = await _service.GetQuestionnaireAsync("q-imaging-mri", "any-tenant");
        q.Should().NotBeNull();
        q!.Title.Should().Contain("MRI");

        var (items, total) = await _service.SearchQuestionnairesAsync(
            new QuestionnaireSearchParams { Count = 100, Page = 1 }, "any-tenant");

        total.Should().Be(5); // 5 seed questionnaires
    }

    [Fact]
    public async System.Threading.Tasks.Task SubmitResponse_InMemory_ValidatesAndStores()
    {
        var response = new QuestionnaireResponse
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
                        new() { Value = new FhirString("back pain") },
                    },
                },
            },
        };

        var submitted = await _service.SubmitResponseAsync(response, "test-tenant");
        submitted.Id.Should().NotBeNullOrEmpty();

        var retrieved = await _service.GetResponseAsync(submitted.Id, "test-tenant");
        retrieved.Should().NotBeNull();
    }
}
