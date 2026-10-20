using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Testing.FhirService;
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
    public async Task PayerToPayerToken_ItsRegisteredPayer_ReachesTheFhirController()
    {
        // D-13 / D-32: smart-auth mints payer_id from the registration; the
        // fhir-service SMART scheme (inbound claim mapping off) delivers it to
        // the P2P controller unrenamed, from the marked issuer only.
        // demo-tenant is the tenant fhir-service's development P2P data serves.
        var (clientId, secret) = await RegisterInDemoTenantAsync("PAYER-E2E");
        var (status, body) = await _smart.ClientCredentialsAsync(clientId, secret, "system/PayerToPayer.read");
        status.Should().Be(HttpStatusCode.OK, body.ToString());

        using var fhir = await FhirTrustingSmartAuthAsync(confineTo: null);
        var client = Bearer(fhir, body.GetProperty("access_token").GetString()!);

        // The member's consent naming the registered payer (ADR 022).
        var consent = FhirService.Services.PayerToPayer.PdexConsentBuilder.Build(
            "Patient/pat-001", "PAYER-E2E", "cloud-health-office", DateTime.UtcNow.AddDays(-1), null);
        var same = await PostJson(client, "/fhir/r4/Patient/$member-match",
            new { receivingPayerId = "PAYER-E2E", memberId = "pat-001", consent });
        var omitted = await PostJson(client, "/fhir/r4/Patient/$member-match", new { memberId = "pat-001", consent });
        var other = await PostJson(client, "/fhir/r4/Patient/$member-match",
            new { receivingPayerId = "PAYER-OTHER", memberId = "pat-001" });

        same.StatusCode.Should().Be(HttpStatusCode.OK, await same.Content.ReadAsStringAsync());
        omitted.StatusCode.Should().Be(HttpStatusCode.OK, await omitted.Content.ReadAsStringAsync());
        other.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.Content.ReadAsStringAsync()).Should().Contain("registered with Cloud Health Office");
    }

    [Fact]
    public async Task PayerToPayerToken_FromAnIssuerNotMarkedAsTheRegistrationAuthority_IsRefused()
    {
        // Same smart-auth token, but fhir-service is configured as if this
        // issuer were an external IdP (another entry is the authority): its
        // payer_id is ignored and the client counts as unmapped.
        var (clientId, secret) = await RegisterInDemoTenantAsync("PAYER-E2E-2");
        var (_, body) = await _smart.ClientCredentialsAsync(clientId, secret, "system/PayerToPayer.read");

        using var fhir = await FhirTrustingSmartAuthAsync(confineTo: null, markSmartAuth: false);
        var response = await PostJson(Bearer(fhir, body.GetProperty("access_token").GetString()!),
            "/fhir/r4/Patient/$member-match", new { receivingPayerId = "PAYER-E2E-2", memberId = "pat-001" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("registered with Cloud Health Office");
    }

    // ── D-32: every token type smart-auth issues, through fhir-service ────────
    //
    // smart-auth-service grants scopes in ONE claim, `scope`, and never `scp`,
    // so fhir-service's scope/scp consistency rule can never refuse its own
    // tokens. Each grant type is presented to fhir-service as well.

    private const string ValidNpi = "1234567893";

    /// <summary>The token's scopes, from `scope` only; asserts there is no `scp`.</summary>
    private static string[] ScopesOf(string accessToken)
    {
        var jwt = SmartAuthDriver.Read(accessToken);
        jwt.Claims.Where(c => c.Type == "scp").Should().BeEmpty("smart-auth-service grants scopes in `scope` only");
        var scope = jwt.Claims.Where(c => c.Type == "scope").ToList();
        scope.Should().NotBeEmpty();
        return scope.SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
    }

    [Fact]
    public async Task MemberToken_CarriesScopeOnly()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (app, _) = await _smart.RegisterClientAsync(tenant, "patient-app", "openid", "launch/patient", "patient/*.read");
        var token = await _smart.MemberAccessTokenAsync(await _smart.LinkedMemberAsync(tenant, "pat-001"), app);

        ScopesOf(token).Should().Contain("patient/*.read");
    }

    [Fact]
    public async Task RefreshedMemberToken_CarriesScopeOnly_AndIsAcceptedByFhir_BoundToItsPatient()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (app, _) = await _smart.RegisterClientAsync(tenant, "patient-app", "openid", "offline_access", "launch/patient", "patient/*.read");
        var browser = await _smart.LinkedMemberAsync(tenant, "pat-001");
        var outcome = await SmartAuthDriver.AuthorizeAsync(browser, app, "openid offline_access launch/patient patient/*.read");
        var (status, body) = await _smart.ExchangeCodeAsync(app, outcome);
        status.Should().Be(HttpStatusCode.OK, body.ToString());

        var (refreshStatus, refreshed) = await _smart.RefreshAsync(app, body.GetProperty("refresh_token").GetString()!);
        refreshStatus.Should().Be(HttpStatusCode.OK, refreshed.ToString());
        var token = refreshed.GetProperty("access_token").GetString()!;
        ScopesOf(token).Should().Contain("patient/*.read");

        using var fhir = await FhirTrustingSmartAuthAsync(confineTo: null);
        var client = Bearer(fhir, token);
        (await client.GetAsync("/fhir/r4/Patient/pat-001")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/fhir/r4/Patient/pat-002")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProviderToken_CarriesScopeOnly_AndPassesFhirScopeEnforcement()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (app, _) = await _smart.RegisterClientAsync(tenant, "provider-app", "openid", "fhirUser", "user/*.read");
        var provider = await _smart.LinkedProviderAsync(tenant, "prov-e2e", ValidNpi);
        var outcome = await SmartAuthDriver.AuthorizeAsync(provider, app, "openid fhirUser user/*.read");
        var (status, body) = await _smart.ExchangeCodeAsync(app, outcome);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var token = body.GetProperty("access_token").GetString()!;
        ScopesOf(token).Should().Contain("user/*.read");

        using var fhir = await FhirTrustingSmartAuthAsync(confineTo: null);
        var response = await Bearer(fhir, token).GetAsync("/fhir/r4/Coverage?patient=pat-001");

        // Authenticated, and the user/*.read grant satisfies the scope check;
        // what remains is Provider Access attribution, which this provider lacks.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should()
            .Contain("Provider Access is not authorized").And.NotContain("Insufficient scope");
    }

    [Fact]
    public async Task EhrLaunchToken_CarriesScopeOnly_AndPassesFhirScopeEnforcement()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (app, _) = await _smart.RegisterClientAsync(tenant, "provider-app", "openid", "fhirUser", "launch", "launch/patient", "user/*.read");
        var provider = await _smart.LinkedProviderAsync(tenant, "prov-ehr-e2e", ValidNpi);
        var launchResp = await _smart.Admin(tenant, CloudHealthOffice.Infrastructure.Security.ChoRolePermissions.MemberServices)
            .PostAsJsonAsync("/launch", new { patientId = "pat-001", clientId = app, practitionerId = "prov-ehr-e2e" });
        launchResp.StatusCode.Should().Be(HttpStatusCode.OK, await launchResp.Content.ReadAsStringAsync());
        var launch = JsonDocument.Parse(await launchResp.Content.ReadAsStringAsync()).RootElement.GetProperty("launch").GetString()!;

        var outcome = await SmartAuthDriver.AuthorizeAsync(provider, app, "openid fhirUser launch user/*.read", launch: launch);
        outcome.Error.Should().BeNull();
        var (status, body) = await _smart.ExchangeCodeAsync(app, outcome);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var token = body.GetProperty("access_token").GetString()!;
        ScopesOf(token).Should().Contain("user/*.read");
        SmartAuthDriver.Claim(SmartAuthDriver.Read(token), "patient").Should().Be("pat-001");

        using var fhir = await FhirTrustingSmartAuthAsync(confineTo: null);
        var response = await Bearer(fhir, token).GetAsync("/fhir/r4/Coverage?patient=pat-001");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should()
            .Contain("Provider Access is not authorized").And.NotContain("Insufficient scope");
    }

    [Fact]
    public async Task ClientCredentialsTokens_CarryScopeOnly()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (clientId, secret) = await _smart.RegisterClientAsync(tenant, "backend", "system/*.read");
        var (_, body) = await _smart.ClientCredentialsAsync(clientId, secret!, "system/*.read");
        ScopesOf(body.GetProperty("access_token").GetString()!).Should().BeEquivalentTo(["system/*.read"]);

        var (p2pClient, p2pSecret) = await RegisterInDemoTenantAsync("PAYER-E2E-SCOPE");
        var (_, p2p) = await _smart.ClientCredentialsAsync(p2pClient, p2pSecret, "system/PayerToPayer.read");
        var p2pToken = p2p.GetProperty("access_token").GetString()!;
        ScopesOf(p2pToken).Should().BeEquivalentTo(["system/PayerToPayer.read"]);
        SmartAuthDriver.Claim(SmartAuthDriver.Read(p2pToken), "payer_id").Should().Be("PAYER-E2E-SCOPE");
    }

    private async Task<(string ClientId, string Secret)> RegisterInDemoTenantAsync(string payerId)
    {
        var resp = await _smart.Admin("demo-tenant").PostAsJsonAsync("/api/admin/smart/clients", new
        {
            kind = "backend",
            scopes = new[] { "system/PayerToPayer.read" },
            payerId,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        var created = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return (created.GetProperty("clientId").GetString()!, created.GetProperty("clientSecret").GetString()!);
    }

    private static Task<HttpResponseMessage> PostJson(HttpClient client, string path, object body)
        => client.PostAsJsonAsync(path, body);

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

    private async Task<WebApplicationFactory<Program>> FhirTrustingSmartAuthAsync(string? confineTo, bool markSmartAuth = true)
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
                    // This IS smart-auth-service: the registration authority
                    // whose payer_id fhir-service believes (D-13).
                    IssuesChoClientRegistrations = markSmartAuth,
                },
                .. (markSmartAuth ? Array.Empty<TrustedIssuerOptions>() :
                [
                    // Some other issuer is the registration authority here.
                    new TrustedIssuerOptions
                    {
                        Issuer = "https://registration-authority.test.local",
                        Audiences = ["fhir-api"],
                        RequireHttpsMetadata = false,
                        IssuesChoClientRegistrations = true,
                    },
                ]),
            ],
        };
        options.Validate(isDevelopmentHost: true);

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            // fhir-service requires MongoDB; these tests never touch it.
            b.UseUnreachableMongoWithInMemoryStores();
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
