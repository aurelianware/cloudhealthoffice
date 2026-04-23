using System.Net;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// Verifies that the CapabilityStatement advertises the FHIR conformance
/// resource endpoints (StructureDefinition, CodeSystem, ValueSet,
/// OperationDefinition) that PR 1 actually implements — and that it does
/// NOT advertise the Task/Communication/DocumentReference/ClaimResponse
/// profiles or the cho-appeal-submit operation, since runtime read/search
/// and the operation itself land in PR 2. Includes a regression guard on
/// existing resource entries and the bulk-export operation.
/// </summary>
public class MetadataCapabilityStatementAppealExtensionTests : IClassFixture<FhirTestWebAppFactory>
{
    private readonly FhirTestWebAppFactory _factory;
    private static readonly FhirJsonParser _parser = new(new ParserSettings { PermissiveParsing = false });

    public MetadataCapabilityStatementAppealExtensionTests(FhirTestWebAppFactory factory)
    {
        _factory = factory;
    }

    private async Task<CapabilityStatement> GetCapabilityStatement()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/fhir/r4/metadata");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return _parser.Parse<CapabilityStatement>(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("StructureDefinition")]
    [InlineData("CodeSystem")]
    [InlineData("ValueSet")]
    [InlineData("OperationDefinition")]
    public async Task Conformance_resource_endpoints_are_advertised_with_read_and_search(string resourceType)
    {
        var cs = await GetCapabilityStatement();
        var rest = cs.Rest.Should().ContainSingle().Subject;
        var resource = rest.Resource.Should().ContainSingle(r => r.Type.ToString() == resourceType).Subject;

        var codes = resource.Interaction.Select(i => i.Code).ToList();
        codes.Should().Contain(CapabilityStatement.TypeRestfulInteraction.Read);
        codes.Should().Contain(CapabilityStatement.TypeRestfulInteraction.SearchType);
    }

    [Theory]
    [InlineData("Task")]
    [InlineData("Communication")]
    [InlineData("DocumentReference")]
    [InlineData("ClaimResponse")]
    public async Task Appeal_profile_resources_are_NOT_yet_advertised_in_PR_1(string resourceType)
    {
        // Advertising read/search interactions or supportedProfile for these
        // resource types before the runtime endpoints exist would be a false
        // conformance claim. The profile JSON is still discoverable via
        // GET /fhir/r4/StructureDefinition; PR 2 adds the resource entries
        // once persistence and read/search land.
        var cs = await GetCapabilityStatement();
        var rest = cs.Rest.Should().ContainSingle().Subject;

        rest.Resource.Should().NotContain(
            r => r.Type.ToString() == resourceType,
            $"{resourceType} read/search is not implemented in PR 1; " +
            "advertising it would mislead clients and compliance tooling");
    }

    [Fact]
    public async Task cho_appeal_submit_operation_is_NOT_yet_advertised_in_PR_1()
    {
        // Same reasoning as above: the operation is defined (OperationDefinition
        // JSON served at its canonical URL) but not implemented. Advertising it
        // in rest[0].operation[] would cause clients to invoke it and receive
        // 404/405. PR 2 adds the advertisement alongside the implementation.
        var cs = await GetCapabilityStatement();

        cs.Rest[0].Operation.Should().NotContain(o => o.Name == "cho-appeal-submit");
    }

    [Fact]
    public async Task Existing_resources_and_operations_unchanged_regression_guard()
    {
        var cs = await GetCapabilityStatement();
        var rest = cs.Rest[0];

        rest.Resource.Select(r => r.Type.ToString()).Should().Contain(new[]
        {
            "Patient", "Coverage", "ExplanationOfBenefit",
            "Encounter", "Claim", "Questionnaire", "QuestionnaireResponse"
        });

        rest.Operation.Should().Contain(o =>
            o.Name == "export" &&
            o.Definition == "http://hl7.org/fhir/uv/bulkdata/OperationDefinition/export");

        cs.Implementation.Description.Should().Contain("CMS-0057-F");
    }
}
