using FhirService.Services.Identity;
using FluentAssertions;
using Xunit;

namespace CloudHealthOffice.FhirService.Tests.Identity;

/// <summary>
/// SEC-01 — fail-closed trust configuration.
///
/// Every case here is a deployment that would otherwise start successfully and
/// be wrong: trusting the demo issuer in production, trusting an issuer over
/// plain HTTP, accepting any audience, or accepting a signing algorithm that
/// makes signature validation meaningless. Startup is the only place these can
/// be caught before a token depends on them.
/// </summary>
public class SmartTrustConfigurationTests
{
    private static TrustedIssuerOptions ExternalIssuer(string issuer = "https://idp.example.com") =>
        new() { Issuer = issuer, Audiences = ["https://api.cloudhealthoffice.com"] };

    private static SmartTrustOptions External(params TrustedIssuerOptions[] issuers) =>
        new() { Mode = SmartTrustMode.ExternalIssuer, TrustedIssuers = [.. issuers] };

    // ── Demo must never reach production ──────────────────────────────────────

    [Fact]
    public void DemoMode_OnAProductionHost_FailsStartup()
    {
        // The failure this prevents is silent: everything works, and CHO trusts
        // the wrong authorization server.
        var options = new SmartTrustOptions
        {
            Mode = SmartTrustMode.Demo,
            Issuer = "https://auth.cloudhealthoffice.com",
            Audience = "fhir-api",
        };

        var act = () => options.Validate(isDevelopmentHost: false);

        act.Should().Throw<SmartTrustValidationException>()
            .WithMessage("*Demo*not a development host*");
    }

    [Fact]
    public void DemoMode_OnADevelopmentHost_IsAccepted()
    {
        var options = new SmartTrustOptions
        {
            Mode = SmartTrustMode.Demo,
            Issuer = "https://auth.cloudhealthoffice.com",
            Audience = "fhir-api",
        };

        options.Invoking(o => o.Validate(isDevelopmentHost: true)).Should().NotThrow();

        // The legacy single-issuer shape still resolves, so an existing Demo
        // deployment keeps working without being rewritten.
        options.NormalizedIssuers().Should().ContainSingle()
            .Which.Audiences.Should().ContainSingle().Which.Should().Be("fhir-api");
    }

    [Fact]
    public void ExternalIssuerMode_IgnoresTheLegacySingleIssuerFields()
    {
        // A production deployment states its trust in the explicit shape. Folding
        // the legacy fields in here would let a half-migrated config silently
        // keep trusting the demo issuer.
        var options = new SmartTrustOptions
        {
            Mode = SmartTrustMode.ExternalIssuer,
            Issuer = "https://auth.cloudhealthoffice.com",
            Audience = "fhir-api",
        };

        options.NormalizedIssuers().Should().BeEmpty();
        options.Invoking(o => o.Validate(false))
            .Should().Throw<SmartTrustValidationException>()
            .WithMessage("*TrustedIssuers is empty*");
    }

    // ── Issuer shape ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("not-a-uri")]
    [InlineData("/relative/path")]
    public void AnIssuerThatIsNotAnAbsoluteUri_FailsStartup(string issuer)
        => External(new TrustedIssuerOptions { Issuer = issuer, Audiences = ["a"] })
            .Invoking(o => o.Validate(false))
            .Should().Throw<SmartTrustValidationException>();

    [Fact]
    public void AnHttpIssuer_FailsStartupOutsideDevelopment()
        => External(ExternalIssuer("http://idp.example.com"))
            .Invoking(o => o.Validate(false))
            .Should().Throw<SmartTrustValidationException>()
            .WithMessage("*must use HTTPS*");

    [Fact]
    public void AnIssuerCarryingAQueryOrFragment_FailsStartup()
    {
        // `iss` is compared as an exact string, so an issuer pinned with a query
        // invites a match differing only in a part nobody meant to pin.
        External(ExternalIssuer("https://idp.example.com/?tenant=a"))
            .Invoking(o => o.Validate(false))
            .Should().Throw<SmartTrustValidationException>()
            .WithMessage("*query string or fragment*");
    }

    [Fact]
    public void DisablingHttpsMetadataOutsideDevelopment_FailsStartup()
    {
        var issuer = ExternalIssuer();
        issuer.RequireHttpsMetadata = false;

        External(issuer).Invoking(o => o.Validate(false))
            .Should().Throw<SmartTrustValidationException>()
            .WithMessage("*development-only*");
    }

    [Fact]
    public void TheSameIssuerConfiguredTwice_FailsStartup()
    {
        // Two entries for one issuer would make trust depend on list order.
        External(ExternalIssuer(), ExternalIssuer())
            .Invoking(o => o.Validate(false))
            .Should().Throw<SmartTrustValidationException>()
            .WithMessage("*more than once*");
    }

    // ── Audience ──────────────────────────────────────────────────────────────

    [Fact]
    public void AnIssuerWithNoAudience_FailsStartup()
    {
        // Without one, any token minted for any other API at the same issuer
        // would be accepted here.
        External(new TrustedIssuerOptions { Issuer = "https://idp.example.com" })
            .Invoking(o => o.Validate(false))
            .Should().Throw<SmartTrustValidationException>()
            .WithMessage("*no Audiences*");
    }

    // ── Algorithms ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    [InlineData("HS512")]
    public void AnUnsupportedOrSymmetricAlgorithm_FailsStartup(string algorithm)
    {
        // `none` removes signature validation outright. HMAC is the ingredient
        // of alg-confusion: a symmetric verifier accepts a token signed with the
        // issuer's PUBLIC key as the shared secret, and a resource server only
        // ever holds public keys.
        var issuer = ExternalIssuer();
        issuer.AllowedAlgorithms = [algorithm];

        External(issuer).Invoking(o => o.Validate(false))
            .Should().Throw<SmartTrustValidationException>()
            .WithMessage($"*'{algorithm}'*not supported*");
    }

