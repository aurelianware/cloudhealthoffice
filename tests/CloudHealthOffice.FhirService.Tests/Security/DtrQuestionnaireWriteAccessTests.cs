using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.FhirService.Tests.Security;

/// <summary>
/// D-15 / G-11. A payer's DTR Questionnaires are the payer's documentation
/// rules. Creating or changing them is a CHO administrative act
/// (<c>settings:manage</c>); a SMART token — a provider's EHR app or a
/// backend client — may read them but never write them. Writing a
/// QuestionnaireResponse (answering one) is what DTR is for and stays open to
/// SMART callers.
/// </summary>
public class DtrQuestionnaireWriteAccessTests : IClassFixture<FhirTestWebAppFactory>
{
    private const string Tenant = "test-tenant";

    private static readonly JsonSerializerOptions FhirJson =
        new JsonSerializerOptions().ForFhir(typeof(Patient).Assembly);

    private readonly FhirTestWebAppFactory _factory;

    public DtrQuestionnaireWriteAccessTests(FhirTestWebAppFactory factory) => _factory = factory;

    private static StringContent Body(Resource resource)
        => new(JsonSerializer.Serialize(resource, resource.GetType(), FhirJson), Encoding.UTF8, "application/fhir+json");

    private static Questionnaire Form(string id, string title) => new()
    {
        Id = id,
        Title = title,
        Status = PublicationStatus.Active,
        Item = [new() { LinkId = "1", Text = "Question", Type = Questionnaire.QuestionnaireItemType.Boolean }],
    };

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, HttpContent? content = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData("user/Questionnaire.write")]
    [InlineData("system/Questionnaire.write")]
    [InlineData("user/*.write")]
    [InlineData("user/*.*")]
    public async Task SmartToken_CannotRewriteATenantsQuestionnaire(string scopes)
    {
        var token = _factory.IssueToken(scopes);

        var put = await SendAsync(HttpMethod.Put, "/fhir/r4/Questionnaire/q-imaging-mri", token,
            Body(Form("q-imaging-mri", "Rewritten by a provider app")));

        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var stored = await _factory.Services.GetRequiredService<IDtrService>()
            .GetQuestionnaireAsync("q-imaging-mri", Tenant);
        stored!.Title.Should().NotBe("Rewritten by a provider app");
    }

    [Theory]
    [InlineData("user/Questionnaire.write")]
    [InlineData("system/Questionnaire.write")]
    [InlineData("user/*.*")]
    public async Task SmartToken_CannotCreateAQuestionnaire(string scopes)
    {
        var id = "q-smart-" + Guid.NewGuid().ToString("N")[..6];

        var post = await SendAsync(HttpMethod.Post, "/fhir/r4/Questionnaire", _factory.IssueToken(scopes),
            Body(Form(id, "Created by a provider app")));

        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.Services.GetRequiredService<IDtrService>().GetQuestionnaireAsync(id, Tenant))
            .Should().BeNull();
    }

    [Fact]
    public async Task ChoAdmin_CanStillAuthorQuestionnaires()
    {
        var tenant = "dtr-admin-" + Guid.NewGuid().ToString("N")[..6];
        var token = ChoDevelopmentAuth.UserToken(tenant, ChoRolePermissions.TenantAdmin);

        var put = await SendAsync(HttpMethod.Put, "/fhir/r4/Questionnaire/q-imaging-mri", token,
            Body(Form("q-imaging-mri", "Tenant admin's MRI form")));

        put.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChoAdmin_OfATenantNamedDefault_CannotWriteTheSharedLibrary()
    {
        // D-14: "default" is the store key of the read-only library every
        // tenant sees. A tenant whose id is "default" must not be able to
        // write there, or its forms would appear in every tenant's search.
        var token = ChoDevelopmentAuth.UserToken("default", ChoRolePermissions.TenantAdmin);

        var put = await SendAsync(HttpMethod.Put, "/fhir/r4/Questionnaire/q-imaging-mri", token,
            Body(Form("q-imaging-mri", "Rewritten shared form")));
        var post = await SendAsync(HttpMethod.Post, "/fhir/r4/Questionnaire", token,
            Body(Form("q-default-" + Guid.NewGuid().ToString("N")[..6], "New shared form")));

        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var library = await _factory.Services.GetRequiredService<IDtrService>()
            .GetQuestionnaireAsync("q-imaging-mri", "some-other-tenant");
        library!.Title.Should().NotBe("Rewritten shared form");
    }

    [Fact]
    public async Task SmartToken_CanStillReadQuestionnaires()
        => (await SendAsync(HttpMethod.Get, "/fhir/r4/Questionnaire/q-imaging-mri",
                _factory.IssueToken("user/Questionnaire.read")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task ProviderToken_CanStillSubmitAQuestionnaireResponse()
    {
        // DTR itself: a provider app answers the payer's questionnaire for an
        // attributed member (test-user is attributed to pat-001).
        var response = await SendAsync(HttpMethod.Post, "/fhir/r4/QuestionnaireResponse",
            _factory.IssueToken("user/QuestionnaireResponse.write"),
            Body(new QuestionnaireResponse
            {
                Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
                Questionnaire = "Questionnaire/q-imaging-mri",
                Subject = new ResourceReference("Patient/pat-001"),
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task SmartConfiguration_DoesNotAdvertiseQuestionnaireWriteScopes()
    {
        using var client = _factory.CreateClient();
        var json = await client.GetStringAsync("/fhir/r4/.well-known/smart-configuration");
        using var doc = JsonDocument.Parse(json);
        var scopes = doc.RootElement.GetProperty("scopes_supported").EnumerateArray()
            .Select(s => s.GetString()).ToList();

        scopes.Should().NotContain(s => s!.EndsWith("/Questionnaire.write", StringComparison.Ordinal));
        scopes.Should().Contain("user/QuestionnaireResponse.write");
    }
}
