using CloudHealthOffice.Testing.FhirService;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.Dtr;
using Hl7.Fhir.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
namespace CloudHealthOffice.FhirService.Tests.Services;

public class DtrServiceTests
{
    private readonly DtrService _service;

    public DtrServiceTests()
    {
        var config = Options.Create(new DtrConfig { Enabled = true });
        var logger = new Mock<ILogger<DtrService>>();
        _service = new DtrService(config, new InMemoryDtrResourceStore(), logger.Object);
    }

    // ── Seed data ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetQuestionnaire_SeedData_ReturnsQuestionnaire()
    {
        var q = await _service.GetQuestionnaireAsync("q-imaging-mri", "any-tenant");

        q.Should().NotBeNull();
        q!.Id.Should().Be("q-imaging-mri");
        q.Title.Should().Contain("MRI");
        q.Status.Should().Be(PublicationStatus.Active);
        q.Item.Should().HaveCountGreaterThanOrEqualTo(4);
    }

    // ── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateQuestionnaire_AssignsIdAndMeta()
    {
        var questionnaire = new Questionnaire
        {
            Status = PublicationStatus.Draft,
            Title = "Test",
            Item = new List<Questionnaire.ItemComponent>
            {
                new() { LinkId = "1", Text = "Q1", Type = Questionnaire.QuestionnaireItemType.String },
            },
        };

        var created = await _service.CreateQuestionnaireAsync(questionnaire, "test-tenant");

        created.Id.Should().NotBeNullOrEmpty();
        created.Id.Should().StartWith("q-");
        created.Meta.Should().NotBeNull();
        created.Meta.VersionId.Should().Be("1");
        created.Meta.LastUpdated.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateQuestionnaire_DuplicateId_GeneratesNewId()
    {
        var q1 = new Questionnaire
        {
            Id = "custom-id",
            Status = PublicationStatus.Draft,
            Item = new List<Questionnaire.ItemComponent>
            {
                new() { LinkId = "1", Text = "Q1", Type = Questionnaire.QuestionnaireItemType.String },
            },
        };
        await _service.CreateQuestionnaireAsync(q1, "test-tenant");

        var q2 = new Questionnaire
        {
            Id = "custom-id", // Same ID
            Status = PublicationStatus.Draft,
            Item = new List<Questionnaire.ItemComponent>
            {
                new() { LinkId = "1", Text = "Q2", Type = Questionnaire.QuestionnaireItemType.String },
            },
        };
        var created = await _service.CreateQuestionnaireAsync(q2, "test-tenant");

        created.Id.Should().NotBe("custom-id");
        created.Id.Should().StartWith("q-");
    }

    // ── Submit response ──────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitResponse_ValidResponse_StoresAndReturns()
    {
        var response = new QuestionnaireResponse
        {
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
            Questionnaire = "Questionnaire/q-imaging-mri",
            Subject = new ResourceReference("Patient/456"),
            Item = new List<QuestionnaireResponse.ItemComponent>
            {
                new() { LinkId = "1", Answer = new List<QuestionnaireResponse.AnswerComponent>
                    { new() { Value = new FhirString("back pain") } } },
            },
        };

        var submitted = await _service.SubmitResponseAsync(response, "test-tenant");

        submitted.Id.Should().NotBeNullOrEmpty();
        submitted.Meta.Should().NotBeNull();

        // Verify it's stored and retrievable
        var retrieved = await _service.GetResponseAsync(submitted.Id, "test-tenant");
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(submitted.Id);
    }

    [Fact]
    public async Task SubmitResponse_NonexistentQuestionnaire_DetectedByQuestionnaireExists()
    {
        (await _service.QuestionnaireExistsAsync("Questionnaire/nonexistent", "test-tenant"))
            .Should().BeFalse();
    }

    // ── Tenant isolation ─────────────────────────────────────────────────────

    [Fact]
    public async Task SearchQuestionnaires_FiltersByTenant()
    {
        // Create a questionnaire in tenant-a
        var q = new Questionnaire
        {
            Status = PublicationStatus.Active,
            Item = new List<Questionnaire.ItemComponent>
            {
                new() { LinkId = "1", Text = "Q", Type = Questionnaire.QuestionnaireItemType.String },
            },
        };
        await _service.CreateQuestionnaireAsync(q, "tenant-a");

        // Search in tenant-b — should only see seed data (default tenant), not tenant-a's questionnaire
        var (itemsB, _) = await _service.SearchQuestionnairesAsync(
            new QuestionnaireSearchParams { Count = 100, Page = 1 }, "tenant-b");

        // tenant-b sees seed data but not tenant-a's custom questionnaire
        var (itemsA, _) = await _service.SearchQuestionnairesAsync(
            new QuestionnaireSearchParams { Count = 100, Page = 1 }, "tenant-a");

        itemsA.Count.Should().BeGreaterThan(itemsB.Count);
    }

    // ── $questionnaire-package ───────────────────────────────────────────────

    [Fact]
    public async Task QuestionnairePackage_ReturnsPackageBundleWithQuestionnaireFirst()
    {
        var request = new QuestionnairePackageRequest(
            new Coverage { Id = "cov-1", Beneficiary = new ResourceReference("Patient/pat-1") },
            Array.Empty<Resource>(), new[] { "q-imaging-mri" }, null, null);

        var result = await _service.GetQuestionnairePackageAsync(request, "test-tenant", "https://test.example.com/fhir/r4");

        result.Package.Should().NotBeNull();
        var bundle = result.Package!.Parameter.Single(p => p.Name == "packagebundle").Resource as Bundle;
        bundle!.Type.Should().Be(Bundle.BundleType.Collection);
        bundle.Entry[0].Resource.Should().BeOfType<Questionnaire>().Which.Id.Should().Be("q-imaging-mri");
    }

    [Fact]
    public async Task QuestionnairePackage_UnknownQuestionnaire_WarnsInsteadOfFailing()
    {
        var request = new QuestionnairePackageRequest(
            new Coverage { Id = "cov-1", Beneficiary = new ResourceReference("Patient/pat-1") },
            Array.Empty<Resource>(), new[] { "nonexistent" }, null, null);

        var result = await _service.GetQuestionnairePackageAsync(request, "test-tenant", "https://test.example.com/fhir/r4");

        result.Package!.Parameter.Should().NotContain(p => p.Name == "packagebundle");
        var outcome = result.Package.Parameter.Single(p => p.Name == "outcome").Resource as OperationOutcome;
        outcome!.Issue.Should().ContainSingle(i => i.Severity == OperationOutcome.IssueSeverity.Warning
                                                  && i.Code == OperationOutcome.IssueType.NotFound);
    }
}
