using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using SmartAuthService.Models;
using SmartAuthService.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// Integration tests for SMART on FHIR launch sequences against the
/// smart-auth-service in-process via WebApplicationFactory.
///
/// Standalone launch sequence:
///   1. GET /connect/authorize → redirects to login (302)
///   2. POST /account/login → sets cookie
///   3. GET /connect/authorize (with cookie) → issues code
///   4. POST /connect/token → access + refresh tokens
///
/// EHR launch sequence:
///   1. POST /launch → launch token
///   2. GET /connect/authorize?launch=... → resolves context → issues code
///   3. POST /connect/token → token with patient + encounter claims
/// </summary>
public class SmartLaunchFlowTests : IClassFixture<SmartAuthTestFixture>
{
    private readonly SmartAuthTestFixture _fixture;

    public SmartLaunchFlowTests(SmartAuthTestFixture fixture)
    {
        _fixture = fixture;
    }

    private HttpClient CreateClient() => _fixture.Factory.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    // ── SMART configuration ───────────────────────────────────────────────────

    [Fact]
    public async Task WellKnownOpenIdConfiguration_Returns200()
    {
        var client = CreateClient();
        var resp = await client.GetAsync("/.well-known/openid-configuration");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("authorization_endpoint").GetString()
            .Should().EndWith("/connect/authorize");
        doc.RootElement.GetProperty("token_endpoint").GetString()
            .Should().EndWith("/connect/token");
    }

    [Fact]
    public async Task WellKnownJwks_Returns200WithKeys()
    {
        var client = CreateClient();
        var resp = await client.GetAsync("/.well-known/jwks");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("keys").GetArrayLength().Should().BeGreaterThan(0);
    }

    // ── Standalone launch: authorization endpoint ─────────────────────────────

    [Fact]
    public async Task AuthorizeEndpoint_NoSession_RedirectsToLogin()
    {
        var client = CreateClient();
        var resp = await client.GetAsync(
            "/connect/authorize?response_type=code&client_id=smart-patient-app" +
            "&scope=openid+patient%2F*.read" +
            "&redirect_uri=http%3A%2F%2Flocalhost%3A4200%2Fcallback" +
            "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM" +
            "&code_challenge_method=S256" +
            "&state=abc123");

        // Expect redirect to /account/login
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.PathAndQuery.Should().Contain("/account/login");
    }

