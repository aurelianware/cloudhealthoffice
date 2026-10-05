using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FhirService.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// A token minted by the real smart-auth-service is presented to the real
/// fhir-service SMART validation: its trusted-issuer registry, key ring,
/// tenant resolution (<c>SmartTenant</c>), issuer confinement and patient
/// binding. The only substitution is how fhir-service fetches smart-auth's
/// JWKS — from the in-process test server rather than over the network.
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class SmartAuthToFhirEndToEndTests
{
    private readonly SmartAuthTestFixture _fixture;
    private readonly SmartAuthDriver _smart;

    public SmartAuthToFhirEndToEndTests(SmartAuthTestFixture fixture)
    {
        _fixture = fixture;
        _smart = new SmartAuthDriver(fixture.Factory);
    }

    [Fact]
    public async Task MemberToken_IsAcceptedByFhir_InTheMappedTenant_BoundToTheMappedPatient()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (app, _) = await _smart.RegisterClientAsync(tenant, "patient-app", "openid", "launch/patient", "patient/*.read");
        var browser = await _smart.LinkedMemberAsync(tenant, "pat-001");
        var accessToken = await _smart.MemberAccessTokenAsync(browser, app);

        using var fhir = await FhirTrustingSmartAuthAsync(confineTo: null);
        var client = Bearer(fhir, accessToken);

        (await client.GetAsync("/fhir/r4/Patient/pat-001")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/fhir/r4/Patient/pat-002")).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the token is bound to the mapped patient");

        // fhir-service resolved the token's tenant: an echo passes, any other is a conflict.
        (await Send(client, "/fhir/r4/Patient/pat-001", tenant)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(client, "/fhir/r4/Patient/pat-001", "demo-tenant")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SeededDemoMemberToken_IsAcceptedByAnUnconfinedFhir_WithItsOwnTenant()
    {
        // With no issuer confinement, fhir-service has only the token to go
        // on. A token without tenant_id is refused (401); this one carries it.
        var browser = await _smart.SignInAsync("demo-member");
        var token = await _smart.MemberAccessTokenAsync(
            browser, "smart-patient-app", redirectUri: "http://localhost:4200/callback");

        using var fhir = await FhirTrustingSmartAuthAsync(confineTo: null);
        var client = Bearer(fhir, token);

        (await Send(client, "/fhir/r4/Patient/pat-001", "demo-tenant")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/fhir/r4/Patient/pat-002")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ClientCredentialsToken_IsAcceptedByFhir_InTheRegisteredTenant()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (clientId, secret) = await _smart.RegisterClientAsync(tenant, "backend", "system/*.read");
        var (status, body) = await _smart.ClientCredentialsAsync(clientId, secret!, "system/*.read");
        status.Should().Be(HttpStatusCode.OK, body.ToString());

        using var fhir = await FhirTrustingSmartAuthAsync(confineTo: null);
        var client = Bearer(fhir, body.GetProperty("access_token").GetString()!);

        // Reaches authorization (not 401), and the tenant is the registered one.
        (await Send(client, "/fhir/r4/metadata", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var echo = await Send(client, "/fhir/r4/Patient", tenant);
        var other = await Send(client, "/fhir/r4/Patient", "demo-tenant");
        echo.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        other.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.Content.ReadAsStringAsync()).Should().Contain("Tenant context conflict");
    }

    [Fact]
    public async Task DevelopmentConfinementOfTheDemoIssuer_StillHolds_AsAFallback()
    {
        // fhir-service Development confines the demo issuer to demo-tenant.
        // A demo-tenant member passes; a member of another tenant is refused.
        var demoMember = await _smart.SignInAsync("demo-member");
        var demoToken = await _smart.MemberAccessTokenAsync(
            demoMember, "smart-patient-app", redirectUri: "http://localhost:4200/callback");
        SmartAuthDriver.Claim(SmartAuthDriver.Read(demoToken), "tenant_id").Should().Be("demo-tenant");

        var tenant = SmartAuthDriver.NewTenant();
        var (app, _) = await _smart.RegisterClientAsync(tenant, "patient-app", "openid", "launch/patient", "patient/*.read");
        var otherToken = await _smart.MemberAccessTokenAsync(await _smart.LinkedMemberAsync(tenant, "pat-001"), app);

        using var fhir = await FhirTrustingSmartAuthAsync(confineTo: "demo-tenant");

        (await Bearer(fhir, demoToken).GetAsync("/fhir/r4/Patient/pat-001")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Bearer(fhir, otherToken).GetAsync("/fhir/r4/Patient/pat-001")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── fhir-service host trusting this smart-auth-service ────────────────────

    private async Task<WebApplicationFactory<Program>> FhirTrustingSmartAuthAsync(string? confineTo)
    {
        var smartHttp = _fixture.Factory.CreateClient();
        var discovery = JsonDocument.Parse(await smartHttp.GetStringAsync("/.well-known/openid-configuration")).RootElement;
        var issuer = discovery.GetProperty("issuer").GetString()!;

        var options = new SmartTrustOptions
        {
            Mode = SmartTrustMode.Demo,
            TrustedIssuers =
            [
                new TrustedIssuerOptions
                {
                    Issuer = issuer,
                    Audiences = ["fhir-api"],
                    RequireHttpsMetadata = false,
                    Tenants = confineTo is null ? [] : [confineTo],
                },
            ],
        };
        options.Validate(isDevelopmentHost: true);

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<SmartTrustOptions>();
                services.RemoveAll<TrustedIssuerRegistry>();
                services.RemoveAll<IIssuerMetadataFetcher>();
                services.AddSingleton(options);
                services.AddSingleton(new TrustedIssuerRegistry(options));
                services.AddSingleton<IIssuerMetadataFetcher>(new InProcessJwksFetcher(smartHttp));
            });
        });
    }

    private static HttpClient Bearer(WebApplicationFactory<Program> fhir, string token)
    {
        var client = fhir.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string path, string? tenantHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (tenantHeader != null) request.Headers.Add("X-Tenant-ID", tenantHeader);
        return client.SendAsync(request);
    }

    /// <summary>smart-auth-service's published JWKS, fetched from the in-process server.</summary>
    private sealed class InProcessJwksFetcher : IIssuerMetadataFetcher
    {
        private readonly HttpClient _smartAuth;

        public InProcessJwksFetcher(HttpClient smartAuth) => _smartAuth = smartAuth;

        public async Task<IssuerMetadata> FetchAsync(TrustedIssuerOptions issuer, CancellationToken ct = default)
        {
            var jwks = new JsonWebKeySet(await _smartAuth.GetStringAsync("/.well-known/jwks", ct));
            return new IssuerMetadata
            {
                Issuer = issuer.Issuer,
                JwksUri = issuer.Issuer.TrimEnd('/') + "/.well-known/jwks",
                SigningKeys = jwks.GetSigningKeys().ToList(),
            };
        }
    }
}
