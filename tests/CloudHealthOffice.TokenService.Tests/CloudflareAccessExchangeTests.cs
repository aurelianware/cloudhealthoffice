using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.TokenService.CloudflareAccess;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.TokenService.Tests;

/// <summary>A Cloudflare Zero Trust team that signs Access application tokens with a local key.</summary>
public static class TestAccess
{
    public const string TeamDomain = "cho-test.cloudflareaccess.com";
    public const string Issuer = "https://" + TeamDomain;
    public const string Aud = "test-access-application-aud";

    public static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "access-key-1" };

    /// <summary>The team's certs document: a JWKS plus Cloudflare's certificate fields.</summary>
    public static string Certs()
    {
        var p = Key.Rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new { kid = Key.KeyId, kty = "RSA", alg = "RS256", use = "sig", e = Base64UrlEncoder.Encode(p.Exponent), n = Base64UrlEncoder.Encode(p.Modulus) },
            },
            public_cert = new { kid = Key.KeyId, cert = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n" },
            public_certs = new[] { new { kid = Key.KeyId, cert = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n" } },
        });
    }

    /// <summary>An Access application token, shaped as Cloudflare issues it.</summary>
    public static string Token(
        string? email = "pat@acme.example",
        string sub = "7335d417-61da-459d-899c-0a01c76a2f94",
        string aud = Aud,
        string issuer = Issuer,
        DateTime? expires = null,
        SecurityKey? key = null,
        string? commonName = null)
    {
        var exp = expires ?? DateTime.UtcNow.AddHours(1);
        var claims = new Dictionary<string, object>
        {
            ["sub"] = sub,
            ["type"] = "app",
            ["identity_nonce"] = "6ei69kawdKzMIAPF",
            ["country"] = "US",
        };
        if (email != null)
            claims["email"] = email;
        if (commonName != null)
            claims["common_name"] = commonName;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = aud,
            IssuedAt = exp.AddHours(-24),
            NotBefore = exp.AddHours(-24),
            Expires = exp,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.RsaSha256),
        });
    }

    public static Dictionary<string, string?> Settings(params string[] tenants)
    {
        var settings = new Dictionary<string, string?>
        {
            ["CloudflareAccess:Enabled"] = "true",
            ["CloudflareAccess:TeamDomain"] = TeamDomain,
            ["CloudflareAccess:Audiences:0"] = Aud,
            ["CloudflareAccess:JwksJson"] = Certs(),
        };
        for (var i = 0; i < tenants.Length; i++)
            settings[$"CloudflareAccess:Tenants:{i}"] = tenants[i];
        return settings;
    }
}

public sealed class CloudflareAccessExchangeTests : IDisposable
{
    private readonly TokenServiceFactory _factory = new() { ExtraSettings = TestAccess.Settings("demo", "sandbox") };
    private FakeTenantService Directory => _factory.TenantService;

    public CloudflareAccessExchangeTests()
    {
        Directory.AddTenant("demo", "demo");
        Directory.AddTenant("sandbox", "sandbox");
        Directory.AddTenant("acme", Ids.AcmeDirectory);
        Directory.AddTenant("closed", "closed", active: false);
    }

    public void Dispose() => _factory.Dispose();