    // ── Login endpoint ────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_DevMode_ValidCredentials_SetsCookieAndRedirects()
    {
        var client = CreateClient();
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", "testpatient"),
            new KeyValuePair<string, string>("password", "Password123!")
        });

        var resp = await client.PostAsync("/account/login?returnUrl=%2F", form);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Login_WrongPassword_RedirectsWithError()
    {
        var client = CreateClient();
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", "testpatient"),
            new KeyValuePair<string, string>("password", "WrongPassword")
        });

        var resp = await client.PostAsync("/account/login?returnUrl=%2F", form);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("error=invalid");
    }

    // ── EHR launch: context registration ─────────────────────────────────────

    [Fact]
    public async Task Launch_Register_Returns200WithLaunchToken()
    {
        var client = CreateClient();
        var body = JsonSerializer.Serialize(new RegisterLaunchRequest
        {
            PatientId = "pat-001",
            EncounterId = "enc-003",
            ClientId = "cho-ehr-app"
        });

        var resp = await client.PostAsync("/launch",
            new StringContent(body, Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseBody = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(responseBody);
        var launch = doc.RootElement.GetProperty("launch").GetString();
        launch.Should().NotBeNullOrEmpty();

        var iss = doc.RootElement.GetProperty("iss").GetString();
        iss.Should().Contain("fhir");
    }

    [Fact]
    public async Task Launch_NoPatientOrEncounter_Returns400()
    {
        var client = CreateClient();
        var body = JsonSerializer.Serialize(new RegisterLaunchRequest
        {
            ClientId = "cho-ehr-app"
        });

        var resp = await client.PostAsync("/launch",
            new StringContent(body, Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── LaunchContextStore unit tests ─────────────────────────────────────────

    [Fact]
    public async Task LaunchContextStore_RegisterAndConsume_Works()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ILaunchContextStore>();

        var token = await store.RegisterAsync(new RegisterLaunchRequest
        {
            PatientId = "pat-002",
            EncounterId = "enc-001",
            ClientId = "test-app"
        });

        token.Should().NotBeNullOrEmpty();

        var context = await store.ConsumeAsync(token);
        context.Should().NotBeNull();
        context!.PatientId.Should().Be("pat-002");
        context.EncounterId.Should().Be("enc-001");
    }

    [Fact]
    public async Task LaunchContextStore_ConsumeIsSingleUse()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ILaunchContextStore>();

        var token = await store.RegisterAsync(new RegisterLaunchRequest
        {
            PatientId = "pat-003",
            ClientId = "test-app"
        });

        var first = await store.ConsumeAsync(token);
        var second = await store.ConsumeAsync(token);  // already consumed

        first.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    public async Task LaunchContextStore_PeekDoesNotConsume()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ILaunchContextStore>();

        var token = await store.RegisterAsync(new RegisterLaunchRequest
        {
            PatientId = "pat-001",
            ClientId = "test-app"
        });

        var peeked = await store.PeekAsync(token);
        peeked.Should().NotBeNull();

        var consumed = await store.ConsumeAsync(token);
        consumed.Should().NotBeNull();  // still available after peek
    }

    [Fact]
    public async Task LaunchContextStore_UnknownToken_ReturnsNull()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ILaunchContextStore>();

        var result = await store.ConsumeAsync("nonexistent-token-xyz");
        result.Should().BeNull();
    }

    // ── Introspection endpoint ────────────────────────────────────────────────

    [Fact]
    public async Task IntrospectionEndpoint_IsReachable()
    {
        var client = CreateClient();
        // Must be called with a resource server credential — we just verify it's present
        var resp = await client.PostAsync("/connect/introspect",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("token", "some-token")
            }));

        // 400 (invalid_client) confirms the endpoint exists and is enforced
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized);
    }

    // ── Log-forging regression tests (CodeQL cs/log-forging / alert #1805) ───

    /// <summary>
    /// Submitting a launch token that contains embedded control characters (a classic
    /// log-injection payload) must not cause an unhandled server error.
    /// The server should reject the unknown/invalid token — returning either
    /// a 302 redirect to the redirect_uri with error=access_denied (OpenIddict
    /// standard OAuth2 error handling) or 403 Forbidden. Either confirms the
    /// sanitized log path is exercised without throwing.
    /// </summary>
    [Theory]
    [InlineData("fake-token\nINJECTED: synthetic log line")]
    [InlineData("fake-token\r\nINJECTED: synthetic log line")]
    [InlineData("fake-token\x00null-byte")]
    [InlineData("fake-token\ttab-char")]
    public async Task Authorize_LaunchTokenWithControlChars_RejectsTokenAndDoesNotThrow(string maliciousToken)
    {
        // Arrange: log in first so we have a session cookie
        var client = CreateClient();
        var loginForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", "testpatient"),
            new KeyValuePair<string, string>("password", "Password123!")
        });
        var loginResp = await client.PostAsync("/account/login?returnUrl=%2F", loginForm);
        loginResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Act: submit an authorization request with a malicious launch token
        var encodedToken = Uri.EscapeDataString(maliciousToken);
        var resp = await client.GetAsync(
            "/connect/authorize?response_type=code&client_id=smart-patient-app" +
            "&scope=openid+launch%2Fpatient+patient%2F*.read" +
            "&redirect_uri=http%3A%2F%2Flocalhost%3A4200%2Fcallback" +
            "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM" +
            "&code_challenge_method=S256" +
            "&state=sec-test" +
            $"&launch={encodedToken}");

        // Assert: the server handled the request without throwing (no 500).
        // OpenIddict translates Forbid() into a redirect to redirect_uri with
        // error=access_denied (302) or, in some configurations, a direct 403.
        // Both outcomes confirm the token was rejected and the sanitized log
        // path was exercised correctly.
        ((int)resp.StatusCode).Should().NotBe(500,
            "a server error would indicate the malicious token was not handled safely");
        resp.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Redirect, HttpStatusCode.Forbidden },
            "the invalid launch token must be rejected by the authorization endpoint");
    }
}
