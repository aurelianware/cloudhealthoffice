using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.Infrastructure.Tests.Security;

/// <summary>Validation of Cloudflare Access application tokens (Cf-Access-Jwt-Assertion).</summary>
public sealed class CloudflareAccessTokenValidatorTests
{
    private const string Team = "cho-test.cloudflareaccess.com";
    private const string Aud = "aud-tag-1";

    private static RsaSecurityKey NewKey(string kid) => new(RSA.Create(2048)) { KeyId = kid };

    private static string Certs(params RsaSecurityKey[] keys) => JsonSerializer.Serialize(new
    {
        keys = keys.Select(k =>
        {
            var p = k.Rsa.ExportParameters(false);
            return new { kid = k.KeyId, kty = "RSA", alg = "RS256", use = "sig", e = Base64UrlEncoder.Encode(p.Exponent), n = Base64UrlEncoder.Encode(p.Modulus) };
        }),
        public_cert = new { kid = keys[0].KeyId, cert = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n" },
        public_certs = Array.Empty<object>(),
    });

    private static string Token(RsaSecurityKey key, string? email = "pat@acme.example", string sub = "user-1")
    {
        var claims = new Dictionary<string, object> { ["sub"] = sub, ["type"] = "app" };
        if (email != null)
            claims["email"] = email;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://" + Team,
            Audience = Aud,
            IssuedAt = DateTime.UtcNow.AddMinutes(-5),
            NotBefore = DateTime.UtcNow.AddMinutes(-5),
            Expires = DateTime.UtcNow.AddHours(1),
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }

    private sealed class CertsEndpoint : HttpMessageHandler
    {
        public string Body { get; set; } = "";
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static CloudflareAccessOptions Options() => new() { TeamDomain = Team, Audiences = { Aud } };

    [Fact]
    public async Task Keys_come_from_the_team_certs_url_and_a_rotated_key_is_picked_up()
    {
        var first = NewKey("k1");
        var endpoint = new CertsEndpoint { Body = Certs(first) };
        var validator = CloudflareAccessTokenValidator.Create(Options(), new HttpClient(endpoint));

        var result = await validator.ValidateAsync(Token(first), default);
        result.Identity.Should().Be(new CloudflareAccessIdentity("pat@acme.example", "user-1", "https://" + Team));
        endpoint.Requests.Should().OnlyContain(u => u.AbsoluteUri == $"https://{Team}/cdn-cgi/access/certs");

        // Cloudflare rotates: a token under a new kid makes the validator refetch once.
        var second = NewKey("k2");
        endpoint.Body = Certs(second, first);
        (await validator.ValidateAsync(Token(second), default)).Identity.Should().NotBeNull();
    }

    [Fact]
    public async Task Unreachable_keys_refuse_rather_than_accept()
    {
        var validator = new CloudflareAccessTokenValidator(Options(), new FailingKeys());

        (await validator.ValidateAsync(Token(NewKey("k1")), default)).Reason.Should().Be("access_keys_unavailable");
    }

    [Theory]
    [InlineData(null, "malformed_token")]
    [InlineData("", "malformed_token")]
    public async Task Empty_tokens_are_refused(string? token, string reason)
    {
        var validator = new CloudflareAccessTokenValidator(Options(), new StaticCloudflareAccessKeys(Certs(NewKey("k1"))));

        (await validator.ValidateAsync(token, default)).Reason.Should().Be(reason);
    }

    [Fact]
    public async Task A_token_without_an_email_names_no_person()
    {
        var key = NewKey("k1");
        var validator = new CloudflareAccessTokenValidator(Options(), new StaticCloudflareAccessKeys(Certs(key)));

        (await validator.ValidateAsync(Token(key, email: null), default)).Reason.Should().Be("no_email");
        (await validator.ValidateAsync(Token(key, email: "not-an-address"), default)).Reason.Should().Be("no_email");
    }

    [Fact]
    public void Configuration_needs_a_team_domain_and_an_application_audience()
    {
        ((Action)(() => new CloudflareAccessOptions { Audiences = { Aud } }.Validate("X")))
            .Should().Throw<InvalidOperationException>().WithMessage("*TeamDomain*");
        ((Action)(() => new CloudflareAccessOptions { TeamDomain = Team }.Validate("X")))
            .Should().Throw<InvalidOperationException>().WithMessage("*Audiences*");
        ((Action)(() => new CloudflareAccessOptions { TeamDomain = "http://" + Team, Audiences = { Aud } }.Validate("X")))
            .Should().Throw<InvalidOperationException>().WithMessage("*https*");
    }

    private sealed class FailingKeys : ICloudflareAccessKeys
    {
        public Task<IReadOnlyCollection<SecurityKey>> GetAsync(bool refresh, CancellationToken ct)
            => throw new HttpRequestException("unreachable");
    }

    // ── Google Cloud IAP ──

    private const string IapAudience = "/projects/123456789012/global/backendServices/987654321";

    private static ECDsaSecurityKey NewEcKey(string kid) => new(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = kid };

    private static string IapJwks(ECDsaSecurityKey key)
    {
        var p = key.ECDsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[] { new { kid = key.KeyId, kty = "EC", alg = "ES256", use = "sig", crv = "P-256", x = Base64UrlEncoder.Encode(p.Q.X), y = Base64UrlEncoder.Encode(p.Q.Y) } },
        });
    }

