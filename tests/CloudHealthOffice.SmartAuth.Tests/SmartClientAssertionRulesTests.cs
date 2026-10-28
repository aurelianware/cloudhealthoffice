using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using SmartAuthService.Services;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// G-42 rules that need no host or database: what a registered JWK Set and
/// jwks_uri may be, which audiences an assertion may name, the jti / exp
/// checks, where a jwks_uri fetch may connect, and how fetched sets are cached.
/// The token-endpoint behaviour is in <see cref="SmartBackendServicesAuthTests"/>.
/// </summary>
public class SmartClientAssertionRulesTests
{
    // ── JWK Set ───────────────────────────────────────────────────────────────

    public static string PublicJwks(params AsymmetricAlgorithm[] keys)
        => JsonSerializer.Serialize(new { keys = keys.Select((k, i) => PublicJwk(k, "k" + i)).ToArray() });

    public static Dictionary<string, object> PublicJwk(AsymmetricAlgorithm key, string kid)
    {
        switch (key)
        {
            case RSA rsa:
                var p = rsa.ExportParameters(false);
                return new() { ["kty"] = "RSA", ["kid"] = kid, ["use"] = "sig", ["n"] = Base64UrlEncoder.Encode(p.Modulus!), ["e"] = Base64UrlEncoder.Encode(p.Exponent!) };
            case ECDsa ec:
                var q = ec.ExportParameters(false);
                var crv = q.Curve.Oid.FriendlyName switch { "nistP256" or "ECDSA_P256" => "P-256", "nistP384" or "ECDSA_P384" => "P-384", _ => "P-521" };
                return new() { ["kty"] = "EC", ["kid"] = kid, ["crv"] = crv, ["x"] = Base64UrlEncoder.Encode(q.Q.X!), ["y"] = Base64UrlEncoder.Encode(q.Q.Y!) };
            default:
                throw new ArgumentException("unsupported key", nameof(key));
        }
    }

    [Fact]
    public void Jwks_PublicRsaAndEcKeys_AreAccepted()
    {
        var (set, error) = SmartClientJwks.Parse(PublicJwks(RSA.Create(2048), ECDsa.Create(ECCurve.NamedCurves.nistP384)));
        error.Should().BeNull();
        set!.Keys.Select(k => k.Kid).Should().Equal("k0", "k1");
        set.GetSigningKeys().Should().HaveCount(2);
    }

    [Fact]
    public void Jwks_WithPrivateKeyMaterial_IsRefused()
    {
        var rsa = RSA.Create(2048);
        var jwk = PublicJwk(rsa, "k0");
        jwk["d"] = Base64UrlEncoder.Encode(rsa.ExportParameters(true).D!);
        var (set, error) = SmartClientJwks.Parse(JsonSerializer.Serialize(new { keys = new[] { jwk } }));
        set.Should().BeNull();
        error.Should().Contain("private");
    }

    [Theory]
    [InlineData("""{"keys":[{"kty":"oct","kid":"s","k":"c2VjcmV0"}]}""", "private")]
    [InlineData("""{"keys":[]}""", "no keys")]
    [InlineData("""{"nokeys":true}""", "keys")]
    [InlineData("""[1,2]""", "keys")]
    [InlineData("""not json""", "JSON")]
    [InlineData("""{"keys":[{"kty":"EC","kid":"e","crv":"secp256k1","x":"AA","y":"AA"}]}""", "P-256")]
    [InlineData("""{"keys":[{"kty":"OKP","kid":"o","crv":"Ed25519","x":"AA"}]}""", "RSA or EC")]
    public void Jwks_Malformed_IsRefused(string json, string expected)
    {
        var (set, error) = SmartClientJwks.Parse(json);
        set.Should().BeNull();
        error.Should().Contain(expected);
    }

