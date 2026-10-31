using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FhirService.Services.Dtr;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task A_crd_assertion_id_alone_selects_the_package_crd_named()
    {
        // The whole CRD to DTR path through the host's own wiring: CRD names the
        // MRI form and keeps the assertion; DTR resolves its id as the context.
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.PostConfigure<global::FhirService.Models.CrdConfig>(c => c.DocumentationRequiredCodes = ["72148"])));
        using var client = host.CreateClient();
        var token = _factory.IssueToken("user/*.read");

        using var hook = new HttpRequestMessage(HttpMethod.Post, "/cds-services/cho-order-sign")
        {
            Content = new StringContent("""
                {
                  "hookInstance": "h-dtr-1",
                  "hook": "order-sign",
                  "context": {
                    "userId": "Practitioner/p1",
                    "patientId": "pat-1",
                    "draftOrders": { "resourceType": "Bundle", "entry": [ { "resource": {
                      "resourceType": "ServiceRequest", "id": "sr-1", "status": "draft", "intent": "order",
                      "code": { "coding": [ { "system": "http://www.ama-assn.org/go/cpt", "code": "72148" } ] },
                      "subject": { "reference": "Patient/pat-1" } } } ] }
                  },
                  "prefetch": { "coverage": { "resourceType": "Bundle", "type": "searchset", "entry": [
                    { "resource": { "resourceType": "Coverage", "id": "cov-1", "status": "active" } } ] } }
                }
                """, Encoding.UTF8, "application/json"),
        };
        hook.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var hookResponse = await client.SendAsync(hook);
        hookResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var cds = JsonDocument.Parse(await hookResponse.Content.ReadAsStringAsync());
        var parts = cds.RootElement.GetProperty("systemActions")[0].GetProperty("resource").GetProperty("extension")
            .EnumerateArray().Single(e => e.GetProperty("url").GetString()!.EndsWith("ext-coverage-information"))
            .GetProperty("extension").EnumerateArray().ToList();
        parts.Single(p => p.GetProperty("url").GetString() == "questionnaire").GetProperty("valueCanonical")
            .GetString().Should().Be("https://cloudhealthoffice.com/fhir/Questionnaire/pa-imaging-mri|1.0.0");
        var assertionId = parts.Single(p => p.GetProperty("url").GetString() == "coverage-assertion-id")
            .GetProperty("valueString").GetString()!;

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
        parameters.Add("context", new FhirString(assertionId));
        using var package = new HttpRequestMessage(HttpMethod.Post, "/fhir/r4/Questionnaire/$questionnaire-package")
        {
            Content = new StringContent(JsonSerializer.Serialize(parameters, FhirJson), Encoding.UTF8, "application/fhir+json"),
        };
        package.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var packageResponse = await client.SendAsync(package);

        packageResponse.StatusCode.Should().Be(HttpStatusCode.OK, await packageResponse.Content.ReadAsStringAsync());
        var output = new FhirJsonParser().Parse<Parameters>(await packageResponse.Content.ReadAsStringAsync());
        var questionnaire = output.Parameter.Single(p => p.Name == "packagebundle").Resource.Should().BeOfType<Bundle>()
            .Subject.Entry[0].Resource.Should().BeOfType<Questionnaire>().Subject;
        questionnaire.Url.Should().Be("https://cloudhealthoffice.com/fhir/Questionnaire/pa-imaging-mri");
    }
}
