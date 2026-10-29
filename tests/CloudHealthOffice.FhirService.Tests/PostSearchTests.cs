using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.FhirService.Tests;

/// <summary>
/// FHIR's POST search (<c>POST [type]/_search</c>, form-encoded) answers as the
/// equivalent GET search and is authorized as one. US Core 3.1.1 requires both
/// forms; before this, the POST answered 403 and Inferno's US Core kit failed
/// Patient <c>_id</c> search on it (run 40956429968).
/// </summary>
public class PostSearchTests : IClassFixture<FhirTestWebAppFactory>
{
    private static readonly JsonSerializerOptions FhirOptions =
        new JsonSerializerOptions().ForFhir(typeof(Patient).Assembly);

    private readonly FhirTestWebAppFactory _factory;

    public PostSearchTests(FhirTestWebAppFactory factory) => _factory = factory;

    private HttpClient Client(string bearer)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields)
        => new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private static async Task<Bundle> BundleOf(HttpResponseMessage response)
        => JsonSerializer.Deserialize<Bundle>(await response.Content.ReadAsStringAsync(), FhirOptions)!;

    [Fact]
    public async Task A_post_search_returns_what_the_get_search_returns()
    {
        using var client = Client(_factory.IssueToken("user/*.read"));

        var get = await client.GetAsync("/fhir/r4/Patient?_id=pat-001");
        var post = await client.PostAsync("/fhir/r4/Patient/_search", Form(("_id", "pat-001")));

        get.StatusCode.Should().Be(HttpStatusCode.OK);
        post.StatusCode.Should().Be(HttpStatusCode.OK);
        var expected = await BundleOf(get);
        var actual = await BundleOf(post);
        actual.Total.Should().Be(expected.Total).And.Be(1);
        actual.Entry.Select(e => e.Resource.Id).Should().Equal(expected.Entry.Select(e => e.Resource.Id));
    }

    [Fact]
    public async Task Parameters_in_the_url_and_in_the_body_both_apply()
    {
        using var client = Client(_factory.IssueToken("user/*.read"));

        var response = await client.PostAsync(
            "/fhir/r4/Encounter/_search?patient=pat-001", Form(("_count", "1")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bundle = await BundleOf(response);
        bundle.Entry.Should().ContainSingle("the body's _count applies");
        bundle.Total.Should().BeGreaterThan(1, "the URL's patient applies, and that patient has more than one");
        bundle.Link.Single(l => l.Relation == "self").Url.Should().Contain("patient=pat-001");
    }

    [Fact]
    public async Task A_patient_token_cannot_reach_another_member_through_the_body()
    {
        // Binding is enforced on the rewritten query, so moving the parameter
        // into the form body is not a way around it.
        using var client = Client(_factory.IssueToken("patient/*.read", "pat-001"));

        var response = await client.PostAsync("/fhir/r4/Encounter/_search", Form(("patient", "pat-002")));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_cho_caller_needs_the_same_permission_as_for_a_get()
    {
        // members:read is not in Finance's permissions.
        using var client = Client(ChoDevelopmentAuth.UserToken("test-tenant", ChoRolePermissions.Finance));

        var response = await client.PostAsync("/fhir/r4/Patient/_search", Form(("_id", "pat-001")));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_post_search_without_a_form_body_is_not_a_search()
    {
        using var client = Client(_factory.IssueToken("user/*.read"));

        var response = await client.PostAsync(
            "/fhir/r4/Patient/_search",
            new StringContent("""{"_id":"pat-001"}""", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }
}
