using FhirService.Controllers;
using FhirService.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;
using Xunit;

namespace CloudHealthOffice.FhirService.Tests.Identity;

/// <summary>
/// SEC-01 — what SMART discovery advertises, and what it refuses to advertise.
///
/// A SMART configuration document is only useful if a client can act on it, and
/// `authorization_endpoint` / `token_endpoint` / `jwks_uri` are what it acts on.
/// Serving the document with those null hands the client something parseable
/// that cannot be used — and since this service does not omit nulls on
/// serialization, they would appear explicitly rather than being absent.
/// </summary>
public class SmartConfigurationEndpointTests
{
    private const string ExternalIdp = "https://idp.payer.example.com";

    private sealed class Fetcher(bool succeed) : IIssuerMetadataFetcher
    {
        public Task<IssuerMetadata> FetchAsync(TrustedIssuerOptions issuer, CancellationToken ct = default)
        {
            if (!succeed) throw new IssuerMetadataException("issuer unreachable");

            return Task.FromResult(new IssuerMetadata
            {
                Issuer = issuer.Issuer,
                JwksUri = $"{issuer.Issuer}/v1/keys",
                AuthorizationEndpoint = $"{issuer.Issuer}/v1/authorize",
                TokenEndpoint = $"{issuer.Issuer}/v1/token",
                SigningKeys = [new RsaSecurityKey(RSA.Create(2048)) { KeyId = "k1" }],
            });
        }
    }

    private static async Task<SmartConfigurationController> ControllerAsync(
        SmartTrustMode mode, bool discoverySucceeds = true)
    {
        var options = mode == SmartTrustMode.Demo
            ? new SmartTrustOptions
            {
                Mode = SmartTrustMode.Demo,
                Issuer = "https://auth.cloudhealthoffice.com",
                Audience = "fhir-api",
            }
            : new SmartTrustOptions
            {
                Mode = SmartTrustMode.ExternalIssuer,
                TrustedIssuers =
                [
                    new TrustedIssuerOptions
                    {
                        Issuer = ExternalIdp,
                        Audiences = ["https://api.cloudhealthoffice.com"],
                    }
                ],
            };

        var registry = new TrustedIssuerRegistry(options);
        var ring = new SmartSigningKeyRing(
            new Fetcher(discoverySucceeds), registry, NullLogger<SmartSigningKeyRing>.Instance);

        foreach (var issuer in registry.Issuers)
            await ring.TryPrimeAsync(issuer.Issuer);

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["SmartAuth:Issuer"] = options.Issuer }).Build();

        return new SmartConfigurationController(config, registry, ring)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public async Task ExternalIssuerMode_AdvertisesTheIssuersOwnDiscoveredEndpoints()
    {
        // Real issuers disagree about paths — Okta /v1/authorize, Entra
        // /oauth2/v2.0/authorize — so these must come from the document.
        var result = (await ControllerAsync(SmartTrustMode.ExternalIssuer))
            .GetSmartConfiguration();

        var body = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        var json = System.Text.Json.JsonSerializer.Serialize(body);

        json.Should().Contain($"{ExternalIdp}/v1/authorize");
        json.Should().Contain($"{ExternalIdp}/v1/token");
        json.Should().Contain($"{ExternalIdp}/v1/keys");
        json.Should().NotContain("/connect/authorize",
            "the demo issuer's OpenIddict paths must not be synthesized for an external IdP");
    }

    [Fact]
    public async Task BeforeDiscoveryCompletes_TheDocumentIsRefusedRatherThanServedWithNulls()
    {
        var result = (await ControllerAsync(SmartTrustMode.ExternalIssuer, discoverySucceeds: false))
            .GetSmartConfiguration();

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(503);

        System.Text.Json.JsonSerializer.Serialize(status.Value!)
            .Should().NotContain("null", "a client must never receive a null core endpoint");
    }

    [Fact]
    public async Task BeforeDiscoveryCompletes_TheResponseTellsTheClientToRetry()
    {
        var controller = await ControllerAsync(SmartTrustMode.ExternalIssuer, discoverySucceeds: false);
        controller.GetSmartConfiguration();

        controller.Response.Headers.RetryAfter.ToString().Should().NotBeEmpty();
    }

    [Fact]
    public async Task DemoMode_StillAdvertisesTheBundledIssuersEndpoints()
    {
        // CHO does host this flow, so it does know the paths.
        var result = (await ControllerAsync(SmartTrustMode.Demo)).GetSmartConfiguration();

        var json = System.Text.Json.JsonSerializer.Serialize(
            result.Should().BeOfType<OkObjectResult>().Subject.Value!);

        json.Should().Contain("/connect/authorize");
        json.Should().Contain("/connect/token");
    }
}