    [Fact]
    public void Jwks_KeyWithoutKid_DuplicateKid_EncryptionUse_ShortRsa_AreRefused()
    {
        var rsa = RSA.Create(2048);
        Refuse(new[] { Without(PublicJwk(rsa, "k0"), "kid") }, "kid");
        Refuse(new[] { PublicJwk(rsa, "same"), PublicJwk(RSA.Create(2048), "same") }, "more than once");
        Refuse(new[] { With(PublicJwk(rsa, "k0"), "use", "enc") }, "sig");
        Refuse(new[] { PublicJwk(RSA.Create(1024), "k0") }, "2048");
        Refuse(Enumerable.Range(0, SmartClientJwks.MaxKeys + 1).Select(i => PublicJwk(rsa, "k" + i)).ToArray(), "more than");

        static void Refuse(Dictionary<string, object>[] keys, string expected)
        {
            var (set, error) = SmartClientJwks.Parse(JsonSerializer.Serialize(new { keys }));
            set.Should().BeNull();
            error.Should().Contain(expected);
        }
        static Dictionary<string, object> Without(Dictionary<string, object> jwk, string name) { jwk.Remove(name); return jwk; }
        static Dictionary<string, object> With(Dictionary<string, object> jwk, string name, string value) { jwk[name] = value; return jwk; }
    }

