using System.Net;
using System.Text;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// Integration tests for <see cref="FhirService.Controllers.StructureDefinitionController"/>.
/// Exercises StructureDefinition, CodeSystem, and ValueSet read + search endpoints.
/// Anonymous — no bearer token on these requests to confirm the endpoints
/// are genuinely public (same posture as /fhir/r4/metadata).
/// </summary>
public class StructureDefinitionControllerTests : IClassFixture<FhirTestWebAppFactory>
{
    private readonly FhirTestWebAppFactory _factory;
    private static readonly FhirJsonParser _parser = new(new ParserSettings { PermissiveParsing = false });

    public StructureDefinitionControllerTests(FhirTestWebAppFactory factory)
    {
        _factory = factory;
    }

    // ── StructureDefinition ──────────────────────────────────────────────────

    [Fact]
    public async Task Get_StructureDefinition_by_id_returns_parseable_profile()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/fhir/r4/StructureDefinition/cho-appeal-task");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType
            .Should().BeOneOf("application/fhir+json", "application/json");

        var json = await response.Content.ReadAsStringAsync();
        var sd = _parser.Parse<StructureDefinition>(json);
        sd.Url.Should().Be(ChoFhirCanonicalUrls.AppealTask);
        sd.Type.Should().Be("Task");
    }

    [Fact]
    public async Task Get_StructureDefinition_by_unknown_id_returns_404_with_OperationOutcome()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/fhir/r4/StructureDefinition/not-a-real-id");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var json = await response.Content.ReadAsStringAsync();
        var outcome = _parser.Parse<OperationOutcome>(json);
        outcome.Issue.Should().ContainSingle();
        outcome.Issue[0].Code.Should().Be(OperationOutcome.IssueType.NotFound);
    }

    [Fact]
    public async Task Get_StructureDefinition_search_returns_Bundle_of_all_11()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/fhir/r4/StructureDefinition");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bundle = _parser.Parse<Bundle>(await response.Content.ReadAsStringAsync());

        bundle.Type.Should().Be(Bundle.BundleType.Searchset);
        bundle.Total.Should().Be(11);
        bundle.Entry.Should().HaveCount(11);
        bundle.Entry.Select(e => ((StructureDefinition)e.Resource).Id)
            .Should().Contain("cho-appeal-task");
    }

    // ── CodeSystem ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_CodeSystem_by_id_returns_concepts()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/fhir/r4/CodeSystem/cho-appeal-type");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cs = _parser.Parse<CodeSystem>(await response.Content.ReadAsStringAsync());
        cs.Concept.Select(c => c.Code).Should().Contain("reconsideration");
    }

    [Fact]
    public async Task Get_CodeSystem_search_returns_Bundle_of_6()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/fhir/r4/CodeSystem");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bundle = _parser.Parse<Bundle>(await response.Content.ReadAsStringAsync());
        bundle.Total.Should().Be(6);
    }

    [Fact]
    public async Task Get_CodeSystem_by_unknown_id_returns_404()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/fhir/r4/CodeSystem/unknown-system");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── ValueSet ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ValueSet_search_returns_Bundle_of_9()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/fhir/r4/ValueSet");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bundle = _parser.Parse<Bundle>(await response.Content.ReadAsStringAsync());
        bundle.Total.Should().Be(9);
    }

    [Fact]
    public async Task Get_ValueSet_by_id_returns_compose()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/fhir/r4/ValueSet/cho-appeal-task-status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var vs = _parser.Parse<ValueSet>(await response.Content.ReadAsStringAsync());
        vs.Compose.Include.Should().HaveCount(1);
        vs.Compose.Include[0].System.Should().Be("http://hl7.org/fhir/task-status");
    }
}
