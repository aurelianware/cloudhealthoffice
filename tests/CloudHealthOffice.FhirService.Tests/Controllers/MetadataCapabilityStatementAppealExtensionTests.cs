using System.Net;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// Verifies that the CapabilityStatement advertises the FHIR conformance
/// resource endpoints (StructureDefinition, CodeSystem, ValueSet,
/// OperationDefinition) AND the PR 3 appeal-projection resources
/// (Task, Communication, DocumentReference, ClaimResponse) plus the
/// `cho-appeal-submit` operation. The assertions here FLIP from
/// negative (PR 1 era — "not yet advertised") to positive in PR 3.
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

    /// <summary>
    /// PR 3 flip: Task, Communication, DocumentReference, and
    /// ClaimResponse are now advertised with read + search interactions,
    /// each declaring its cho-appeal-* supportedProfile.
    /// </summary>
    [Theory]
    [InlineData("Task", "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-task")]
    [InlineData("Communication", "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-communication")]
    [InlineData("DocumentReference", "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-document-reference")]
    [InlineData("ClaimResponse", "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-claim-response")]
    public async Task Appeal_projection_resources_are_advertised_with_read_search_and_profile(
        string resourceType, string profileUrl)
    {
        var cs = await GetCapabilityStatement();
        var rest = cs.Rest.Should().ContainSingle().Subject;

        var resource = rest.Resource.Should().ContainSingle(
            r => r.Type.ToString() == resourceType).Subject;

        resource.Interaction.Should().Contain(i =>
            i.Code == CapabilityStatement.TypeRestfulInteraction.Read);
        resource.Interaction.Should().Contain(i =>
            i.Code == CapabilityStatement.TypeRestfulInteraction.SearchType);
        resource.SupportedProfile.Should().Contain(profileUrl);
    }

    /// <summary>
    /// G-36: the resources CHO serves in a CARIN BB / US Core shape declare
    /// those profiles, the ones the projections put in meta.profile.
    /// </summary>
    [Theory]
    [InlineData("Patient", "http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient")]
    [InlineData("Patient", "http://hl7.org/fhir/us/carin-bb/StructureDefinition/C4BB-Patient")]
    [InlineData("Coverage", "http://hl7.org/fhir/us/carin-bb/StructureDefinition/C4BB-Coverage")]
    [InlineData("ExplanationOfBenefit", "http://hl7.org/fhir/us/carin-bb/StructureDefinition/C4BB-ExplanationOfBenefit-Inpatient-Institutional-Basis")]
    [InlineData("ExplanationOfBenefit", "http://hl7.org/fhir/us/carin-bb/StructureDefinition/C4BB-ExplanationOfBenefit-Professional-NonClinician-Basis")]
    [InlineData("ExplanationOfBenefit", "http://hl7.org/fhir/us/davinci-pdex/StructureDefinition/pdex-priorauthorization")]
    [InlineData("Practitioner", "http://hl7.org/fhir/us/carin-bb/StructureDefinition/C4BB-Practitioner")]
    [InlineData("Organization", "http://hl7.org/fhir/us/carin-bb/StructureDefinition/C4BB-Organization")]
    public async Task Served_profiles_are_declared(string resourceType, string profileUrl)
    {
        var cs = await GetCapabilityStatement();
        cs.Rest[0].Resource.Single(r => r.Type.ToString() == resourceType)
            .SupportedProfile.Should().Contain(profileUrl);
    }

    [Fact]
    public async Task cho_appeal_submit_operation_is_advertised()
    {
        var cs = await GetCapabilityStatement();
        cs.Rest[0].Operation.Should().Contain(o =>
            o.Name == "cho-appeal-submit" &&
            o.Definition == "http://fhir.cloudhealthoffice.com/OperationDefinition/cho-appeal-submit");
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

        // System-level $export is refused by the server, so it is not claimed;
        // with bulk export off neither is Patient or Group export. HRex
        // $member-match is always served, so Patient declares it alone.
        rest.Operation.Should().NotContain(o => o.Name == "export");
        rest.Resource.Single(r => r.Type.ToString() == "Patient").Operation
            .Select(o => (o.Name, o.Definition)).Should().Equal(
                ("member-match", "http://hl7.org/fhir/us/davinci-hrex/OperationDefinition/member-match"));
        rest.Resource.Should().NotContain(r => r.Type.ToString() == "Group");

        cs.Implementation.Description.Should().Contain("CMS-0057-F");
    }
}
