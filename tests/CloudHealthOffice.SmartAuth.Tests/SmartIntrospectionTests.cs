using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SmartAuthService.Services;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// Token introspection (RFC 7662; SMART App Launch 2.2): a resource server
/// registered with the introspection permission sees its own tenant's tokens,
/// with their launch context, and nothing of another tenant's.
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class SmartIntrospectionTests
{
    private const string MemberScope = "openid launch/patient patient/*.read";

    private readonly SmartAuthDriver _smart;

    public SmartIntrospectionTests(SmartAuthTestFixture fixture) => _smart = new SmartAuthDriver(fixture.Factory);

    [Fact]
    public async Task ResourceServer_SeesItsOwnTenantsTokens_AndNothingOfAnotherTenants()
    {
        // One resource server for the whole collection: its client id is the
        // tokens' audience, and a client id is registered once.
        var tenant = SmartAuthDriver.NewTenant();
        var secret = await RegisterIntrospectorAsync(tenant, SmartIntrospection.ResourceServerClientId);

        var (app, _) = await _smart.RegisterClientAsync(tenant, "patient-app", "openid", "launch/patient", "patient/*.read");
        var browser = await _smart.LinkedMemberAsync(tenant, "mbr-4401", username: "ina-" + tenant);
        var token = await _smart.MemberAccessTokenAsync(browser, app, MemberScope);

        var (status, active) = await IntrospectAsync(SmartIntrospection.ResourceServerClientId, secret, token);
        status.Should().Be(HttpStatusCode.OK, active.ToString());
        active.GetProperty("active").GetBoolean().Should().BeTrue(active.ToString());
        active.GetProperty("client_id").GetString().Should().Be(app);
        active.GetProperty("patient").GetString().Should().Be("mbr-4401");
        active.GetProperty("fhirUser").GetString().Should().Be("Patient/mbr-4401");
        active.GetProperty("scope").GetString()!.Split(' ').Should().BeEquivalentTo(MemberScope.Split(' '));
        active.TryGetProperty("exp", out _).Should().BeTrue();

        var (invalidStatus, invalid) = await IntrospectAsync(SmartIntrospection.ResourceServerClientId, secret, "invalid_token_value");
        invalidStatus.Should().Be(HttpStatusCode.OK);
        invalid.GetProperty("active").GetBoolean().Should().BeFalse();

        // Another tenant's member token: inactive, as if unknown.
        var other = SmartAuthDriver.NewTenant();
        var (otherApp, _) = await _smart.RegisterClientAsync(other, "patient-app", "openid", "launch/patient", "patient/*.read");
        var otherBrowser = await _smart.LinkedMemberAsync(other, "mbr-4402", username: "ina-" + other);
        var otherToken = await _smart.MemberAccessTokenAsync(otherBrowser, otherApp, MemberScope);

        var (crossStatus, cross) = await IntrospectAsync(SmartIntrospection.ResourceServerClientId, secret, otherToken);
        crossStatus.Should().Be(HttpStatusCode.OK, cross.ToString());
        cross.GetProperty("active").GetBoolean().Should().BeFalse("a resource server sees only its own tenant's tokens");
        cross.ToString().Should().NotContain("mbr-4402");
    }

    [Fact]
    public async Task ResourceServer_WithoutCredentials_IsRefused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/introspect")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "anything" }),
        };
        using var response = await _smart.Anonymous().SendAsync(request);
        ((int)response.StatusCode).Should().BeOneOf(400, 401);
    }

    [Fact]
    public async Task IntrospectionPermission_IsRefusedToAPublicClient()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var response = await _smart.Admin(tenant).PostAsJsonAsync("/api/admin/smart/clients", new
        {
            kind = "patient-app",
            redirectUris = new[] { SmartAuthDriver.RedirectUri },
            scopes = new[] { "openid", "launch/patient", "patient/*.read" },
            introspection = true,
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ClientWithoutIntrospectionPermission_CannotIntrospect()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (backend, secret) = await _smart.RegisterClientAsync(tenant, "backend", "system/Patient.read");

        var (status, _) = await IntrospectAsync(backend, secret!, "anything");
        ((int)status).Should().BeOneOf(400, 401);
    }

    [Theory]
    [InlineData("tenant-a", "tenant-a", true)]
    [InlineData("tenant-a", "tenant-b", false)]
    [InlineData("tenant-a", null, false)]
    [InlineData(null, "tenant-a", false)]
    [InlineData("", "", false)]
    public void SameTenant_RequiresTwoEqualTenants(string? caller, string? token, bool expected)
        => SmartIntrospection.SameTenant(caller, token).Should().Be(expected);

    private async Task<string> RegisterIntrospectorAsync(string tenant, string clientId)
    {
        var response = await _smart.Admin(tenant).PostAsJsonAsync("/api/admin/smart/clients", new
        {
            clientId,
            kind = "backend",
            scopes = Array.Empty<string>(),
            introspection = true,
        });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("clientSecret").GetString()!;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> IntrospectAsync(string clientId, string secret, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/introspect")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(secret)}")));
        using var response = await _smart.Anonymous().SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text)
            ? default
            : JsonDocument.Parse(text).RootElement.Clone());
    }
}
