using System.Security.Claims;
using FhirService.Services.Identity;
using FluentAssertions;
using Xunit;

namespace CloudHealthOffice.FhirService.Tests.Identity;

/// <summary>
/// SEC-01 — caller identity, and the one rule that makes provider identity
/// safe to act on.
///
/// An NPI is public information, so a claim merely NAMED <c>npi</c> proves
/// nothing: any IdP can emit any claim, and so can any IdP an attacker controls.
/// It becomes authoritative only when a named issuer CHO already trusts was
/// configured, by an administrator, to assert it. These tests pin that
/// distinction, because getting it wrong would turn a public number into an
/// authorization decision.
/// </summary>
public class CallerIdentityResolverTests
{
    private const string TrustedIssuer = "https://idp.example.com";

    private static CallerIdentityResolver Resolver(
        Action<IssuerClaimMappingOptions>? configureClaims = null,
        params string[] tenants)
    {
        var claims = new IssuerClaimMappingOptions();
        configureClaims?.Invoke(claims);

        var options = new SmartTrustOptions
        {
            Mode = SmartTrustMode.ExternalIssuer,
            TrustedIssuers =
            [
                new TrustedIssuerOptions
                {
                    Issuer = TrustedIssuer,
                    Audiences = ["https://api.cloudhealthoffice.com"],
                    Claims = claims,
                    Tenants = [.. tenants],
                }
            ],
        };

        return new CallerIdentityResolver(new TrustedIssuerRegistry(options));
    }

    private static ClaimsPrincipal Token(string issuer = TrustedIssuer, params (string Type, string Value)[] claims)
    {
        var list = new List<Claim> { new("iss", issuer) };
        list.AddRange(claims.Select(c => new Claim(c.Type, c.Value)));
        return new ClaimsPrincipal(new ClaimsIdentity(list, "Bearer"));
    }

    // ── Provider identity: the trust rule ─────────────────────────────────────

    [Fact]
    public void AnNpiClaim_IsIgnored_WhenTheIssuerWasNotConfiguredToAssertOne()
    {
        // The default posture. The token says npi=1234567893; CHO has not been
        // told that this issuer speaks for provider identity, so it does not.
        var caller = Resolver().Resolve(Token(claims: ("npi", "1234567893")));

        caller.Should().NotBeNull();
        caller!.ProviderNpi.Should().BeNull();
        caller.HasVerifiedProviderIdentity.Should().BeFalse();
    }

    [Fact]
    public void AnNpiClaim_BindsTheCaller_WhenTheTrustedIssuerMapsIt()
    {
        var caller = Resolver(c => c.ProviderNpiClaim = "npi")
            .Resolve(Token(claims: ("npi", "1234567893")));

        caller!.ProviderNpi.Should().Be("1234567893");
        caller.HasVerifiedProviderIdentity.Should().BeTrue();
    }

    [Fact]
    public void ADifferentlyNamedClaim_IsNotDiscoveredByConvention()
    {
        // Only the configured claim is read. Scanning for conventionally named
        // claims would reintroduce exactly the trust this design refuses.
        var caller = Resolver(c => c.ProviderNpiClaim = "https://payer.example/npi")
            .Resolve(Token(claims: ("npi", "1234567893")));

        caller!.ProviderNpi.Should().BeNull();
    }

    [Theory]
    [InlineData("123")]                  // too short
    [InlineData("12345678901")]          // too long
    [InlineData("123456789X")]           // not digits
    [InlineData("  ")]
    public void AMalformedNpiClaim_IsDropped(string value)
    {
        // A malformed value that reached a comparison would either never match
        // (harmless) or match something it should not. Neither is worth keeping.
        Resolver(c => c.ProviderNpiClaim = "npi")
            .Resolve(Token(claims: ("npi", value)))!
            .ProviderNpi.Should().BeNull();
    }

    // ── Issuer gating ─────────────────────────────────────────────────────────

    [Fact]
    public void APrincipalFromAnUntrustedIssuer_ResolvesToNothing()
        => Resolver().Resolve(Token(issuer: "https://attacker.test")).Should().BeNull();

