using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// A backend client's provider (the provider it acts for in Provider Access
/// and bulk Group export) is a field on its registration, set by the
/// registering tenant's administrator, and minted into its client_credentials
/// tokens as <c>provider_id</c>. The token's <c>sub</c> stays the client id.
/// The fhir-service side is in <see cref="SmartAuthToFhirEndToEndTests"/>.
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class SmartClientProviderBindingTests
{
    private const string ClientsPath = "/api/admin/smart/clients";

    private readonly SmartAuthDriver _smart;

    public SmartClientProviderBindingTests(SmartAuthTestFixture fixture) => _smart = new SmartAuthDriver(fixture.Factory);

    private async Task<(string ClientId, string Secret)> RegisterAsync(string tenant, string? providerId)
    {
        var resp = await _smart.Admin(tenant).PostAsJsonAsync(ClientsPath, new { kind = "backend", scopes = new[] { "system/*.read" }, providerId });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return (body.GetProperty("clientId").GetString()!, body.GetProperty("clientSecret").GetString()!);
    }

    [Fact]
    public async Task Register_WithProvider_IsStored_AndMintedIntoTokens()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (clientId, secret) = await RegisterAsync(tenant, "prov-001");

        (await _smart.Admin(tenant).GetFromJsonAsync<JsonElement>($"{ClientsPath}/{clientId}"))
            .GetProperty("providerId").GetString().Should().Be("prov-001");

        var (status, body) = await _smart.TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scope"] = "system/*.read",
            ["provider_id"] = "someone-else",
        });
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var token = SmartAuthDriver.Read(body.GetProperty("access_token").GetString()!);
        token.Claims.Where(c => c.Type == "provider_id").Select(c => c.Value).Should().Equal("prov-001");
        SmartAuthDriver.Claim(token, "sub").Should().Be(clientId);
    }

    [Fact]
    public async Task Register_WithoutProvider_TokenHasNone()
    {
        var (clientId, secret) = await RegisterAsync(SmartAuthDriver.NewTenant(), null);
        var (_, body) = await _smart.ClientCredentialsAsync(clientId, secret, "system/*.read");
        SmartAuthDriver.Claim(SmartAuthDriver.Read(body.GetProperty("access_token").GetString()!), "provider_id").Should().BeNull();
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("prov/001")]
    [InlineData("prov_001")]
    [InlineData("P12345678901234567890123456789012345678901234567890123456789012345")] // 65
    public async Task Register_MalformedProvider_IsRefused(string providerId)
    {
        var resp = await _smart.Admin(SmartAuthDriver.NewTenant()).PostAsJsonAsync(ClientsPath,
            new { kind = "backend", scopes = new[] { "system/*.read" }, providerId });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_InteractiveAppWithProvider_IsRefused()
    {
        var resp = await _smart.Admin(SmartAuthDriver.NewTenant()).PostAsJsonAsync(ClientsPath, new
        {
            kind = "provider-app",
            redirectUris = new[] { SmartAuthDriver.RedirectUri },
            scopes = new[] { "user/*.read" },
            providerId = "prov-001",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutProvider_ChangesTheNextToken_AndIsTenantScoped()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (clientId, secret) = await RegisterAsync(tenant, "prov-001");

        (await _smart.Admin(SmartAuthDriver.NewTenant()).PutAsJsonAsync($"{ClientsPath}/{clientId}/provider", new { providerId = "prov-x" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "another tenant's client is not found");
        (await _smart.Admin(tenant).PutAsJsonAsync($"{ClientsPath}/{clientId}/provider", new { providerId = "bad id" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var put = await _smart.Admin(tenant).PutAsJsonAsync($"{ClientsPath}/{clientId}/provider", new { providerId = "prov-002" });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var (_, body) = await _smart.ClientCredentialsAsync(clientId, secret, "system/*.read");
        SmartAuthDriver.Claim(SmartAuthDriver.Read(body.GetProperty("access_token").GetString()!), "provider_id").Should().Be("prov-002");
    }
}
