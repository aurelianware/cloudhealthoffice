using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FhirService.Services.Dtr;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// DTR <c>$questionnaire-package</c> over HTTP, as a DTR client calls it: a
/// SMART token with read scopes, a DTR input Parameters, and the
/// CapabilityStatement entry the DTR payer-server kit reads first.
/// </summary>
public class DtrQuestionnairePackageHttpTests : IClassFixture<FhirTestWebAppFactory>
{
    private static readonly JsonSerializerOptions FhirJson =
        new JsonSerializerOptions().ForFhir(typeof(Patient).Assembly);

    private readonly FhirTestWebAppFactory _factory;

    public DtrQuestionnairePackageHttpTests(FhirTestWebAppFactory factory) => _factory = factory;

    private async Task<HttpResponseMessage> PostPackageAsync(Parameters parameters)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/fhir/r4/Questionnaire/$questionnaire-package")
        {
            Content = new StringContent(JsonSerializer.Serialize(parameters, FhirJson), Encoding.UTF8, "application/fhir+json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _factory.IssueToken("user/*.read"));
        return await client.SendAsync(request);
    }

    private static Parameters MriRequest()
    {
        var parameters = new Parameters();
        parameters.Parameter.Add(new Parameters.ParameterComponent
        {
            Name = "coverage",
            Resource = new Coverage
            {
                Id = "cov-1",
                Status = FinancialResourceStatusCodes.Active,
                Beneficiary = new ResourceReference("Patient/pat-1"),
                Payor = [new ResourceReference("Organization/payer-1")],
            },
        });
        parameters.Parameter.Add(new Parameters.ParameterComponent
        {
            Name = "order",
            Resource = new ServiceRequest
            {
                Id = "sr-1",
                Status = RequestStatus.Draft,
                Intent = RequestIntent.Order,
                Code = new CodeableConcept("http://www.ama-assn.org/go/cpt", "72148"),
                Subject = new ResourceReference("Patient/pat-1"),
            },
        });
        return parameters;
    }

    [Fact]
    public async Task A_dtr_client_gets_the_mri_package_as_fhir_json()
    {
        var response = await PostPackageAsync(MriRequest());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var output = new FhirJsonParser().Parse<Parameters>(await response.Content.ReadAsStringAsync());
        var bundle = output.Parameter.Single(p => p.Name == "packagebundle").Resource.Should().BeOfType<Bundle>().Subject;
        bundle.Entry[0].Resource.Should().BeOfType<Questionnaire>();
        bundle.Entry.Select(e => e.Resource.TypeName).Should().Contain(["QuestionnaireResponse", "Library", "ValueSet"]);
    }

    [Fact]
    public async Task A_request_breaking_the_input_profile_is_a_400_with_an_operation_outcome()
    {
        var parameters = new Parameters();
        parameters.Add("questionnaire", new Canonical("https://cloudhealthoffice.com/fhir/Questionnaire/pa-imaging-mri"));

        var response = await PostPackageAsync(parameters);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        new FhirJsonParser().Parse<OperationOutcome>(await response.Content.ReadAsStringAsync())
            .Issue.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_capability_statement_declares_the_operation_under_the_dtr_canonical()
    {
        using var client = _factory.CreateClient();
        var metadata = await client.GetAsync("/fhir/r4/metadata");
        var cs = new FhirJsonParser().Parse<CapabilityStatement>(await metadata.Content.ReadAsStringAsync());

        var questionnaire = cs.Rest.Single().Resource.Single(r => r.Type == "Questionnaire");
        questionnaire.Operation.Should().ContainSingle(o =>
            o.Name == "questionnaire-package"
            && o.Definition == "http://hl7.org/fhir/us/davinci-dtr/OperationDefinition/questionnaire-package");
        questionnaire.SupportedProfile.Should().Contain(
            "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-std-questionnaire");
    }
}
