using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CloudHealthOffice.FhirService.Tests.Identity;
using FhirService.Services.PayerToPayer.Outbound;
using FhirService.Services.SmartBackend;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// CHO as a SMART Backend Services client (G-42 outbound): the client
/// assertion it signs, the token request it sends, how it caches and fails,
/// the keys it loads and publishes, and the Payer-to-Payer credential
/// provider on top of it.
/// </summary>
public class SmartBackendTokenClientTests
{
    private const string TokenEndpoint = "https://prior-payer.test/auth/token";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T12:00:00Z");

    private sealed class TokenEndpointHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, Dictionary<string, string> Form)> Calls { get; } = new();
        public Func<int, HttpResponseMessage> Responder { get; set; } = n => Issue($"token-{n}", 300);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var form = body.Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
            Calls.Add((request, form));
            return Responder(Calls.Count);
        }
    }

    private static HttpResponseMessage Issue(string token, int expiresIn) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $"{{\"access_token\":\"{token}\",\"token_type\":\"bearer\",\"expires_in\":{expiresIn},\"scope\":\"system/*.read\"}}",
            Encoding.UTF8, "application/json"),
    };

    private static string RsaPem(int bits = 2048)
    {
        using var rsa = RSA.Create(bits);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    private static string EcPem(ECCurve curve)
    {
        using var ec = ECDsa.Create(curve);
        return ec.ExportECPrivateKeyPem();
    }

    private static SmartBackendClientOptions Options(params SmartBackendSigningKeyOptions[] keys) => new()
    {
        SigningKeys = keys.Length > 0 ? keys.ToList() : [new SmartBackendSigningKeyOptions { PrivateKeyPem = RsaPem() }],
        Clients =
        {
            ["prior-plan"] = new SmartBackendRegistrationOptions
            {
                ClientId = "cho-at-prior-plan", TokenEndpoint = TokenEndpoint, Scope = "system/*.read",
            },
        },
    };

    private static (SmartBackendTokenClient Client, TokenEndpointHandler Handler, FakeTimeProvider Time, SmartBackendKeyRing Keys) Build(
        SmartBackendClientOptions? options = null)
    {
        options ??= Options();
        var handler = new TokenEndpointHandler();
        var factory = new Moq.Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(SmartBackendTokenClient.HttpClientName)).Returns(() => new HttpClient(handler));
        var time = new FakeTimeProvider(Now);
        var keys = new SmartBackendKeyRing(Microsoft.Extensions.Options.Options.Create(options));
        var client = new SmartBackendTokenClient(
            factory.Object, keys, Microsoft.Extensions.Options.Options.Create(options), time,
            NullLogger<SmartBackendTokenClient>.Instance);
        return (client, handler, time, keys);
    }

    [Fact]
    public async Task TokenRequest_IsClientCredentialsWithAJwtBearerAssertion()
    {
        var (client, handler, _, _) = Build();

        var result = await client.GetTokenAsync("prior-plan");

        result.Succeeded.Should().BeTrue();
        result.AccessToken.Should().Be("token-1");
        result.ExpiresAt.Should().Be(Now.AddSeconds(300));

        var (request, form) = handler.Calls.Should().ContainSingle().Subject;
        request.RequestUri.Should().Be(new Uri(TokenEndpoint));
        request.Method.Should().Be(HttpMethod.Post);
        form["grant_type"].Should().Be("client_credentials");
        form["scope"].Should().Be("system/*.read");
        form["client_assertion_type"].Should().Be("urn:ietf:params:oauth:client-assertion-type:jwt-bearer");
        form.Should().NotContainKey("client_secret");
        form.Should().NotContainKey("client_id", "the assertion identifies the client");
    }

    [Fact]
    public async Task Assertion_FollowsSmartBackendServices_AndVerifiesAgainstThePublishedJwks()
    {
        var (client, handler, _, keys) = Build();
        await client.GetTokenAsync("prior-plan");
        var assertion = handler.Calls.Single().Form["client_assertion"];

        // Verify exactly as a peer would: only with what the JWKS endpoint publishes.
        var published = JsonWebKeySet.Create(keys.PublicJwks()!.ToJsonString());
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(assertion, new TokenValidationParameters
        {
            ValidIssuer = "cho-at-prior-plan",
            ValidAudience = TokenEndpoint,
            IssuerSigningKeys = published.GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha384],
            ValidTypes = ["JWT"],
            LifetimeValidator = (_, _, _, _) => true, // the fake clock is not the handler's clock
        });
        validation.IsValid.Should().BeTrue(validation.Exception?.Message);

        var jwt = new JsonWebToken(assertion);
        jwt.Alg.Should().Be("RS384");
        jwt.Typ.Should().Be("JWT");
        jwt.Kid.Should().Be(keys.Active!.KeyId);
        jwt.Subject.Should().Be("cho-at-prior-plan");
        jwt.Id.Should().NotBeNullOrEmpty();
        jwt.ValidTo.Should().Be(Now.UtcDateTime.AddSeconds(240));
        (jwt.ValidTo - jwt.IssuedAt).Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task EveryAssertion_HasAFreshJti()
    {
        var (client, handler, time, _) = Build();
        await client.GetTokenAsync("prior-plan");
        time.Advance(TimeSpan.FromMinutes(10));
        await client.GetTokenAsync("prior-plan");

        handler.Calls.Select(c => new JsonWebToken(c.Form["client_assertion"]).Id).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task Token_IsCachedUntilShortlyBeforeExpiry()
    {
        var (client, handler, time, _) = Build();

        (await client.GetTokenAsync("prior-plan")).AccessToken.Should().Be("token-1");
        time.Advance(TimeSpan.FromSeconds(239)); // 300 s token, 60 s skew
        (await client.GetTokenAsync("prior-plan")).AccessToken.Should().Be("token-1");
        time.Advance(TimeSpan.FromSeconds(1));
        (await client.GetTokenAsync("prior-plan")).AccessToken.Should().Be("token-2");
        handler.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task ShortLivedToken_IsRefreshedAtHalfLife()
    {
        var (client, handler, time, _) = Build();
        handler.Responder = n => Issue($"token-{n}", 60);

        await client.GetTokenAsync("prior-plan");
        time.Advance(TimeSpan.FromSeconds(29));
        (await client.GetTokenAsync("prior-plan")).AccessToken.Should().Be("token-1");
        time.Advance(TimeSpan.FromSeconds(1));
        (await client.GetTokenAsync("prior-plan")).AccessToken.Should().Be("token-2");
    }

    [Fact]
    public async Task Invalidate_ForcesANewToken()
    {
        var (client, handler, _, _) = Build();
        await client.GetTokenAsync("prior-plan");

        client.Invalidate("prior-plan");

        (await client.GetTokenAsync("prior-plan")).AccessToken.Should().Be("token-2");
        handler.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneTokenRequest()
    {
        var (client, handler, _, _) = Build();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetTokenAsync("prior-plan")));

        results.Should().OnlyContain(r => r.AccessToken == "token-1");
        handler.Calls.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, SmartBackendTokenFailure.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, SmartBackendTokenFailure.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, SmartBackendTokenFailure.Unavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, SmartBackendTokenFailure.Unavailable)]
    [InlineData(HttpStatusCode.Found, SmartBackendTokenFailure.InvalidResponse)]
    public async Task RefusedTokenRequest_IsAFailureAndIsNotCached(HttpStatusCode status, SmartBackendTokenFailure expected)
    {
        var (client, handler, _, _) = Build();
        handler.Responder = _ => new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"error\":\"invalid_client\",\"error_description\":\"member SMITH\"}"),
        };

        var result = await client.GetTokenAsync("prior-plan");
        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(expected);
        result.AccessToken.Should().BeNull();

        handler.Responder = n => Issue($"token-{n}", 300);
        (await client.GetTokenAsync("prior-plan")).Succeeded.Should().BeTrue("a failure is not cached");
    }

    [Theory]
    [InlineData("{\"token_type\":\"bearer\",\"expires_in\":300}")]
    [InlineData("{\"access_token\":\"t\",\"token_type\":\"mac\",\"expires_in\":300}")]
    [InlineData("{\"access_token\":\"t\",\"expires_in\":300}")]
    [InlineData("{\"access_token\":\"t\",\"token_type\":\"bearer\",\"expires_in\":0}")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task UnusableTokenResponse_IsInvalid(string body)
    {
        var (client, handler, _, _) = Build();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };

        (await client.GetTokenAsync("prior-plan")).Failure.Should().Be(SmartBackendTokenFailure.InvalidResponse);
    }

    [Fact]
    public async Task MissingExpiresIn_AssumesFiveMinutes()
    {
        var (client, handler, _, _) = Build();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"access_token\":\"t\",\"token_type\":\"Bearer\"}"),
        };

        (await client.GetTokenAsync("prior-plan")).ExpiresAt.Should().Be(Now.AddMinutes(5));
    }

    [Fact]
    public async Task TransportFault_IsUnavailable()
    {
        var (client, handler, _, _) = Build();
        handler.Responder = _ => throw new HttpRequestException("connection refused");

        (await client.GetTokenAsync("prior-plan")).Failure.Should().Be(SmartBackendTokenFailure.Unavailable);
    }

    [Fact]
    public async Task UnknownRegistration_OrNoKeys_IsNotConfigured_AndCallsNothing()
    {
        var (client, handler, _, _) = Build();
        (await client.GetTokenAsync("someone-else")).Failure.Should().Be(SmartBackendTokenFailure.NotConfigured);
        (await client.GetTokenAsync("")).Failure.Should().Be(SmartBackendTokenFailure.NotConfigured);

        var noKeys = Options();
        noKeys.SigningKeys.Clear();
        var (keyless, keylessHandler, _, _) = Build(noKeys);
        (await keyless.GetTokenAsync("prior-plan")).Failure.Should().Be(SmartBackendTokenFailure.NotConfigured);

        handler.Calls.Should().BeEmpty();
        keylessHandler.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://prior-payer.test/token")]
    [InlineData("https://user:pw@prior-payer.test/token")]
    [InlineData("https://prior-payer.test/token?x=1")]
    [InlineData("/token")]
    [InlineData("")]
    public async Task UnsafeTokenEndpoint_IsNeverCalled(string endpoint)
    {
        var options = Options();
        options.Clients["prior-plan"].TokenEndpoint = endpoint;
        var (client, handler, _, _) = Build(options);

        (await client.GetTokenAsync("prior-plan")).Failure.Should().Be(SmartBackendTokenFailure.NotConfigured);
        handler.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("P-256", "ES256")]
    [InlineData("P-384", "ES384")]
    public async Task EcKey_SignsWithTheMatchingEs(string curve, string alg)
    {
        var (client, handler, _, keys) = Build(Options(new SmartBackendSigningKeyOptions
        {
            PrivateKeyPem = EcPem(ECCurve.CreateFromFriendlyName(curve == "P-256" ? "nistP256" : "nistP384")),
        }));

        await client.GetTokenAsync("prior-plan");

        new JsonWebToken(handler.Calls.Single().Form["client_assertion"]).Alg.Should().Be(alg);
        keys.PublicJwks()!["keys"]![0]!["crv"]!.GetValue<string>().Should().Be(curve);
    }

    [Fact]
    public void PublishedJwks_HasEveryKey_AndNoPrivateParts()
    {
        var options = Options(
            new SmartBackendSigningKeyOptions { PrivateKeyPem = RsaPem() },
            new SmartBackendSigningKeyOptions { PrivateKeyPem = EcPem(ECCurve.NamedCurves.nistP384), Standby = true });
        var keys = new SmartBackendKeyRing(Microsoft.Extensions.Options.Options.Create(options));

        var jwks = keys.PublicJwks()!;
        var published = jwks["keys"]!.AsArray().Select(k => k!.AsObject()).ToList();

        published.Should().HaveCount(2);
        published.Select(k => k["kty"]!.GetValue<string>()).Should().Equal("RSA", "EC");
        published.Should().OnlyContain(k => k["use"]!.GetValue<string>() == "sig");
        foreach (var privatePart in new[] { "d", "p", "q", "dp", "dq", "qi" })
            published.Should().OnlyContain(k => !k.ContainsKey(privatePart));
        keys.Active!.Algorithm.Should().Be("RS384", "the first key not in standby signs");
    }

    [Fact]
    public void DefaultKid_IsTheRfc7638Thumbprint()
    {
        var pem = RsaPem();
        var keys = new SmartBackendKeyRing(Microsoft.Extensions.Options.Options.Create(Options(
            new SmartBackendSigningKeyOptions { PrivateKeyPem = pem })));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var expected = Base64UrlEncoder.Encode(new JsonWebKey
        {
            Kty = "RSA",
            N = Base64UrlEncoder.Encode(rsa.ExportParameters(false).Modulus),
            E = Base64UrlEncoder.Encode(rsa.ExportParameters(false).Exponent),
        }.ComputeJwkThumbprint());

        keys.Active!.KeyId.Should().Be(expected);
    }

    [Fact]
    public void BadKeys_StopStartup()
    {
        SmartBackendKeyRing Load(params SmartBackendSigningKeyOptions[] entries)
            => new(Microsoft.Extensions.Options.Options.Create(new SmartBackendClientOptions { SigningKeys = entries.ToList() }));

        FluentActions.Invoking(() => Load(new SmartBackendSigningKeyOptions { PrivateKeyPem = RsaPem(1024) }))
            .Should().Throw<InvalidOperationException>().WithMessage("*2048*");
        FluentActions.Invoking(() => Load(new SmartBackendSigningKeyOptions { PrivateKeyPem = "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----" }))
            .Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => Load(new SmartBackendSigningKeyOptions { PrivateKeyPem = RsaPem(), Algorithm = "ES384" }))
            .Should().Throw<InvalidOperationException>().WithMessage("*RSA*");
        FluentActions.Invoking(() => Load(new SmartBackendSigningKeyOptions { PrivateKeyPem = EcPem(ECCurve.NamedCurves.nistP256), Algorithm = "ES384" }))
            .Should().Throw<InvalidOperationException>().WithMessage("*P-256*");
        FluentActions.Invoking(() => Load(new SmartBackendSigningKeyOptions { PrivateKeyPem = RsaPem(), Standby = true }))
            .Should().Throw<InvalidOperationException>().WithMessage("*Standby*");
        FluentActions.Invoking(() => Load(
                new SmartBackendSigningKeyOptions { PrivateKeyPem = RsaPem(), KeyId = "k1" },
                new SmartBackendSigningKeyOptions { PrivateKeyPem = RsaPem(), KeyId = "k1", Standby = true }))
            .Should().Throw<InvalidOperationException>().WithMessage("*k1*");
    }

    [Fact]
    public void NoKeys_PublishesNothing()
    {
        var keys = new SmartBackendKeyRing(Microsoft.Extensions.Options.Options.Create(new SmartBackendClientOptions()));
        keys.Active.Should().BeNull();
        keys.PublicJwks().Should().BeNull();
    }

    [Fact]
    public async Task PayerToPayerProvider_UsesTheEndpointsCredentialKey()
    {
        var (client, handler, _, _) = Build();
        var provider = new SmartBackendPayerToPayerCredentialProvider(client);
        PayerToPayerEndpoint Endpoint(string? key) => new()
        {
            PayerId = "PRIOR-PLAN", EndpointKey = "prior", CredentialKey = key,
            MemberMatchUri = new Uri("https://prior-payer.test/fhir/r4/Patient/$member-match"),
            MemberDataExportUri = new Uri("https://prior-payer.test/fhir/r4/PayerToPayer/$member-data-export"),
        };

        (await provider.GetAccessTokenAsync(Endpoint("prior-plan"))).Should().Be("token-1");
        (await provider.GetAccessTokenAsync(Endpoint(null))).Should().BeNull();
        (await provider.GetAccessTokenAsync(Endpoint("unregistered"))).Should().BeNull();

        provider.Invalidate(Endpoint("prior-plan"));
        (await provider.GetAccessTokenAsync(Endpoint("prior-plan"))).Should().Be("token-2");
        handler.Calls.Should().HaveCount(2);
    }
}
