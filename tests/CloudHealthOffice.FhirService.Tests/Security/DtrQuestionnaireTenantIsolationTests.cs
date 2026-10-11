using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.FhirService.Tests.Security;

/// <summary>
/// D-14 / G-11 / T-3. A DTR Questionnaire search returns the caller's tenant's
/// own questionnaires plus the read-only default library, and nothing that
/// belongs to another tenant. The search used to walk every tenant's
/// questionnaires and keep any whose id the caller could also see, so where
/// ids collided (tenant B overriding a default id, or two tenants choosing the
/// same id) tenant A was served tenant B's copy.
/// </summary>
public class DtrQuestionnaireTenantIsolationTests : IClassFixture<FhirTestWebAppFactory>
{
    private static readonly JsonSerializerOptions FhirJson =
        new JsonSerializerOptions().ForFhir(typeof(Patient).Assembly);

    private readonly FhirTestWebAppFactory _factory;

    public DtrQuestionnaireTenantIsolationTests(FhirTestWebAppFactory factory) => _factory = factory;

    private IDtrService Dtr => _factory.Services.GetRequiredService<IDtrService>();

    private static Questionnaire Form(string id, string title) => new()
    {
        Id = id,
        Title = title,
        Status = PublicationStatus.Active,
        Item = [new() { LinkId = "1", Text = "Question", Type = Questionnaire.QuestionnaireItemType.Boolean }],
    };

    private async Task<List<Questionnaire>> SearchAsync(string tenantId, string query = "?_count=100")
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/fhir/r4/Questionnaire" + query);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.IssueToken("user/Questionnaire.read", tenantId: tenantId));
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var bundle = JsonSerializer.Deserialize<Bundle>(await response.Content.ReadAsStringAsync(), FhirJson)!;
        return bundle.Entry.Select(e => (Questionnaire)e.Resource).ToList();
    }

    [Fact]
    public async Task Search_DoesNotReturnAnotherTenantsOverrideOfADefaultId()
    {
        // Tenant B customises the default MRI form (an override under the same id).
        var tenantB = "dtr-b-" + Guid.NewGuid().ToString("N")[..6];
        var tenantA = "dtr-a-" + Guid.NewGuid().ToString("N")[..6];
        (await Dtr.UpdateQuestionnaireAsync("q-imaging-mri", Form("q-imaging-mri", "Tenant B private MRI form"), tenantB))
            .Should().NotBeNull();

        var results = await SearchAsync(tenantA);

        results.Select(q => q.Title).Should().NotContain("Tenant B private MRI form");
        results.Where(q => q.Id == "q-imaging-mri").Should().ContainSingle("tenant A sees exactly one MRI form: the default");
    }

    [Fact]
    public async Task Search_DoesNotReturnAnotherTenantsQuestionnaireWithACollidingId()
    {
        var tenantA = "dtr-a-" + Guid.NewGuid().ToString("N")[..6];
        var tenantB = "dtr-b-" + Guid.NewGuid().ToString("N")[..6];
        var id = "q-shared-" + Guid.NewGuid().ToString("N")[..6];
        await Dtr.CreateQuestionnaireAsync(Form(id, "Tenant A form"), tenantA);
        await Dtr.CreateQuestionnaireAsync(Form(id, "Tenant B form"), tenantB);

        var results = await SearchAsync(tenantA, $"?_id={id}");

        results.Should().ContainSingle().Which.Title.Should().Be("Tenant A form");
    }

    [Fact]
    public async Task Search_ReturnsTheTenantsOwnOverrideInPlaceOfTheDefault()
    {
        var tenantA = "dtr-a-" + Guid.NewGuid().ToString("N")[..6];
        await Dtr.UpdateQuestionnaireAsync("q-imaging-mri", Form("q-imaging-mri", "Tenant A MRI form"), tenantA);

        var results = await SearchAsync(tenantA, "?_id=q-imaging-mri");

        results.Should().ContainSingle().Which.Title.Should().Be("Tenant A MRI form");
    }

    [Fact]
    public async Task Search_StillReturnsTheDefaultLibrary()
    {
        var tenantA = "dtr-a-" + Guid.NewGuid().ToString("N")[..6];

        var results = await SearchAsync(tenantA, "?_id=q-imaging-mri");

        results.Should().ContainSingle();
    }
}
