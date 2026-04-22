using System.Net;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// Verifies the CapabilityStatement correctly advertises CHO appeal
/// profiles (via supportedProfile on Task/Communication/DocumentReference/ClaimResponse)
/// and the cho-appeal-submit operation. Includes a regression guard on
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
    [InlineData("Task",              "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-task")]
    [InlineData("Communication",     "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-communication")]
    [InlineData("DocumentReference", "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-document-reference")]
    [InlineData("ClaimResponse",     "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-claim-response")]
    public async Task Resource_entry_advertises_CHO_appeal_profile(string resourceType, string expectedProfileUrl)
    {
        var cs = await GetCapabilityStatement();

        var rest = cs.Rest.Should().ContainSingle().Subject;
        var resource = rest.Resource.Should().ContainSingle(r => r.Type.ToString() == resourceType).Subject;
        resource.SupportedProfile.Should().Contain(expectedProfileUrl);
    }

    [Fact]
    public async Task Operation_list_includes_cho_appeal_submit()
    {
        var cs = await GetCapabilityStatement();

        var op = cs.Rest[0].Operation
            .Should().ContainSingle(o => o.Name == "cho-appeal-submit").Subject;
        op.Definition.Should().Be(ChoFhirCanonicalUrls.AppealSubmitOperation);
        op.Documentation.ToString().Should().Contain("appeal");
    }

    [Fact]
    public async Task Existing_resources_and_operations_unchanged_regression_guard()
    {
        var cs = await GetCapabilityStatement();
        var rest = cs.Rest[0];

        // Existing resources still advertised
        rest.Resource.Select(r => r.Type.ToString()).Should().Contain(new[]
        {
            "Patient", "Coverage", "ExplanationOfBenefit",
            "Encounter", "Claim", "Questionnaire", "QuestionnaireResponse"
        });

        // Existing bulk-export operation still listed
        rest.Operation.Should().Contain(o =>
            o.Name == "export" &&
            o.Definition == "http://hl7.org/fhir/uv/bulkdata/OperationDefinition/export");

        // Implementation description unchanged
        cs.Implementation.Description.Should().Contain("CMS-0057-F");
    }
}