    [Theory]
    [InlineData("https://client.example.com/.well-known/jwks.json", true)]
    [InlineData("http://client.example.com/jwks", false)]
    [InlineData("https://user:pw@client.example.com/jwks", false)]
    [InlineData("https://client.example.com/jwks#frag", false)]
    [InlineData("/relative/jwks", false)]
    [InlineData("", false)]
    public void JwksUri_MustBeAbsoluteHttps(string raw, bool accepted)
        => SmartClientJwks.IsAcceptableJwksUri(raw, out _).Should().Be(accepted);

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)] // cloud metadata
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void JwksFetch_ConnectsOnlyToPublicAddresses(string address, bool isPublic)
        => SmartJwksFetcher.IsPublic(IPAddress.Parse(address)).Should().Be(isPublic);

    // ── Audience ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://auth.example.com/connect/token", true)]
    [InlineData("https://auth.example.com/connect/token/", true)]
    [InlineData("https://AUTH.example.com/connect/token", true)]
    [InlineData("https://auth.example.com", true)]
    [InlineData("https://auth.example.com/", true)]
    [InlineData("https://auth.example.com/connect/authorize", false)]
    [InlineData("https://evil.example.com/connect/token", false)]
    [InlineData("http://auth.example.com/connect/token", false)]
    [InlineData("https://auth.example.com:8443/connect/token", false)]
    [InlineData("https://auth.example.com/connect/token?x=1", false)]
    public void Audience_IsTheTokenEndpointOrTheIssuer(string aud, bool accepted)
    {
        var accepted_ = SmartClientAssertions.ValidateAudience.AcceptedAudiences(
            new Uri("https://auth.example.com"), [new Uri("/connect/token", UriKind.Relative)]);
        accepted_.Any(a => SmartClientAssertions.ValidateAudience.SameUri(a, new Uri(aud))).Should().Be(accepted);
    }

    [Fact]
    public void Audience_IssuerWithPath_TokenEndpointIsUnderIt()
    {
        var audiences = SmartClientAssertions.ValidateAudience.AcceptedAudiences(
            new Uri("https://host.example.com/smart"), [new Uri("/connect/token", UriKind.Relative)]).ToList();
        audiences.Select(a => a.AbsoluteUri).Should().Contain("https://host.example.com/smart/connect/token");
    }

    // ── jti / exp ─────────────────────────────────────────────────────────────

    private static ClaimsPrincipal Assertion(string? jti, DateTimeOffset? exp)
    {
        var identity = new ClaimsIdentity("test");
        if (jti != null) identity.AddClaim(new Claim(Claims.JwtId, jti));
        if (exp != null) identity.AddClaim(new Claim(Claims.ExpiresAt, exp.Value.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64));
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void Lifetime_FiveMinutesIsAccepted_TenIsNot()
    {
        var now = DateTimeOffset.UtcNow;
        SmartClientAssertions.ValidateLifetimeAndReplay.Check(Assertion("j", now.AddMinutes(5)), now).Should().BeNull();
        SmartClientAssertions.ValidateLifetimeAndReplay.Check(Assertion("j", now.AddMinutes(10)), now)!
            .Value.Reason.Should().Be("client_assertion_lifetime_too_long");
    }

    [Fact]
    public void Jti_IsRequired_AndBounded()
    {
        var now = DateTimeOffset.UtcNow;
        SmartClientAssertions.ValidateLifetimeAndReplay.Check(Assertion(null, now.AddMinutes(1)), now)!
            .Value.Reason.Should().Be("client_assertion_without_jti");
        SmartClientAssertions.ValidateLifetimeAndReplay.Check(Assertion(new string('x', 257), now.AddMinutes(1)), now)!
            .Value.Reason.Should().Be("client_assertion_without_jti");
    }

    // ── jwks_uri fetch and cache ──────────────────────────────────────────────

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class JwksServer : HttpMessageHandler, IHttpClientFactory
    {
        public string Body { get; set; } = "";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    }

    private static readonly Uri JwksUri = new("https://client.example.com/jwks.json");

    private static (SmartJwksFetcher Fetcher, JwksServer Server, FakeTime Time) Fetcher()
    {
        var server = new JwksServer();
        var time = new FakeTime();
        return (new SmartJwksFetcher(server, new MemoryCache(new MemoryCacheOptions()), NullLogger<SmartJwksFetcher>.Instance, time), server, time);
    }

    [Fact]
    public async Task Fetch_CachesTheSet_AndRefetchesOnAnUnknownKid()
    {
        var (fetcher, server, time) = Fetcher();
        server.Body = PublicJwks(RSA.Create(2048));

        (await fetcher.GetAsync(JwksUri, "k0", default))!.Keys.Should().ContainSingle();
        (await fetcher.GetAsync(JwksUri, "k0", default)).Should().NotBeNull();
        server.Requests.Should().Be(1, "a fresh set is reused");

        // The client rotates: the new kid is not cached, so the set is fetched again
        // (once the minimum refetch interval has passed).
        server.Body = PublicJwks(RSA.Create(2048), RSA.Create(2048));
        time.Now += SmartJwksFetcher.RefetchAfter + TimeSpan.FromSeconds(1);
        (await fetcher.GetAsync(JwksUri, "k1", default))!.Keys.Should().HaveCount(2);
        server.Requests.Should().Be(2);
    }

    [Fact]
    public async Task Fetch_UnknownKid_IsNotRefetchedInATightLoop()
    {
        var (fetcher, server, _) = Fetcher();
        server.Body = PublicJwks(RSA.Create(2048));
        await fetcher.GetAsync(JwksUri, "k0", default);
        await fetcher.GetAsync(JwksUri, "nope", default);
        await fetcher.GetAsync(JwksUri, "nope", default);
        server.Requests.Should().Be(1);
    }

    [Fact]
    public async Task Fetch_Failure_KeepsTheLastGoodSetForAnHour_ThenNothing()
    {
        var (fetcher, server, time) = Fetcher();
        server.Body = PublicJwks(RSA.Create(2048));
        await fetcher.GetAsync(JwksUri, null, default);

        server.Status = HttpStatusCode.ServiceUnavailable;
        time.Now += SmartJwksFetcher.FreshFor + TimeSpan.FromMinutes(1);
        (await fetcher.GetAsync(JwksUri, null, default)).Should().NotBeNull("the last good set covers a brief outage");

        time.Now += SmartJwksFetcher.StaleFor;
        (await fetcher.GetAsync(JwksUri, null, default)).Should().BeNull();
    }

    [Fact]
    public async Task Fetch_ErrorsDoNotEchoTheResponse()
    {
        var (fetcher, server, _) = Fetcher();
        server.Status = HttpStatusCode.Forbidden;
        server.Body = "internal secret page";
        var (set, error) = await fetcher.FetchAsync(JwksUri, default);
        set.Should().BeNull();
        error.Should().Be("The jwks_uri answered HTTP 403.");

        server.Status = HttpStatusCode.OK;
        server.Body = new string(' ', SmartClientJwks.MaxDocumentBytes + 10);
        (set, error) = await fetcher.FetchAsync(JwksUri, default);
        set.Should().BeNull();
        error.Should().NotContain("internal");
    }
}
