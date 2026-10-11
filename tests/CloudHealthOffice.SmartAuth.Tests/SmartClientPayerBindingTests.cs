using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using SmartAuthService.Models;
using SmartAuthService.Services;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// D-13: a backend client's payer (the payer it acts for in Payer-to-Payer
/// exchange) is a field on its registration, set by the registering tenant's
/// administrator, and minted into its client_credentials tokens as
/// <c>payer_id</c>. <c>system/PayerToPayer.read</c> is never granted, nor
/// issued, to a backend client without one.
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class SmartClientPayerBindingTests
{
    private const string P2PScope = "system/PayerToPayer.read";
    private const string ClientsPath = "/api/admin/smart/clients";

    private readonly SmartAuthTestFixture _fixture;
    private readonly SmartAuthDriver _smart;

    public SmartClientPayerBindingTests(SmartAuthTestFixture fixture)
    {
        _fixture = fixture;
        _smart = new SmartAuthDriver(fixture.Factory);
    }

    private async Task<HttpResponseMessage> RegisterAsync(string tenant, object body)
        => await _smart.Admin(tenant).PostAsJsonAsync(ClientsPath, body);

    private async Task<(string ClientId, string Secret)> RegisterPayerClientAsync(string tenant, string payerId, params string[] scopes)
    {
        var resp = await RegisterAsync(tenant, new { kind = "backend", scopes = scopes.Length > 0 ? scopes : [P2PScope], payerId });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("payerId").GetString().Should().Be(payerId);
        return (body.GetProperty("clientId").GetString()!, body.GetProperty("clientSecret").GetString()!);
    }

    // ── Registration ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Register_BackendWithP2PScopeAndPayer_IsStored_AndReadBack()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (clientId, _) = await RegisterPayerClientAsync(tenant, "PAYER-A");

        var admin = _smart.Admin(tenant);
        (await admin.GetFromJsonAsync<JsonElement>($"{ClientsPath}/{clientId}"))
            .GetProperty("payerId").GetString().Should().Be("PAYER-A");
        (await admin.GetFromJsonAsync<JsonElement>(ClientsPath)).EnumerateArray().Single()
            .GetProperty("payerId").GetString().Should().Be("PAYER-A");
    }

    [Fact]
    public async Task Register_BackendWithP2PScope_WithoutPayer_IsRefused()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var resp = await RegisterAsync(tenant, new { kind = "backend", scopes = new[] { "system/*.read", P2PScope } });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("payerId is required");
        (await _smart.Admin(tenant).GetFromJsonAsync<JsonElement>(ClientsPath)).GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("line\nbreak")]
    [InlineData("-leading-hyphen")]
    [InlineData("payer/with/slash")]
    [InlineData("P12345678901234567890123456789012345678901234567890123456789012345")] // 65
    public async Task Register_MalformedPayer_IsRefused(string payerId)
    {
        var resp = await RegisterAsync(SmartAuthDriver.NewTenant(), new { kind = "backend", scopes = new[] { P2PScope }, payerId });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("patient-app", "patient/*.read")]
    [InlineData("provider-app", "user/*.read")]
    public async Task Register_InteractiveAppWithPayer_IsRefused(string kind, string scope)
    {
        var resp = await RegisterAsync(SmartAuthDriver.NewTenant(), new
        {
            kind,
            redirectUris = new[] { SmartAuthDriver.RedirectUri },
            scopes = new[] { scope },
            payerId = "PAYER-A",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_BackendWithoutP2PScope_PayerIsOptional()
    {
        var resp = await RegisterAsync(SmartAuthDriver.NewTenant(), new { kind = "backend", scopes = new[] { "system/*.read" } });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── Tokens ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClientCredentialsToken_CarriesTheRegisteredPayer_NotOneTheClientSends()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (clientId, secret) = await RegisterPayerClientAsync(tenant, "PAYER-A");

        var (status, body) = await _smart.TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scope"] = P2PScope,
            ["payer_id"] = "PAYER-B",
            ["receivingPayerId"] = "PAYER-B",
        }, r => r.Headers.Add("X-Payer-ID", "PAYER-B"));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var token = SmartAuthDriver.Read(body.GetProperty("access_token").GetString()!);
        token.Claims.Where(c => c.Type == "payer_id").Select(c => c.Value).Should().Equal("PAYER-A");
        SmartAuthDriver.Claim(token, "tenant_id").Should().Be(tenant);
        body.TryGetProperty("refresh_token", out _).Should().BeFalse("a backend token is re-minted from the registration, never refreshed");
    }

    [Fact]
    public async Task InteractiveTokens_NeverCarryAPayer()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var browser = await _smart.LinkedMemberAsync(tenant, "mbr-payer");
        var (appId, _) = await _smart.RegisterClientAsync(tenant, "patient-app", "openid", "launch/patient", "patient/*.read");

        var jwt = await _smart.MemberAccessTokenAsync(browser, appId);

        SmartAuthDriver.Claim(SmartAuthDriver.Read(jwt), "payer_id").Should().BeNull();
    }

    [Fact]
    public async Task LegacyClientWithP2PScopeButNoPayer_GetsNoP2PToken_UntilAnAdminSetsOne()
    {
        // A registration made before the field existed: the scope permission
        // is on the application, the registration has no payer.
        var tenant = SmartAuthDriver.NewTenant();
        var clientId = "legacy-" + Guid.NewGuid().ToString("N")[..8];
        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISmartIdentityStore>().CreateClientAsync(new ClientTenantRegistration
            {
                ClientId = clientId, TenantId = tenant, Kind = SmartClientKind.Backend,
                DisplayName = clientId, CreatedBy = "legacy", CreatedAt = DateTimeOffset.UtcNow,
            });
            await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = clientId,
                ClientSecret = "legacy-secret",
                ClientType = ClientTypes.Confidential,
                Permissions =
                {
                    Permissions.Endpoints.Token,
                    Permissions.GrantTypes.ClientCredentials,
                    Permissions.Prefixes.Scope + "system/*.read",
                    Permissions.Prefixes.Scope + P2PScope,
                },
            });
        }

        var (refused, refusal) = await _smart.ClientCredentialsAsync(clientId, "legacy-secret", P2PScope);
        refused.Should().NotBe(HttpStatusCode.OK);
        refusal.TryGetProperty("access_token", out _).Should().BeFalse();

        // Other system scopes still work, and carry no payer.
        var (ok, other) = await _smart.ClientCredentialsAsync(clientId, "legacy-secret", "system/*.read");
        ok.Should().Be(HttpStatusCode.OK, other.ToString());
        SmartAuthDriver.Claim(SmartAuthDriver.Read(other.GetProperty("access_token").GetString()!), "payer_id").Should().BeNull();

        // The admin cannot clear it to nothing, but can set one.
        var admin = _smart.Admin(tenant);
        (await admin.PutAsJsonAsync($"{ClientsPath}/{clientId}/payer", new { payerId = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var set = await admin.PutAsJsonAsync($"{ClientsPath}/{clientId}/payer", new { payerId = "PAYER-L" });
        set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());

        var (status, body) = await _smart.ClientCredentialsAsync(clientId, "legacy-secret", P2PScope);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        SmartAuthDriver.Claim(SmartAuthDriver.Read(body.GetProperty("access_token").GetString()!), "payer_id")
            .Should().Be("PAYER-L");
    }

    [Fact]
    public async Task ChangingThePayer_ChangesTheNextToken()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (clientId, secret) = await RegisterPayerClientAsync(tenant, "PAYER-OLD");

        (await _smart.Admin(tenant).PutAsJsonAsync($"{ClientsPath}/{clientId}/payer", new { payerId = "PAYER-NEW" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var (_, body) = await _smart.ClientCredentialsAsync(clientId, secret, P2PScope);
        SmartAuthDriver.Claim(SmartAuthDriver.Read(body.GetProperty("access_token").GetString()!), "payer_id")
            .Should().Be("PAYER-NEW");
    }

    // ── Tenant scoping and permissions ────────────────────────────────────────

    [Fact]
    public async Task SettingThePayer_IsTenantScoped()
    {
        var tenantA = SmartAuthDriver.NewTenant();
        var tenantB = SmartAuthDriver.NewTenant();
        var (clientA, secretA) = await RegisterPayerClientAsync(tenantA, "PAYER-A");

        (await _smart.Admin(tenantB).PutAsJsonAsync($"{ClientsPath}/{clientA}/payer", new { payerId = "PAYER-B" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await _smart.Admin(tenantA).GetFromJsonAsync<JsonElement>($"{ClientsPath}/{clientA}"))
            .GetProperty("payerId").GetString().Should().Be("PAYER-A");
        var (_, body) = await _smart.ClientCredentialsAsync(clientA, secretA, P2PScope);
        SmartAuthDriver.Claim(SmartAuthDriver.Read(body.GetProperty("access_token").GetString()!), "payer_id")
            .Should().Be("PAYER-A");
    }

    [Fact]
    public async Task SettingThePayer_NeedsSettingsManage()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (clientId, _) = await RegisterPayerClientAsync(tenant, "PAYER-A");

        (await _smart.Admin(tenant, ChoRolePermissions.ProviderRelations)
                .PutAsJsonAsync($"{ClientsPath}/{clientId}/payer", new { payerId = "PAYER-X" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SettingThePayer_OnAnInteractiveApp_IsRefused()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (appId, _) = await _smart.RegisterClientAsync(tenant, "patient-app", "openid", "patient/*.read");

        (await _smart.Admin(tenant).PutAsJsonAsync($"{ClientsPath}/{appId}/payer", new { payerId = "PAYER-A" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