    private static string IapToken(SecurityKey key, string algorithm, string issuer = CloudflareAccessOptions.GoogleIapIssuer, string audience = IapAudience)
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(9),
            Claims = new Dictionary<string, object> { ["sub"] = "accounts.google.com:1234567890", ["email"] = "pat@acme.example" },
            SigningCredentials = new SigningCredentials(key, algorithm),
        });

    private static CloudflareAccessOptions IapOptions() => new() { Provider = EdgeAccessProvider.GoogleIap, Audiences = { IapAudience } };

    [Fact]
    public async Task Google_iap_keys_come_from_gstatic_and_an_es256_token_names_the_person()
    {
        var key = NewEcKey("iap-1");
        var endpoint = new CertsEndpoint { Body = IapJwks(key) };
        var validator = CloudflareAccessTokenValidator.Create(IapOptions(), new HttpClient(endpoint));

        var result = await validator.ValidateAsync(IapToken(key, SecurityAlgorithms.EcdsaSha256), default);

        result.Identity.Should().Be(new CloudflareAccessIdentity("pat@acme.example", "accounts.google.com:1234567890", CloudflareAccessOptions.GoogleIapIssuer));
        endpoint.Requests.Should().OnlyContain(u => u.AbsoluteUri == CloudflareAccessOptions.GoogleIapCertsUrl);
    }

    [Theory]
    [InlineData("wrong_issuer")]
    [InlineData("wrong_audience")]
    [InlineData("bad_algorithm")]
    public async Task Google_iap_tokens_must_come_from_iap_for_this_backend(string expected)
    {
        var ec = NewEcKey("iap-1");
        var rsa = NewKey("iap-1");
        var validator = new CloudflareAccessTokenValidator(IapOptions(), new StaticCloudflareAccessKeys(IapJwks(ec)));
        var token = expected switch
        {
            "wrong_issuer" => IapToken(ec, SecurityAlgorithms.EcdsaSha256, issuer: "https://" + Team),
            "wrong_audience" => IapToken(ec, SecurityAlgorithms.EcdsaSha256, audience: "/projects/123456789012/global/backendServices/1"),
            _ => IapToken(rsa, SecurityAlgorithms.RsaSha256),
        };

        var result = await validator.ValidateAsync(token, default);

        result.Identity.Should().BeNull();
        if (expected != "bad_algorithm")
            result.Reason.Should().Be(expected);
    }

    [Fact]
    public void Google_iap_configuration_needs_backend_service_audiences_and_no_team_domain()
    {
        IapOptions().Invoking(o => o.Validate("A")).Should().NotThrow();
        new CloudflareAccessOptions { Provider = EdgeAccessProvider.GoogleIap, Audiences = { Aud } }
            .Invoking(o => o.Validate("A")).Should().Throw<InvalidOperationException>().WithMessage("*backendServices*");
        new CloudflareAccessOptions { Provider = EdgeAccessProvider.GoogleIap }
            .Invoking(o => o.Validate("A")).Should().Throw<InvalidOperationException>();
        new CloudflareAccessOptions { Provider = EdgeAccessProvider.GoogleIap, TeamDomain = Team, Audiences = { IapAudience } }
            .Invoking(o => o.Validate("A")).Should().Throw<InvalidOperationException>().WithMessage("*TeamDomain*");
        IapOptions().Header.Should().Be("x-goog-iap-jwt-assertion");
        Options().Header.Should().Be("Cf-Access-Jwt-Assertion");
    }
}
