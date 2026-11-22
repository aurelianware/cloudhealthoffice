using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.FhirService.Tests.TestSupport;
using FhirService.Services.Pas;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CloudHealthOffice.FhirService.Tests.Security;

/// <summary>
/// A $submit bundle may carry the resource types the PAS 2.2.1 request bundle
/// profiles, and nothing else. The Inferno PAS kit's own examples name the
/// requestor as a PractitionerRole and order equipment as a DeviceRequest;
/// both were refused before (Inferno run 9: every $submit answered 400).
/// </summary>
public class PasBundleResourceTypeTests : IClassFixture<FhirTestWebAppFactory>
{
    private static readonly JsonSerializerOptions FhirJson =
        new JsonSerializerOptions().ForFhir(typeof(Patient).Assembly);

    private readonly FhirTestWebAppFactory _factory;

    public PasBundleResourceTypeTests(FhirTestWebAppFactory factory) => _factory = factory;

    [Theory]
    [MemberData(nameof(PasProfiledResources))]
    public async System.Threading.Tasks.Task A_resource_type_the_pas_request_bundle_profiles_is_accepted(Resource resource)
    {
        var response = await SubmitAsync(WithEntry(resource));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task A_resource_type_outside_the_pas_request_bundle_is_refused()
    {
        var response = await SubmitAsync(WithEntry(new Account { Id = "acct-1", Status = Account.AccountStatus.Active }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("disallowed resource type: Account");
    }

    public static TheoryData<Resource> PasProfiledResources() => new()
    {
        new PractitionerRole { Id = "role-1" },
        new DeviceRequest
        {
            Id = "dr-1", Status = RequestStatus.Active, Intent = RequestIntent.Order,
            Code = new CodeableConcept("http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets", "E0250"),
            Subject = new ResourceReference("Patient/p1"),
        },
        new MedicationRequest
        {
            Id = "mr-1", Status = MedicationRequest.MedicationrequestStatus.Active,
            Intent = MedicationRequest.MedicationRequestIntent.Order,
            Medication = new CodeableConcept("http://www.nlm.nih.gov/research/umls/rxnorm", "197361"),
            Subject = new ResourceReference("Patient/p1"),
        },
        new Location { Id = "loc-1" },
        new Encounter { Id = "enc-1", Status = Encounter.EncounterStatus.Planned, Class = new Coding("http://terminology.hl7.org/CodeSystem/v3-ActCode", "AMB") },
    };

    private static string WithEntry(Resource resource)
    {
        var bundle = PasBundles.Complete(procedureCode: "70553", serviceState: "TX");
        bundle.Entry.Add(new Bundle.EntryComponent
        {
            FullUrl = $"http://example.org/fhir/{resource.TypeName}/{resource.Id}",
            Resource = resource,
        });
        return JsonSerializer.Serialize(bundle, FhirJson);
    }

    private async Task<HttpResponseMessage> SubmitAsync(string body)
    {
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IPasAuthorizationRecorder>();
            services.AddSingleton<IPasAuthorizationRecorder>(new FakePasAuthorizationRecorder());
            services.AddHttpClient("AuthorizationService")
                .ConfigurePrimaryHttpMessageHandler(() => new NotFoundHandler());
            services.AddHttpClient("ProviderVerificationService")
                .ConfigurePrimaryHttpMessageHandler(() => new NotFoundHandler());
        }));
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/fhir/r4/Claim/$submit")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/fhir+json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.IssueToken("system/Claim.write", tenantId: "test-tenant"));
        return await client.SendAsync(request);
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
