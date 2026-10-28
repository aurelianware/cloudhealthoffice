using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartAuthService.Services;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// G-42: SMART Backend Services client authentication. A backend client
/// registered with a JWK Set or a jwks_uri gets a client_credentials token by
/// presenting a signed JWT (private_key_jwt), shaped exactly as the SMART
/// profile has clients send it: typ JWT, RS384/ES384, iss = sub = client_id,
/// aud = the token endpoint URL, a fresh jti, exp at most five minutes out.
/// https://hl7.org/fhir/smart-app-launch/backend-services.html
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class SmartBackendServicesAuthTests
{
    private const string ClientsPath = "/api/admin/smart/clients";
    private const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    private readonly SmartAuthTestFixture _fixture;
    private readonly SmartAuthDriver _smart;
    private readonly string _tokenEndpoint;

    public SmartBackendServicesAuthTests(SmartAuthTestFixture fixture)
    {
        _fixture = fixture;
        _smart = new SmartAuthDriver(fixture.Factory);
        var issuer = fixture.Factory.Services.GetRequiredService<IConfiguration>()["SmartAuth:Issuer"]!.TrimEnd('/');
        _tokenEndpoint = issuer + "/connect/token";
    }

    private sealed record Client(string ClientId, JsonElement Registration);

    private static async Task<Client> RegisterAsync(SmartAuthDriver smart, string tenant, object body)
    {
        var resp = await smart.Admin(tenant).PostAsJsonAsync(ClientsPath, body);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
        return new Client(json.GetProperty("clientId").GetString()!, json);
    }

    private static JsonElement Jwks(params (AsymmetricAlgorithm Key, string Kid)[] keys)
        => JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            keys = keys.Select(k => SmartClientAssertionRulesTests.PublicJwk(k.Key, k.Kid)).ToArray(),
        })).RootElement.Clone();

    private static SigningCredentials Credentials(AsymmetricAlgorithm key, string kid) => key switch
    {
        RSA rsa => new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = kid }, SecurityAlgorithms.RsaSha384),
        ECDsa ec => new SigningCredentials(new ECDsaSecurityKey(ec) { KeyId = kid }, SecurityAlgorithms.EcdsaSha384),
        _ => throw new ArgumentException("unsupported key", nameof(key)),
    };

    /// <summary>A SMART Backend Services client assertion.</summary>
    private string Assertion(
        string clientId, SigningCredentials credentials,
        string? audience = null, string? jti = null, TimeSpan? lifetime = null, bool omitJti = false,
        string? subject = null, string type = "JWT")
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject ?? clientId };
        if (!omitJti) claims["jti"] = jti ?? Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Audience = audience ?? _tokenEndpoint,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + (lifetime ?? TimeSpan.FromMinutes(4)),
            SigningCredentials = credentials,
            TokenType = type,
        });
    }

    private static Dictionary<string, string> TokenForm(string assertion, string scope = "system/*.read", string? clientId = null)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = scope,
            ["client_assertion_type"] = AssertionType,
            ["client_assertion"] = assertion,
        };
        if (clientId != null) form["client_id"] = clientId;
        return form;
    }

    // ── Registration ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Register_WithJwks_IssuesNoSecret_AndSaysPrivateKeyJwt()
    {
        var client = await RegisterAsync(_smart, SmartAuthDriver.NewTenant(), new
        {
            kind = "backend", scopes = new[] { "system/*.read" }, jwks = Jwks((RSA.Create(2048), "k1")),
        });

        client.Registration.GetProperty("clientSecret").ValueKind.Should().Be(JsonValueKind.Null);
        client.Registration.GetProperty("tokenEndpointAuthMethod").GetString().Should().Be("private_key_jwt");
    }

    [Fact]
    public async Task Register_SecretClient_IsUnchanged()
    {
        var client = await RegisterAsync(_smart, SmartAuthDriver.NewTenant(), new { kind = "backend", scopes = new[] { "system/*.read" } });
        client.Registration.GetProperty("clientSecret").GetString().Should().NotBeNullOrEmpty();
        client.Registration.GetProperty("tokenEndpointAuthMethod").GetString().Should().Be("client_secret_basic");
    }

    [Fact]
    public async Task Register_JwksWithPrivateKey_IsRefused()
    {
        var rsa = RSA.Create(2048);
        var jwk = SmartClientAssertionRulesTests.PublicJwk(rsa, "k1");
        jwk["d"] = Base64UrlEncoder.Encode(rsa.ExportParameters(true).D!);
        var tenant = SmartAuthDriver.NewTenant();

        var resp = await _smart.Admin(tenant).PostAsJsonAsync(ClientsPath, new
        {
            kind = "backend", scopes = new[] { "system/*.read" }, jwks = new { keys = new[] { jwk } },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("private");
        (await _smart.Admin(tenant).GetFromJsonAsync<JsonElement>(ClientsPath)).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Register_InteractiveAppWithJwks_IsRefused()
    {
        var resp = await _smart.Admin(SmartAuthDriver.NewTenant()).PostAsJsonAsync(ClientsPath, new
        {
            kind = "provider-app",
            redirectUris = new[] { SmartAuthDriver.RedirectUri },
            scopes = new[] { "user/*.read" },
            jwks = Jwks((RSA.Create(2048), "k1")),
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_JwksAndJwksUri_IsRefused()
    {
        var resp = await _smart.Admin(SmartAuthDriver.NewTenant()).PostAsJsonAsync(ClientsPath, new
        {
            kind = "backend", scopes = new[] { "system/*.read" },
            jwks = Jwks((RSA.Create(2048), "k1")), jwksUri = "https://client.example.com/jwks.json",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Tokens ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rs384Assertion_ToTheTokenEndpoint_GetsASystemToken_ForTheClientsTenant()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var rsa = RSA.Create(2048);
        var client = await RegisterAsync(_smart, tenant, new
        {
            kind = "backend", scopes = new[] { "system/*.read" }, jwks = Jwks((rsa, "k1")),
        });

        var (status, body) = await _smart.TokenAsync(TokenForm(Assertion(client.ClientId, Credentials(rsa, "k1"))));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var token = SmartAuthDriver.Read(body.GetProperty("access_token").GetString()!);
        SmartAuthDriver.Claim(token, "tenant_id").Should().Be(tenant);
        SmartAuthDriver.Claim(token, "sub").Should().Be(client.ClientId);
        body.TryGetProperty("refresh_token", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Es384Assertion_WithClientIdParameter_GetsAToken()
    {
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var client = await RegisterAsync(_smart, SmartAuthDriver.NewTenant(), new
        {
            kind = "backend", scopes = new[] { "system/*.read" }, jwks = Jwks((ec, "ec1")),
        });

        var (status, body) = await _smart.TokenAsync(
            TokenForm(Assertion(client.ClientId, Credentials(ec, "ec1")), clientId: client.ClientId));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
    }

    [Fact]
    public async Task Assertion_AddressedToTheIssuer_IsAlsoAccepted()
    {
        var rsa = RSA.Create(2048);
        var client = await RegisterAsync(_smart, SmartAuthDriver.NewTenant(), new
        {
            kind = "backend", scopes = new[] { "system/*.read" }, jwks = Jwks((rsa, "k1")),
        });
        var issuer = _tokenEndpoint[..^"/connect/token".Length];

        var (status, body) = await _smart.TokenAsync(TokenForm(Assertion(client.ClientId, Credentials(rsa, "k1"), audience: issuer)));
        status.Should().Be(HttpStatusCode.OK, body.ToString());
    }

    [Fact]
    public async Task ReplayedAssertion_IsRefused()
    {
        var rsa = RSA.Create(2048);
        var client = await RegisterAsync(_smart, SmartAuthDriver.NewTenant(), new
        {
            kind = "backend", scopes = new[] { "system/*.read" }, jwks = Jwks((rsa, "k1")),
        });
        var assertion = Assertion(client.ClientId, Credentials(rsa, "k1"));

        (await _smart.TokenAsync(TokenForm(assertion))).Status.Should().Be(HttpStatusCode.OK);
        var (status, body) = await _smart.TokenAsync(TokenForm(assertion));

        status.Should().NotBe(HttpStatusCode.OK);
        body.GetProperty("error").GetString().Should().Be("invalid_client");
    }

    [Fact]
    public async Task SameJti_FromAnotherClient_IsNotAReplay()
    {
        var rsa = RSA.Create(2048);
        var tenant = SmartAuthDriver.NewTenant();
        var a = await RegisterAsync(_smart, tenant, new { kind = "backend", scopes = new[] { "system/*.read" }, jwks = Jwks((rsa, "k1")) });
        var b = await RegisterAsync(_smart, tenant, new { kind = "backend", scopes = new[] { "system/*.read" }, jwks = Jwks((rsa, "k1")) });
        var jti = Guid.NewGuid().ToString("N");

        (await _smart.TokenAsync(TokenForm(Assertion(a.ClientId, Credentials(rsa, "k1"), jti: jti)))).Status.Should().Be(HttpStatusCode.OK);
        (await _smart.TokenAsync(TokenForm(Assertion(b.ClientId, Credentials(rsa, "k1"), jti: jti)))).Status.Should().Be(HttpStatusCode.OK);
    }

    public static TheoryData<string> BadAssertions => new()
    {
        "wrong-key", "wrong-audience", "too-long", "no-jti", "sub-not-client", "client-id-mismatch", "hs256",
    };

    [Theory]
    [MemberData(nameof(BadAssertions))]
    public async Task BadAssertion_IsRefused_AsInvalidClient(string kind)
    {
        var rsa = RSA.Create(2048);
        var tenant = SmartAuthDriver.NewTenant();
        var client = await RegisterAsync(_smart, tenant, new
        {
            kind = "backend", scopes = new[] { "system/*.read" }, jwks = Jwks((rsa, "k1")),
        });
        var good = Credentials(rsa, "k1");

        var form = kind switch
        {
            "wrong-key" => TokenForm(Assertion(client.ClientId, Credentials(RSA.Create(2048), "k1"))),
            "wrong-audience" => TokenForm(Assertion(client.ClientId, good, audience: "https://elsewhere.example.com/connect/token")),
            "too-long" => TokenForm(Assertion(client.ClientId, good, lifetime: TimeSpan.FromMinutes(30))),
            "no-jti" => TokenForm(Assertion(client.ClientId, good, omitJti: true)),
            "sub-not-client" => TokenForm(Assertion(client.ClientId, good, subject: "someone-else")),
            "client-id-mismatch" => TokenForm(Assertion(client.ClientId, good), clientId: "another-client"),
            "hs256" => TokenForm(Assertion(client.ClientId,
                new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "k1" }, SecurityAlgorithms.HmacSha256))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var (status, body) = await _smart.TokenAsync(form);

        status.Should().NotBe(HttpStatusCode.OK, kind);
        body.TryGetProperty("access_token", out _).Should().BeFalse();
        body.GetProperty("error").GetString().Should().BeOneOf("invalid_client", "invalid_grant", "invalid_request");
    }

    [Fact]
    public async Task SecretClient_CannotUseAnAssertion_AndKeyClient_CannotUseASecret()
    {
        var rsa = RSA.Create(2048);
        var tenant = SmartAuthDriver.NewTenant();
        var secretClient = await RegisterAsync(_smart, tenant, new { kind = "backend", scopes = new[] { "system/*.read" } });
        var keyClient = await RegisterAsync(_smart, tenant, new { kind = "backend", scopes = new[] { "system/*.read" }, jwks = Jwks((rsa, "k1")) });

        (await _smart.TokenAsync(TokenForm(Assertion(secretClient.ClientId, Credentials(rsa, "k1"))))).Status
            .Should().NotBe(HttpStatusCode.OK, "a secret client has no keys to verify an assertion with");
        (await _smart.ClientCredentialsAsync(keyClient.ClientId, "guessed-secret", "system/*.read")).Status
            .Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AssertionClient_StillGetsOnlyItsRegisteredScopes()
    {
        var rsa = RSA.Create(2048);
        var client = await RegisterAsync(_smart, SmartAuthDriver.NewTenant(), new
        {
            kind = "backend", scopes = new[] { "system/Patient.read" }, jwks = Jwks((rsa, "k1")),
        });

        var (status, _) = await _smart.TokenAsync(TokenForm(Assertion(client.ClientId, Credentials(rsa, "k1")), scope: "system/*.read"));
        status.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeletedClient_AssertionIsRefused()
    {
        var rsa = RSA.Create(2048);
        var tenant = SmartAuthDriver.NewTenant();
        var client = await RegisterAsync(_smart, tenant, new { kind = "backend", scopes = new[] { "system/*.read" }, jwks = Jwks((rsa, "k1")) });
        (await _smart.Admin(tenant).DeleteAsync($"{ClientsPath}/{client.ClientId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _smart.TokenAsync(TokenForm(Assertion(client.ClientId, Credentials(rsa, "k1"))))).Status.Should().NotBe(HttpStatusCode.OK);
    }

    // ── Key rotation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PutKeys_ReplacesTheJwks_OldKeyStopsWorking()
    {
        var oldKey = RSA.Create(2048);
        var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var tenant = SmartAuthDriver.NewTenant();
        var client = await RegisterAsync(_smart, tenant, new { kind = "backend", scopes = new[] { "system/*.read" }, jwks = Jwks((oldKey, "old")) });

        var put = await _smart.Admin(tenant).PutAsJsonAsync($"{ClientsPath}/{client.ClientId}/keys", new { jwks = Jwks((newKey, "new")) });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        (await _smart.TokenAsync(TokenForm(Assertion(client.ClientId, Credentials(oldKey, "old"))))).Status.Should().NotBe(HttpStatusCode.OK);
        (await _smart.TokenAsync(TokenForm(Assertion(client.ClientId, Credentials(newKey, "new"))))).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PutKeys_OnAnotherTenantsClient_Or_ASecretClient_IsRefused()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var keyClient = await RegisterAsync(_smart, tenant, new { kind = "backend", scopes = new[] { "system/*.read" }, jwks = Jwks((RSA.Create(2048), "k1")) });
        var secretClient = await RegisterAsync(_smart, tenant, new { kind = "backend", scopes = new[] { "system/*.read" } });
        var body = new { jwks = Jwks((RSA.Create(2048), "k2")) };

        (await _smart.Admin(SmartAuthDriver.NewTenant()).PutAsJsonAsync($"{ClientsPath}/{keyClient.ClientId}/keys", body))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _smart.Admin(tenant).PutAsJsonAsync($"{ClientsPath}/{secretClient.ClientId}/keys", body))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── jwks_uri ──────────────────────────────────────────────────────────────

    /// <summary>Stands in for clients' jwks_uri hosts.</summary>
    private sealed class JwksHost : HttpMessageHandler
    {
        public Dictionary<string, string> Documents { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Documents.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private (WebApplicationFactory<SmartAuthService.Program> Host, JwksHost Jwks) WithJwksHost()
    {
        var jwks = new JwksHost();
        var host = _fixture.Factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.AddHttpClient(SmartJwksFetcher.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => jwks)));
        return (host, jwks);
    }

    [Fact]
    public async Task JwksUri_IsFetched_AndARotatedKeyIsPickedUp()
    {
        var (host, jwksHost) = WithJwksHost();
        await using var _ = host;
        var smart = new SmartAuthDriver(host);
        var uri = $"https://client-{Guid.NewGuid():N}.example.com/.well-known/jwks.json";
        var first = RSA.Create(2048);
        jwksHost.Documents[uri] = Jwks((first, "k1")).GetRawText();

        var client = await RegisterAsync(smart, SmartAuthDriver.NewTenant(), new { kind = "backend", scopes = new[] { "system/*.read" }, jwksUri = uri });
        client.Registration.GetProperty("jwksUri").GetString().Should().Be(uri);
        client.Registration.GetProperty("tokenEndpointAuthMethod").GetString().Should().Be("private_key_jwt");

        (await smart.TokenAsync(TokenForm(Assertion(client.ClientId, Credentials(first, "k1"))))).Status.Should().Be(HttpStatusCode.OK);

        // The client publishes a new key and signs with it. Its kid is not in
        // the cached set, so the set is fetched again (after the minimum
        // refetch interval) and the new key is accepted.
        var second = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        jwksHost.Documents[uri] = Jwks((first, "k1"), (second, "k2")).GetRawText();
        await Task.Delay(SmartJwksFetcher.RefetchAfter + TimeSpan.FromSeconds(1));
        var (status, body) = await smart.TokenAsync(TokenForm(Assertion(client.ClientId, Credentials(second, "k2"))));
        status.Should().Be(HttpStatusCode.OK, body.ToString());
    }

    [Fact]
    public async Task JwksUri_Unreachable_IsRefusedAtRegistration()
    {
        var (host, _) = WithJwksHost();
        await using var _host = host;
        var resp = await new SmartAuthDriver(host).Admin(SmartAuthDriver.NewTenant()).PostAsJsonAsync(ClientsPath, new
        {
            kind = "backend", scopes = new[] { "system/*.read" }, jwksUri = "https://nowhere.example.com/jwks.json",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("jwksUri");
    }

    [Fact]
    public async Task JwksUri_NotHttps_IsRefused()
    {
        var resp = await _smart.Admin(SmartAuthDriver.NewTenant()).PostAsJsonAsync(ClientsPath, new
        {
            kind = "backend", scopes = new[] { "system/*.read" }, jwksUri = "http://client.example.com/jwks.json",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
