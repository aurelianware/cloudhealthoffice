using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// A Development host whose issuer is plain HTTP (the interop compose network)
/// serves OpenIddict's endpoints over HTTP; an https issuer still requires
/// HTTPS. These hosts do not use the fixture's transport-security override.
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class SmartHttpIssuerTests
{
    // The collection fixture's mongod (it sets MongoDb__ConnectionString).
    public SmartHttpIssuerTests(SmartAuthTestFixture fixture) => _ = fixture;

    private static WebApplicationFactory<SmartAuthService.Program> Host(string issuer)
        => new WebApplicationFactory<SmartAuthService.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("SmartAuth:Issuer", issuer);
        });

    [Fact]
    public async Task HttpIssuer_InDevelopment_ServesTheTokenEndpointOverHttp()
    {
        await using var host = Host("http://cho-smart-auth-service:8080");
        var client = host.CreateClient();

        var discovery = await client.GetAsync("/.well-known/openid-configuration");
        discovery.StatusCode.Should().Be(HttpStatusCode.OK, await discovery.Content.ReadAsStringAsync());

        // Reaches OpenIddict's request validation (unknown client), not the HTTPS refusal.
        var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "no-such-client",
        }));
        (await token.Content.ReadAsStringAsync()).Should().NotContain("ID2083");
    }

    [Fact]
    public async Task HttpsIssuer_StillRefusesPlainHttp()
    {
        await using var host = Host("https://auth.cloudhealthoffice.com");
        var discovery = await host.CreateClient().GetAsync("/.well-known/openid-configuration");

        discovery.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await discovery.Content.ReadAsStringAsync()).Should().Contain("ID2083");
    }
}
