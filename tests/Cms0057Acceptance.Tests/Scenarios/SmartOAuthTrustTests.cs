using System.Security.Claims;
using System.Security.Cryptography;
using FhirService.Services.Identity;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// SEC-01 — production SMART on FHIR / OAuth trust, executed against the REAL
/// token validation parameters the FHIR resource server runs
/// (<see cref="SmartTokenValidation.CreateParameters"/>) and the real key ring.
///
/// These tests answer the four questions authentication is responsible for —
/// who issued this token, is it cryptographically valid, was it intended for
/// CHO, and who is the caller — for a deployment behind an externally managed
/// identity provider. SMART authorization (what the caller may do) is proven
/// separately in SecurityConsentMetricsTests; the two concerns are deliberately
/// not collapsed.
///
/// Traceability:
///   trust      src/services/fhir-service/Services/Identity/SmartTrustOptions.cs
///   validate   src/services/fhir-service/Services/Identity/SmartTokenValidation.cs
///   keys       src/services/fhir-service/Services/Identity/SmartSigningKeyRing.cs
///   identity   src/services/fhir-service/Services/Identity/CallerIdentityResolver.cs
/// </summary>
public class SmartOAuthTrustTests
{
    private const string PayerIdp = "https://idp.payer.example.com";
    private const string OtherIdp = "https://idp.other.example.com";
    private const string ChoAudience = "https://api.cloudhealthoffice.com";

    // Two issuers, each with its own key — the configuration that makes
    // "first matching key wins" observably wrong.
    private static readonly RsaSecurityKey PayerKey =
        new(RSA.Create(2048)) { KeyId = "payer-key-1" };
    private static readonly RsaSecurityKey OtherKey =
        new(RSA.Create(2048)) { KeyId = "other-key-1" };

    private sealed class StaticKeys : IIssuerMetadataFetcher
    {
        public Dictionary<string, List<SecurityKey>> Keys { get; } = new(StringComparer.Ordinal)
        {
            [PayerIdp] = [PayerKey],
            [OtherIdp] = [OtherKey],
        };

        public Task<IssuerMetadata> FetchAsync(TrustedIssuerOptions issuer, CancellationToken ct = default)
            => Task.FromResult(new IssuerMetadata
            {
                Issuer = issuer.Issuer,
                JwksUri = $"{issuer.Issuer}/jwks",
                SigningKeys = Keys.TryGetValue(issuer.Issuer, out var k) ? k : [],
            });
    }

    private static (TokenValidationParameters Parameters, TrustedIssuerRegistry Registry) Server(
        Action<TrustedIssuerOptions>? configurePayer = null)
    {
        var payer = new TrustedIssuerOptions
        {
            Issuer = PayerIdp,
            Audiences = [ChoAudience],
            Claims = new IssuerClaimMappingOptions { ProviderNpiClaim = "npi" },
        };
        configurePayer?.Invoke(payer);

        var options = new SmartTrustOptions
        {
            Mode = SmartTrustMode.ExternalIssuer,
            TrustedIssuers =
            [
                payer,
                new TrustedIssuerOptions
                {
                    Issuer = OtherIdp,
                    Audiences = ["https://api.other-payer.example.com"],
                },
            ],
        };
        options.Validate(isDevelopmentHost: false);

        var registry = new TrustedIssuerRegistry(options);
        var keyRing = new SmartSigningKeyRing(
            new StaticKeys(), registry, NullLogger<SmartSigningKeyRing>.Instance);

        return (SmartTokenValidation.CreateParameters(registry, keyRing), registry);
    }