    [Fact]
    public void TheSupportedAlgorithmSet_IsAsymmetricOnly()
    {
        TrustedIssuerOptions.SupportedAlgorithms.Should().NotContain("none");
        TrustedIssuerOptions.SupportedAlgorithms.Should()
            .OnlyContain(a => a.StartsWith("RS") || a.StartsWith("PS") || a.StartsWith("ES"));
    }

    [Fact]
    public void AnIssuerWithNoAlgorithmList_DefaultsToEveryAsymmetricAlgorithm()
        => ExternalIssuer().EffectiveAlgorithms()
            .Should().BeEquivalentTo(TrustedIssuerOptions.SupportedAlgorithms);

    // ── Registration authority (D-13): exactly one marked issuer ──────────────

    private static TrustedIssuerOptions Authority(string issuer = "https://auth.cloudhealthoffice.com") =>
        new() { Issuer = issuer, Audiences = ["fhir-api"], IssuesChoClientRegistrations = true };

    [Fact]
    public void ExternalIssuerMode_WithNoRegistrationAuthority_FailsStartup()
        => External(ExternalIssuer())
            .Invoking(o => o.Validate(false))
            .Should().Throw<SmartTrustValidationException>()
            .WithMessage("*No SmartAuth:TrustedIssuers entry sets IssuesChoClientRegistrations=true*");

    [Fact]
    public void ExternalIssuerMode_WithExactlyOneRegistrationAuthority_IsAccepted()
        => External(Authority(), ExternalIssuer())
            .Invoking(o => o.Validate(false))
            .Should().NotThrow();

    [Fact]
    public void ExternalIssuerMode_WithTwoRegistrationAuthorities_FailsStartup()
        => External(Authority(), Authority("https://idp.example.com"))
            .Invoking(o => o.Validate(false))
            .Should().Throw<SmartTrustValidationException>()
            .WithMessage("*More than one*IssuesChoClientRegistrations*");

    [Fact]
    public void TheFlag_IsOffByDefault()
        => new TrustedIssuerOptions().IssuesChoClientRegistrations.Should().BeFalse();

    [Fact]
    public void DemoMode_TheLegacyIssuer_IsTheRegistrationAuthority()
    {
        var options = new SmartTrustOptions
        {
            Mode = SmartTrustMode.Demo,
            Issuer = "https://auth.cloudhealthoffice.com",
            Audience = "fhir-api",
        };

        options.NormalizedIssuers().Single().IssuesChoClientRegistrations.Should().BeTrue();
        options.Invoking(o => o.Validate(isDevelopmentHost: true)).Should().NotThrow();
    }

    [Fact]
    public void DemoMode_WithExplicitTrustedIssuers_StillNeedsExactlyOneMarked()
    {
        var options = new SmartTrustOptions
        {
            Mode = SmartTrustMode.Demo,
            TrustedIssuers = [new TrustedIssuerOptions { Issuer = "http://localhost:5000", Audiences = ["fhir-api"], RequireHttpsMetadata = false }],
        };

        options.Invoking(o => o.Validate(isDevelopmentHost: true))
            .Should().Throw<SmartTrustValidationException>()
            .WithMessage("*IssuesChoClientRegistrations*");
    }

    // ── Clock skew ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(SmartTrustOptions.MaxClockSkewSeconds + 1)]
    public void ClockSkewOutsideTheAllowedBand_FailsStartup(int seconds)
    {
        // Skew large enough to meaningfully extend a token's life is lifetime
        // validation switched off wearing a clock-drift costume.
        var options = External(ExternalIssuer());
        options.ClockSkewSeconds = seconds;

        options.Invoking(o => o.Validate(false))
            .Should().Throw<SmartTrustValidationException>()
            .WithMessage("*ClockSkewSeconds*");
    }

    // ── D-26: an issuer that claims to serve providers must identify them ─────

    [Fact]
    public void AnIssuerServingProviderCallers_WithoutAnNpiClaim_FailsStartup()
    {
        // The silent failure this prevents: the deployment believes PAS binds
        // its callers, and PAS falls back to corroborating keys out of the
        // request body because no claim was ever configured to bind them with.
        var issuer = ExternalIssuer();
        issuer.ServesProviderCallers = true;

        External(issuer).Invoking(o => o.Validate(false))
            .Should().Throw<SmartTrustValidationException>()
            .WithMessage("*ServesProviderCallers*ProviderNpiClaim*");
    }

    [Fact]
    public void AnIssuerServingProviderCallers_WithAnNpiClaim_IsAccepted()
    {
        var issuer = ExternalIssuer();
        issuer.ServesProviderCallers = true;
        issuer.Claims.ProviderNpiClaim = "https://payer.example/npi";

        External(Authority(), issuer).Invoking(o => o.Validate(false)).Should().NotThrow();
    }

    [Fact]
    public void AnIssuerThatDeclaresNothing_NeedsNoNpiClaim()
    {
        // The tighten-only guarantee, asserted as configuration: a deployment
        // that has not opted in must keep starting exactly as it did. A patient
        // app's issuer has no NPI to assert, so requiring one of every issuer
        // would be requiring a claim that cannot exist.
        var issuer = ExternalIssuer();
        issuer.Claims.ProviderNpiClaim.Should().BeNull("it is unset by default");

        External(Authority(), issuer).Invoking(o => o.Validate(false)).Should().NotThrow();
    }
}
