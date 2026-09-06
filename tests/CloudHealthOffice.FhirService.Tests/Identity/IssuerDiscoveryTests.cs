using System.Net;
using System.Text;
using FhirService.Services.Identity;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CloudHealthOffice.FhirService.Tests.Identity;

/// <summary>
/// SEC-01 — OIDC discovery validation.
///
/// Discovery is a document fetched from the network that then tells CHO where
/// to get the keys it will verify tokens against. An unchecked discovery
/// response is therefore a redirection primitive aimed at the single decision
/// that matters, which is why each hop is validated before any of it becomes
/// trust material.
/// </summary>
public class IssuerDiscoveryTests
{
    private const string IssuerName = "https://idp.example.com";

    /// <summary>Serves scripted responses per URL, with no network involved.</summary>
    private sealed class StubHandler(Dictionary<string, (HttpStatusCode Status, string Body)> routes)
        : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);

            if (!routes.TryGetValue(url, out var route))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(route.Status)
            {
                Content = new StringContent(route.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubFactory(StubHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Env(bool development) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = development ? "Development" : "Production";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private const string ValidJwks = """
        {"keys":[{"kty":"RSA","use":"sig","kid":"key-A","alg":"RS256",
        "n":"sXchDaQebHnPiGvyDOAT4saGEUetSyo9MKLOoWFsueri23bOdgWp4Dy1WlUzewbgBHod5pcM9H95GQRV3JDXboIRROSBigeC5yjU1hGzHHyXss8UDprecbAYxknTcQkhslANGRUZmdTOQ5qTRsLAt6BTYuyvVRdhS8exSZEy_c4gs_7svlJJQ4H9_NxsiIoLwAEk7-Q3UXERGYw_75IDrGA84-lA_-Ct4eTlXHBIY2EaV7t7LjJaynVJCpkv4LKjTTAumiGUIuQhrNhZLuF_RJLqHpM2kgWFLU7-VTdL1VbC2tejvcI2BlMkEpk1BzBZI0KQB0GaDWFLN-aEAw3vRw","e":"AQAB"}]}
        """;

    private static (HttpIssuerMetadataFetcher Fetcher, StubHandler Handler) Build(
        Dictionary<string, (HttpStatusCode, string)> routes, bool development = false)
    {
        var handler = new StubHandler(routes);
        return (new HttpIssuerMetadataFetcher(new StubFactory(handler), new Env(development)), handler);
    }

    private static TrustedIssuerOptions Issuer(params string[] additionalHosts) => new()
    {
        Issuer = IssuerName,
        Audiences = ["https://api.cloudhealthoffice.com"],
        AdditionalJwksHosts = [.. additionalHosts],
    };

    private static string Discovery(string issuer, string jwksUri) => $$"""
        {"issuer":"{{issuer}}","jwks_uri":"{{jwksUri}}",
         "authorization_endpoint":"{{issuer}}/v1/authorize",
         "token_endpoint":"{{issuer}}/v1/token"}
        """;

    // ── The happy path, and what it advertises ────────────────────────────────

    [Fact]
    public async Task ValidDiscovery_YieldsKeysAndTheIssuersRealEndpoints()
    {
        var (fetcher, _) = Build(new()
        {
            [$"{IssuerName}/.well-known/openid-configuration"] =
                (HttpStatusCode.OK, Discovery(IssuerName, $"{IssuerName}/v1/keys")),
            [$"{IssuerName}/v1/keys"] = (HttpStatusCode.OK, ValidJwks),
        });

        var metadata = await fetcher.FetchAsync(Issuer());

        metadata.Issuer.Should().Be(IssuerName);
        metadata.SigningKeys.Should().ContainSingle();
        metadata.KeyIds.Should().Contain("key-A");

        // Real issuers disagree about paths — Okta serves /v1/authorize, Entra
        // /oauth2/v2.0/authorize — so these must come from the document, never
        // from string concatenation.
        metadata.AuthorizationEndpoint.Should().Be($"{IssuerName}/v1/authorize");
        metadata.TokenEndpoint.Should().Be($"{IssuerName}/v1/token");
    }

    // ── Issuer binding ────────────────────────────────────────────────────────

    [Fact]
    public async Task ADocumentDeclaringADifferentIssuer_IsRejected()
    {
        // The single check that binds a fetched document to the trust
        // relationship it claims to describe.
        var (fetcher, _) = Build(new()
        {
            [$"{IssuerName}/.well-known/openid-configuration"] =
                (HttpStatusCode.OK, Discovery("https://attacker.test", $"{IssuerName}/v1/keys")),
        });

        await fetcher.Invoking(f => f.FetchAsync(Issuer()))
            .Should().ThrowAsync<IssuerMetadataException>()
            .WithMessage("*issuer mismatch*");
    }

    // ── SSRF via the discovery document ───────────────────────────────────────

    [Fact]
    public async Task ADocumentPointingJwksAtAnUntrustedHost_IsRejectedWithoutFetchingIt()
    {
        var (fetcher, handler) = Build(new()
        {
            [$"{IssuerName}/.well-known/openid-configuration"] =
                (HttpStatusCode.OK, Discovery(IssuerName, "https://attacker.test/jwks.json")),
            ["https://attacker.test/jwks.json"] = (HttpStatusCode.OK, ValidJwks),
        });

        await fetcher.Invoking(f => f.FetchAsync(Issuer()))
            .Should().ThrowAsync<IssuerMetadataException>()
            .WithMessage("*attacker.test*not permitted*");

        handler.Requested.Should().NotContain(u => u.Contains("attacker.test"),
            "the refusal must happen before the request, or the SSRF already occurred");
    }

    [Fact]
    public async Task ADocumentPointingJwksAtInstanceMetadata_IsRejected()
    {
        var (fetcher, handler) = Build(new()
        {
            [$"{IssuerName}/.well-known/openid-configuration"] =
                (HttpStatusCode.OK, Discovery(IssuerName, "http://169.254.169.254/latest/meta-data/")),
        });

        await fetcher.Invoking(f => f.FetchAsync(Issuer()))
            .Should().ThrowAsync<IssuerMetadataException>();

        handler.Requested.Should().NotContain(u => u.Contains("169.254.169.254"));
    }

    [Fact]
    public async Task AnAdministratorListedJwksHost_IsAccepted()
    {
        var (fetcher, _) = Build(new()
        {
            [$"{IssuerName}/.well-known/openid-configuration"] =
                (HttpStatusCode.OK, Discovery(IssuerName, "https://keys.example-cdn.net/jwks.json")),
            ["https://keys.example-cdn.net/jwks.json"] = (HttpStatusCode.OK, ValidJwks),
        });

        var metadata = await fetcher.FetchAsync(Issuer("keys.example-cdn.net"));
        metadata.SigningKeys.Should().ContainSingle();
    }

    // ── Malformed and unavailable ─────────────────────────────────────────────

    [Fact]
    public async Task AMalformedDiscoveryDocument_IsRejected()
    {
        var (fetcher, _) = Build(new()
        {
            [$"{IssuerName}/.well-known/openid-configuration"] = (HttpStatusCode.OK, "not json"),
        });

        await fetcher.Invoking(f => f.FetchAsync(Issuer()))
            .Should().ThrowAsync<IssuerMetadataException>().WithMessage("*not valid JSON*");
    }

    [Fact]
    public async Task ADocumentWithNoJwksUri_IsRejected()
    {
        var (fetcher, _) = Build(new()
        {
            [$"{IssuerName}/.well-known/openid-configuration"] =
                (HttpStatusCode.OK, $$"""{"issuer":"{{IssuerName}}"}"""),
        });

        await fetcher.Invoking(f => f.FetchAsync(Issuer()))
            .Should().ThrowAsync<IssuerMetadataException>().WithMessage("*jwks_uri*");
    }

    [Fact]
    public async Task AnUnavailableIssuer_FailsClosed()
    {
        var (fetcher, _) = Build(new()
        {
            [$"{IssuerName}/.well-known/openid-configuration"] =
                (HttpStatusCode.ServiceUnavailable, ""),
        });

        await fetcher.Invoking(f => f.FetchAsync(Issuer()))
            .Should().ThrowAsync<IssuerMetadataException>().WithMessage("*503*");
    }

    // ── Key hygiene at ingestion ──────────────────────────────────────────────

    [Fact]
    public async Task ASymmetricKeyInTheJwks_IsRefusedAtIngestion()
    {
        // Filtered at the door rather than relied on being unreachable later.
        // A symmetric key from a third-party issuer has no legitimate role in
        // resource-server validation and is the ingredient of alg-confusion.
        var (fetcher, _) = Build(new()
        {
            [$"{IssuerName}/.well-known/openid-configuration"] =
                (HttpStatusCode.OK, Discovery(IssuerName, $"{IssuerName}/v1/keys")),
            [$"{IssuerName}/v1/keys"] = (HttpStatusCode.OK,
                """{"keys":[{"kty":"oct","kid":"shared","alg":"HS256","k":"c2VjcmV0LWtleS12YWx1ZQ"}]}"""),
        });

        await fetcher.Invoking(f => f.FetchAsync(Issuer()))
            .Should().ThrowAsync<IssuerMetadataException>()
            .WithMessage("*no usable signing key*");
    }

    [Fact]
    public async Task AKeyWhoseAlgorithmTheIssuerDoesNotAllow_IsFilteredOut()
    {
        var issuer = Issuer();
        issuer.AllowedAlgorithms = ["ES256"];   // the JWKS below publishes RS256

        var (fetcher, _) = Build(new()
        {
            [$"{IssuerName}/.well-known/openid-configuration"] =
                (HttpStatusCode.OK, Discovery(IssuerName, $"{IssuerName}/v1/keys")),
            [$"{IssuerName}/v1/keys"] = (HttpStatusCode.OK, ValidJwks),
        });

        await fetcher.Invoking(f => f.FetchAsync(issuer))
            .Should().ThrowAsync<IssuerMetadataException>()
            .WithMessage("*no usable signing key*ES256*");
    }

    // ── Explicit JWKS skips discovery ─────────────────────────────────────────

    [Fact]
    public async Task AnExplicitJwksUri_SkipsDiscoveryEntirely()
    {
        // One less network-supplied value to validate: the administrator has
        // already named the endpoint.
        var issuer = Issuer();
        issuer.JwksUri = $"{IssuerName}/static/jwks.json";

        var (fetcher, handler) = Build(new()
        {
            [$"{IssuerName}/static/jwks.json"] = (HttpStatusCode.OK, ValidJwks),
        });

        var metadata = await fetcher.FetchAsync(issuer);

        metadata.SigningKeys.Should().ContainSingle();
        handler.Requested.Should().NotContain(u => u.Contains("openid-configuration"));
    }
}