    private static string Mint(
        SecurityKey key,
        string issuer = PayerIdp,
        string audience = ChoAudience,
        string algorithm = SecurityAlgorithms.RsaSha256,
        DateTime? notBefore = null,
        DateTime? expires = null,
        params Claim[] claims)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(key, algorithm),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static async Task<TokenValidationResult> ValidateAsync(
        string token, TokenValidationParameters parameters)
        => await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);

    // ── Trusted issuer ────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public async Task SEC01_Replace_ATokenFromAConfiguredIssuerIsAccepted()
    {
        var (parameters, _) = Server();
        var result = await ValidateAsync(Mint(PayerKey), parameters);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public async Task SEC01_Replace_AnUnknownIssuerIsRejected()
    {
        // Trust is administrator-controlled. A correctly signed token from an
        // issuer nobody configured is exactly the trust-on-first-use case the
        // registry exists to refuse.
        var (parameters, _) = Server();
        var rogue = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "rogue" };

        var result = await ValidateAsync(Mint(rogue, issuer: "https://attacker.test"), parameters);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public async Task SEC01_Replace_OneTrustedIssuersKeyCannotSignAnotherIssuersToken()
    {
        // The property that makes multi-issuer trust real rather than a shared
        // trust blob: the issuer is resolved FIRST and supplies its own keys.
        var (parameters, _) = Server();

        var result = await ValidateAsync(Mint(OtherKey, issuer: PayerIdp), parameters);

        result.IsValid.Should().BeFalse(
            "a token claiming the payer IdP must not validate against another issuer's key");
    }

    // ── Signature ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public async Task SEC01_Replace_ATamperedTokenIsRejected()
    {
        var (parameters, _) = Server();
        var token = Mint(PayerKey, claims: new Claim("scope", "patient/Patient.read"));

        // Flip a character in the payload; the signature no longer covers it.
        var parts = token.Split('.');
        parts[1] = parts[1][..^2] + (parts[1][^2] == 'A' ? 'B' : 'A') + parts[1][^1];

        var result = await ValidateAsync(string.Join('.', parts), parameters);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public async Task SEC01_Replace_AnUnsignedAlgNoneTokenIsRejected()
    {
        // RequireSignedTokens. An unsigned token is not a weakly authenticated
        // caller; it is an unauthenticated one wearing a JWT.
        var (parameters, _) = Server();

        var header = Base64Url("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64Url(
            $$"""{"iss":"{{PayerIdp}}","aud":"{{ChoAudience}}","exp":{{DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()}}}""");

        var result = await ValidateAsync($"{header}.{payload}.", parameters);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public async Task SEC01_Replace_AnAlgorithmOutsideTheIssuersPolicyIsRejected()
    {
        // ValidAlgorithms is the union across issuers, so the per-issuer check
        // is what keeps an issuer restricted to ES256 from accepting RS256.
        var (parameters, registry) = Server(payer => payer.AllowedAlgorithms = ["ES256"]);

        var token = Mint(PayerKey);            // RS256
        var result = await ValidateAsync(token, parameters);

        var acceptedPerIssuer = result.IsValid
            && SmartTokenValidation.AlgorithmIsAcceptedForIssuer(
                registry, result.SecurityToken, PayerIdp);

        acceptedPerIssuer.Should().BeFalse();
    }

    // ── Audience ──────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public async Task SEC01_Replace_ATokenForAnotherApiIsRejected()
    {
        // Correctly signed, from a trusted issuer, and never meant for CHO.
        // Issuer plus signature alone would have accepted this.
        var (parameters, _) = Server();

        var result = await ValidateAsync(
            Mint(PayerKey, audience: "https://api.some-other-system.example.com"), parameters);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public async Task SEC01_Replace_AnotherIssuersAudienceDoesNotAuthorizeThisIssuersToken()
    {
        // Per-issuer audiences: the other IdP's audience is configured and valid
        // — for the other IdP.
        var (parameters, _) = Server();

        var result = await ValidateAsync(
            Mint(PayerKey, audience: "https://api.other-payer.example.com"), parameters);

        result.IsValid.Should().BeFalse();
    }

    // ── Lifetime ──────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public async Task SEC01_Replace_AnExpiredTokenIsRejected()
    {
        var (parameters, _) = Server();

        var result = await ValidateAsync(Mint(PayerKey,
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1)), parameters);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public async Task SEC01_Replace_ANotYetValidTokenIsRejected()
    {
        var (parameters, _) = Server();

        var result = await ValidateAsync(Mint(PayerKey,
            notBefore: DateTime.UtcNow.AddHours(1),
            expires: DateTime.UtcNow.AddHours(2)), parameters);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public async Task SEC01_Replace_ATokenInsideTheClockSkewAllowanceIsAccepted()
    {
        // Clock drift between an IdP and CHO is real; the allowance is bounded
        // at five minutes so it cannot become lifetime validation switched off.
        var (parameters, _) = Server();

        var result = await ValidateAsync(Mint(PayerKey,
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddSeconds(-10)), parameters);

        result.IsValid.Should().BeTrue("a 10s expiry is inside the 30s default skew");
    }

    // ── Caller identity ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public async Task SEC01_Replace_AProviderNpiFromTheTrustedIssuerBindsTheCaller()
    {
        var (parameters, registry) = Server();
        var resolver = new CallerIdentityResolver(registry);

        var result = await ValidateAsync(Mint(PayerKey, claims:
        [
            new Claim("sub", "practitioner-77"),
            new Claim("npi", "1234567893"),
            new Claim("scope", "user/Claim.write"),
        ]), parameters);

        result.IsValid.Should().BeTrue();

        var caller = resolver.Resolve(new ClaimsPrincipal(result.ClaimsIdentity));
        caller!.HasVerifiedProviderIdentity.Should().BeTrue();
        caller.ProviderNpi.Should().Be("1234567893");
        caller.CallerType.Should().Be(SmartCallerType.User);
    }

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public async Task SEC01_Replace_AnNpiFromAnIssuerNotConfiguredToAssertOneIsNotIdentity()
    {
        // The same claim, from a trusted issuer that was never configured to
        // speak for provider identity. NPIs are public, so the claim alone is
        // not evidence — and CHO does not treat it as any.
        var (parameters, registry) = Server(payer => payer.Claims = new IssuerClaimMappingOptions());
        var resolver = new CallerIdentityResolver(registry);

        var result = await ValidateAsync(
            Mint(PayerKey, claims: new Claim("npi", "1234567893")), parameters);

        resolver.Resolve(new ClaimsPrincipal(result.ClaimsIdentity))!
            .HasVerifiedProviderIdentity.Should().BeFalse();
    }

    // ── Fail-closed production configuration ──────────────────────────────────

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public void SEC01_Replace_AProductionHostCannotFallBackToDemoTrust()
    {
        // The one failure mode nobody notices, because everything works — CHO
        // just trusts the bundled demo authorization server.
        var demo = new SmartTrustOptions
        {
            Mode = SmartTrustMode.Demo,
            Issuer = "https://auth.cloudhealthoffice.com",
            Audience = "fhir-api",
        };

        demo.Invoking(o => o.Validate(isDevelopmentHost: false))
            .Should().Throw<SmartTrustValidationException>();
    }

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public void SEC01_Replace_ProductionTrustMustBeExplicitlyConfigured()
    {
        // No trusted issuer means no token can validate. Starting up that way
        // serves 401s that read as an outage rather than a misconfiguration.
        new SmartTrustOptions { Mode = SmartTrustMode.ExternalIssuer }
            .Invoking(o => o.Validate(false))
            .Should().Throw<SmartTrustValidationException>()
            .WithMessage("*TrustedIssuers is empty*");
    }

    private static string Base64Url(string value)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
