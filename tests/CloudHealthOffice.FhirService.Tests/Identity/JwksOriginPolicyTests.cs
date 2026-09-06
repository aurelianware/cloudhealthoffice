using FhirService.Services.Identity;
using FluentAssertions;
using Xunit;

namespace CloudHealthOffice.FhirService.Tests.Identity;

/// <summary>
/// SEC-01 — SSRF boundary on discovery and JWKS retrieval.
///
/// The attack these close is specific. A resource server that reads a token's
/// <c>iss</c> and fetches whatever that host serves has handed an
/// unauthenticated caller two things at once: an outbound request primitive
/// aimed at its own network, and the ability to nominate the key its token will
/// be verified against. So the rule is inverted from "block known-bad" — a
/// fetch target is refused unless configuration already named it.
/// </summary>
public class JwksOriginPolicyTests
{
    private static TrustedIssuerOptions Issuer(params string[] additionalHosts) => new()
    {
        Issuer = "https://idp.example.com",
        Audiences = ["https://api.cloudhealthoffice.com"],
        AdditionalJwksHosts = [.. additionalHosts],
    };

    [Fact]
    public void TheIssuersOwnHost_IsAllowed()
        => JwksOriginPolicy.IsAllowedHost(
            new Uri("https://idp.example.com/.well-known/jwks.json"), Issuer(), false)
            .Should().BeTrue();

    [Fact]
    public void AnUnlistedThirdPartyHost_IsRefused()
    {
        // This is the discovery-redirection case: a document that points key
        // retrieval somewhere the operator never approved.
        JwksOriginPolicy.IsAllowedHost(
            new Uri("https://attacker.test/jwks.json"), Issuer(), false)
            .Should().BeFalse();
    }

    [Fact]
    public void AnExplicitlyListedHost_IsAllowed()
    {
        // Some managed IdPs legitimately serve keys from a sibling CDN host, so
        // a blanket refusal would be wrong — the allowance just has to be
        // written down by an administrator rather than taken from the document.
        JwksOriginPolicy.IsAllowedHost(
            new Uri("https://keys.example-cdn.net/jwks.json"),
            Issuer("keys.example-cdn.net"), false)
            .Should().BeTrue();
    }

    [Fact]
    public void ASuffixLookalikeHost_IsRefused()
    {
        // idp.example.com.attacker.test defeats any prefix or suffix match.
        JwksOriginPolicy.IsAllowedHost(
            new Uri("https://idp.example.com.attacker.test/jwks.json"), Issuer(), false)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("http://127.0.0.1/jwks.json")]
    [InlineData("http://localhost/jwks.json")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]  // cloud instance metadata
    [InlineData("http://10.0.0.5/jwks.json")]
    [InlineData("http://172.16.4.4/jwks.json")]
    [InlineData("http://192.168.1.10/jwks.json")]
    public void PrivateAndLoopbackTargets_AreRefusedOutsideDevelopment(string url)
    {
        // 169.254.169.254 is the reason this list exists rather than just
        // "same-origin only": instance metadata is the classic SSRF payoff.
        JwksOriginPolicy.IsAllowedHost(new Uri(url), Issuer("127.0.0.1", "localhost",
            "169.254.169.254", "10.0.0.5", "172.16.4.4", "192.168.1.10"), false)
            .Should().BeFalse("an explicit allow-list entry must not override the private-address bar");
    }

    [Fact]
    public void ALoopbackTarget_IsAllowedInDevelopment()
    {
        // A developer running Keycloak locally has to be able to work.
        JwksOriginPolicy.IsAllowedHost(
            new Uri("http://localhost:8080/realms/cho/protocol/openid-connect/certs"),
            Issuer("localhost"), isDevelopmentHost: true)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("https://8.8.8.8/jwks.json", false)]
    [InlineData("https://[::1]/jwks.json", true)]
    [InlineData("https://[fe80::1]/jwks.json", true)]
    public void PrivateAddressDetection_CoversIpv4AndIpv6(string url, bool expectedPrivate)
        => JwksOriginPolicy.IsPrivateOrLoopback(new Uri(url)).Should().Be(expectedPrivate);

    [Fact]
    public void ConfigurationNamingAPrivateJwksHost_FailsStartupOutsideDevelopment()
    {
        // The policy is enforced at startup too, not only at fetch time — a
        // deployment should not have to receive a request to discover this.
        var issuer = new TrustedIssuerOptions
        {
            Issuer = "https://idp.example.com",
            Audiences = ["api"],
            JwksUri = "https://169.254.169.254/jwks.json",
        };

        issuer.Invoking(i => i.Validate(false))
            .Should().Throw<SmartTrustValidationException>()
            .WithMessage("*169.254.169.254*AdditionalJwksHosts*");
    }
}
