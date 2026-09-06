using FhirService.Services.Identity;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;
using Xunit;

namespace CloudHealthOffice.FhirService.Tests.Identity;

/// <summary>
/// SEC-01 — identity trust readiness.
///
/// The distinction this check has to keep is between OPERATIONAL health and
/// token outcomes. A flood of 401s because clients are presenting expired
/// tokens is a healthy resource server doing its job; an issuer whose keys have
/// never loaded is a resource server that will reject everyone. Conflating them
/// makes the signal fire during an attack and stay silent during an outage.
/// </summary>
public class SmartIdentityTrustHealthCheckTests
{
    private sealed class Fetcher(params string[] failingIssuers) : IIssuerMetadataFetcher
    {
        public Task<IssuerMetadata> FetchAsync(TrustedIssuerOptions issuer, CancellationToken ct = default)
        {
            if (failingIssuers.Contains(issuer.Issuer))
                throw new IssuerMetadataException("issuer unreachable");

            return Task.FromResult(new IssuerMetadata
            {
                Issuer = issuer.Issuer,
                JwksUri = $"{issuer.Issuer}/jwks",
                SigningKeys = [new RsaSecurityKey(RSA.Create(2048)) { KeyId = "k1" }],
            });
        }
    }

    private static async Task<HealthCheckResult> CheckAsync(
        IIssuerMetadataFetcher fetcher, params string[] issuerNames)
    {
        var options = new SmartTrustOptions
        {
            Mode = SmartTrustMode.ExternalIssuer,
            TrustedIssuers = issuerNames
                .Select(n => new TrustedIssuerOptions { Issuer = n, Audiences = ["api"] })
                .ToList(),
        };

        var registry = new TrustedIssuerRegistry(options);
        var ring = new SmartSigningKeyRing(fetcher, registry, NullLogger<SmartSigningKeyRing>.Instance);

        foreach (var name in issuerNames)
            await ring.TryPrimeAsync(name);

        return await new SmartIdentityTrustHealthCheck(ring, registry)
            .CheckHealthAsync(new HealthCheckContext());
    }

    [Fact]
    public async Task AllIssuersWithKeys_IsHealthy()
        => (await CheckAsync(new Fetcher(), "https://a.example.com", "https://b.example.com"))
            .Status.Should().Be(HealthStatus.Healthy);

    [Fact]
    public async Task NoIssuerWithKeys_IsUnhealthy()
    {
        // Authentication is unavailable: every token will be rejected regardless
        // of who presents it.
        var result = await CheckAsync(
            new Fetcher("https://a.example.com"), "https://a.example.com");

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("authentication is unavailable");
    }

    [Fact]
    public async Task SomeIssuersWithKeys_IsDegradedNotUnhealthy()
    {
        // Callers of the healthy issuer are served normally, so pulling the
        // instance out of rotation would not fix the broken one.
        var result = await CheckAsync(
            new Fetcher("https://b.example.com"), "https://a.example.com", "https://b.example.com");

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task TheReportedData_CarriesNoKeyMaterialOrErrorDetail()
    {
        // A health endpoint is frequently the least-authenticated surface a
        // service has, and a failing IdP's error text can carry hostnames and
        // request identifiers.
        var result = await CheckAsync(
            new Fetcher("https://a.example.com"), "https://a.example.com");

        var rendered = string.Join(" ", result.Data.Select(kv => $"{kv.Key}={kv.Value}"));

        rendered.Should().NotContain("BEGIN");
        rendered.Should().NotContain("issuer unreachable", "the raw failure message must not leak");
        rendered.Should().Contain("retrieval failed", "a category is reported instead");
        result.Data.Should().ContainKey("mode");
    }
}