    private async Task<HttpResponseMessage> Exchange(string? accessToken, string? tenantId = null)
    {
        var client = _factory.CreateClient();
        if (accessToken != null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.PostAsJsonAsync(CloudflareAccessEndpoints.ExchangePath, new { tenantId });
    }

    private IEnumerable<string> Audit(string text)
        => _factory.Logs.Entries
            .Where(e => e.Category == "CloudHealthOffice.TokenService.Audit" && e.Message.Contains(text))
            .Select(e => e.Message);

    [Fact]
    public async Task An_active_user_signed_in_by_access_gets_a_cho_token_with_their_tenant_roles()
    {
        var user = Directory.AddUser("demo", "Pat@Acme.example", roles: ["ClaimsExaminer", "PlatformAdmin", "cho.service"]);

        var response = await Exchange(TestAccess.Token(email: "pat@acme.example"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await response.JsonAsync();
        body.GetProperty("tenant_id").GetString().Should().Be("demo");
        body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("ClaimsExaminer");
        body.GetProperty("user").GetProperty("id").GetString().Should().Be(user.Id);

        var jwt = new JsonWebToken(body.GetProperty("access_token").GetString());
        jwt.Issuer.Should().Be("cho-token-service");
        jwt.Subject.Should().Be(user.Id);
        jwt.GetClaim("tenant_id").Value.Should().Be("demo");
        jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value).Should().NotContain(["PlatformAdmin", "cho.service"]);

        var line = Audit("CHO token issued").Should().ContainSingle().Subject;
        line.Should().Contain("idp=cloudflare-access").And.Contain($"sub={user.Id}").And.Contain("tenant=demo");
        line.Should().NotContain("pat@acme.example", "addresses are never logged");
    }

    [Fact]
    public async Task Tenants_not_enabled_for_access_are_refused_even_with_a_matching_user()
    {
        Directory.AddUser("acme", "pat@acme.example", roles: ["TenantAdmin"]);

        var response = await Exchange(TestAccess.Token(), "acme");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Audit("reason=tenant_not_enabled").Should().ContainSingle();
    }

    [Fact]
    public async Task A_person_with_no_user_record_gets_no_token()
    {
        var response = await Exchange(TestAccess.Token(email: "stranger@example.com"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Audit("reason=no_accessible_tenant").Should().ContainSingle();
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("Invited")]
    [InlineData("Locked")]
    public async Task Only_active_users_get_a_token(string status)
    {
        Directory.AddUser("demo", "pat@acme.example", status: status, roles: ["ClaimsExaminer"]);

        (await Exchange(TestAccess.Token(), "demo")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_inactive_tenant_is_refused()
    {
        var factory = new TokenServiceFactory { ExtraSettings = TestAccess.Settings("closed") };
        factory.TenantService.AddTenant("closed", "closed", active: false);
        factory.TenantService.AddUser("closed", "pat@acme.example", roles: ["ClaimsExaminer"]);
        using var _ = factory;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAccess.Token());

        var response = await client.PostAsJsonAsync(CloudflareAccessEndpoints.ExchangePath, new { tenantId = "closed" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Several_enabled_tenants_need_a_choice_and_the_list_names_them()
    {
        Directory.AddUser("demo", "pat@acme.example", roles: ["ClaimsExaminer"]);
        Directory.AddUser("sandbox", "pat@acme.example", roles: ["TenantAdmin"]);

        var response = await Exchange(TestAccess.Token());
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.JsonAsync()).GetProperty("tenants").EnumerateArray()
            .Select(t => t.GetProperty("tenantId").GetString()).Should().BeEquivalentTo("demo", "sandbox");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAccess.Token());
        var list = await (await client.GetAsync(CloudflareAccessEndpoints.TenantsPath)).JsonAsync();
        list.EnumerateArray().Select(t => t.GetProperty("tenantId").GetString()).Should().BeEquivalentTo("demo", "sandbox");

        var chosen = await (await Exchange(TestAccess.Token(), "sandbox")).JsonAsync();
        chosen.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("TenantAdmin");
    }

    [Theory]
    [InlineData("wrong_audience")]
    [InlineData("wrong_issuer")]
    [InlineData("other_key")]
    [InlineData("expired")]
    [InlineData("no_email")]
    [InlineData("service_token")]
    [InlineData("entra_token")]
    [InlineData("none")]
    public async Task Invalid_access_tokens_are_unauthorized(string variant)
    {
        Directory.AddUser("demo", "pat@acme.example", roles: ["TenantAdmin"]);
        var token = variant switch
        {
            "wrong_audience" => TestAccess.Token(aud: "another-application"),
            "wrong_issuer" => TestAccess.Token(issuer: "https://other-team.cloudflareaccess.com"),
            "other_key" => TestAccess.Token(key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = TestAccess.Key.KeyId }),
            "expired" => TestAccess.Token(expires: DateTime.UtcNow.AddMinutes(-10)),
            "no_email" => TestAccess.Token(email: null),
            "service_token" => TestAccess.Token(email: null, sub: "", commonName: "abc.access"),
            "entra_token" => TestEntra.Token(Ids.AcmeDirectory, "aaaaaaaa-0000-0000-0000-000000000001", username: "pat@acme.example"),
            _ => null,
        };

        var response = await Exchange(token, "demo");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Audit("CHO token issued").Should().BeEmpty();
    }

    [Fact]
    public async Task An_access_token_is_not_accepted_by_the_entra_exchange()
    {
        Directory.AddUser("demo", "pat@acme.example", roles: ["TenantAdmin"]);

        var response = await _factory.ClientWith(TestAccess.Token()).ExchangeAsync("demo");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_endpoints_do_not_exist_unless_enabled()
    {
        using var factory = new TokenServiceFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAccess.Token());

        var response = await client.PostAsJsonAsync(CloudflareAccessEndpoints.ExchangePath, new { tenantId = "demo" });

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);
    }

    // ── Configuration ───────────────────────────────────────────────────

    private static CloudflareAccessExchangeOptions Valid() => new()
    {
        Enabled = true,
        TeamDomain = TestAccess.TeamDomain,
        Audiences = { TestAccess.Aud },
        Tenants = { "demo" },
    };

    [Fact]
    public void A_valid_configuration_passes() => Valid().Validate();

    [Theory]
    [InlineData("")]
    [InlineData("http://cho-test.cloudflareaccess.com")]
    [InlineData("https://cho-test.cloudflareaccess.com/path")]
    [InlineData("https://cho-test.cloudflareaccess.com:8443")]
    public void The_team_domain_must_be_an_https_host(string domain)
    {
        var options = Valid();
        options.TeamDomain = domain;
        ((Action)options.Validate).Should().Throw<InvalidOperationException>().WithMessage("*TeamDomain*");
    }

    [Fact]
    public void An_application_audience_is_required()
    {
        var options = Valid();
        options.Audiences.Clear();
        ((Action)options.Validate).Should().Throw<InvalidOperationException>().WithMessage("*Audiences*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    public void Tenants_must_be_listed_explicitly(string? tenant)
    {
        var options = Valid();
        options.Tenants.Clear();
        if (tenant != null)
            options.Tenants.Add(tenant);
        ((Action)options.Validate).Should().Throw<InvalidOperationException>().WithMessage("*Tenants*");
    }

    [Fact]
    public void The_issuer_is_the_team_domain_whatever_form_it_is_given_in()
    {
        new CloudflareAccessOptions { TeamDomain = "https://CHO-Test.cloudflareaccess.com/" }.Issuer
            .Should().Be(TestAccess.Issuer);
        new CloudflareAccessOptions { TeamDomain = TestAccess.TeamDomain }.CertsUrl
            .Should().Be(TestAccess.Issuer + "/cdn-cgi/access/certs");
    }
}