    [Fact]
    public void AnUnauthenticatedPrincipal_ResolvesToNothing()
        => Resolver().Resolve(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeNull();

    // ── Caller shape ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("patient/*.read", SmartCallerType.Patient)]
    [InlineData("user/Patient.read", SmartCallerType.User)]
    [InlineData("system/Claim.write", SmartCallerType.System)]
    [InlineData("openid profile", SmartCallerType.Unknown)]
    public void CallerType_FollowsTheGrantedScopes(string scope, SmartCallerType expected)
        => Resolver().Resolve(Token(claims: ("scope", scope)))!.CallerType.Should().Be(expected);

    [Fact]
    public void AMixedPatientAndSystemGrant_IsTreatedAsPatientContext()
    {
        // Patient context is the one that CONSTRAINS. Reading an ambiguous grant
        // as system would drop the patient binding and WIDEN the token; reading
        // it as patient only narrows it. When a grant is ambiguous the narrower
        // reading is the safe one.
        Resolver().Resolve(Token(claims: ("scope", "system/*.read patient/*.read")))!
            .CallerType.Should().Be(SmartCallerType.Patient);
    }

    [Fact]
    public void ScopesComeFromBothScopeAndScpClaims()
    {
        // Matching SmartScopeEnforcementMiddleware exactly. Two scope parsers
        // that drift are two different answers to "what may this caller do".
        var caller = Resolver().Resolve(Token(
            claims: [("scope", "user/Patient.read"), ("scp", "user/Claim.write")]));

        caller!.Scopes.Should().BeEquivalentTo(["user/Patient.read", "user/Claim.write"]);
    }

    // ── Patient and tenant ────────────────────────────────────────────────────

    [Fact]
    public void ThePatientBinding_IsReadFromTheTokenAndUnprefixed()
        => Resolver().Resolve(Token(claims: ("patient", "Patient/pat-001")))!
            .PatientId.Should().Be("pat-001");

    [Fact]
    public void TheTenantClaim_IsReadOnlyWhenTheIssuerMapsIt()
    {
        Resolver().Resolve(Token(claims: ("org", "tenant-a")))!.TenantClaim.Should().BeNull();

        Resolver(c => c.TenantClaim = "org").Resolve(Token(claims: ("org", "tenant-a")))!
            .TenantClaim.Should().Be("tenant-a");
    }

    // ── Issuer/tenant scoping ─────────────────────────────────────────────────

    [Fact]
    public void AnIssuerScopedToTenants_MayNotServeAnother()
    {
        // This is what stops customer A's IdP from authenticating into customer
        // B's data, whatever claims its tokens carry.
        var issuer = new TrustedIssuerOptions
        {
            Issuer = TrustedIssuer,
            Audiences = ["api"],
            Tenants = ["tenant-a"],
        };

        TrustedIssuerRegistry.IssuerMayServeTenant(issuer, "tenant-a").Should().BeTrue();
        TrustedIssuerRegistry.IssuerMayServeTenant(issuer, "tenant-b").Should().BeFalse();
        TrustedIssuerRegistry.IssuerMayServeTenant(issuer, null).Should().BeFalse();
    }

    [Fact]
    public void AnIssuerWithNoTenantList_MayServeAny()
        => TrustedIssuerRegistry.IssuerMayServeTenant(
            new TrustedIssuerOptions { Issuer = TrustedIssuer, Audiences = ["api"] }, "anything")
            .Should().BeTrue();

    // ── Issuer resolution is exact ────────────────────────────────────────────

    [Theory]
    [InlineData("https://idp.example.com/")]           // trailing slash
    [InlineData("HTTPS://IDP.EXAMPLE.COM")]            // case
    [InlineData("https://idp.example.com.attacker.test")]
    [InlineData("https://idp.example.com/../evil")]
    public void IssuerResolution_RejectsNormalizationLookalikes(string issuer)
    {
        // `iss` is an exact string by RFC 7519. Any place two spellings of one
        // issuer can both match is a place they can diverge.
        var options = new SmartTrustOptions
        {
            Mode = SmartTrustMode.ExternalIssuer,
            TrustedIssuers = [new TrustedIssuerOptions
            {
                Issuer = TrustedIssuer, Audiences = ["api"],
            }],
        };

        new TrustedIssuerRegistry(options).Resolve(issuer).Should().BeNull();
    }

    // ── Audit projection ──────────────────────────────────────────────────────

    [Fact]
    public void TheAuditProjection_CarriesNoScopesNoClaimsAndStripsNewlines()
    {
        var caller = Resolver(c => c.ProviderNpiClaim = "npi").Resolve(Token(claims:
            [("sub", "user-1\r\ninjected"), ("npi", "1234567893"), ("scope", "patient/*.read")]))!;

        var audit = caller.ToAuditFields();

        audit["subject"].Should().Be("user-1injected", "CR/LF must not reach a log line (CWE-117)");
        audit["providerIdentity"].Should().Be("asserted",
            "the audit records THAT identity was verified, not the NPI itself");
        audit.Values.Should().NotContain(v => v.Contains("patient/*.read"));
        audit.Should().NotContainKey("scopes");
    }
}
