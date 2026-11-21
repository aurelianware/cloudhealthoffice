using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using FhirService.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecurityClaim = System.Security.Claims.Claim;

namespace CloudHealthOffice.FhirService.Tests.Security;

/// <summary>
/// Two trusted issuers: the test issuer, with no scope allowlist (as every
/// issuer was before G-39), and a provider-app IdP confined to patient and
/// user reads.
/// </summary>
public class IssuerScopeAllowlistWebAppFactory : FhirTestWebAppFactory
{
    public const string ConfinedIssuer = "https://provider-idp.test.local";

    protected override IReadOnlyList<string> AdditionalValidIssuers => [ConfinedIssuer];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Replaced in DI, as PayerToPayerWebAppFactory does, because Program
        // reads SmartAuth before a test host's configuration is applied.
        builder.ConfigureTestServices(services =>
        {
            var trust = new SmartTrustOptions
            {
                Mode = SmartTrustMode.Demo,
                TrustedIssuers =
                [
                    new TrustedIssuerOptions
                    {
                        Issuer = TestIssuer, Audiences = ["fhir-api"], RequireHttpsMetadata = false,
                        IssuesChoClientRegistrations = true,
                    },
                    new TrustedIssuerOptions
                    {
                        Issuer = ConfinedIssuer, Audiences = ["fhir-api"], RequireHttpsMetadata = false,
                        AllowedScopes = ["patient/*.read", "user/*.read"],
                    },
                ],
            };
            trust.Validate(isDevelopmentHost: true);
            services.RemoveAll<SmartTrustOptions>();
            services.RemoveAll<TrustedIssuerRegistry>();
            services.AddSingleton(trust);
            services.AddSingleton(new TrustedIssuerRegistry(trust));
        });
    }
}

/// <summary>
/// G-39: per-issuer scope allowlists, through the real SMART scheme and
/// SmartScopeEnforcementMiddleware. A scope an issuer may not grant is removed
/// at authentication, so it is answered like any other missing scope (403),
/// and an issuer without a list is unchanged.
/// </summary>
public class IssuerScopeAllowlistTests : IClassFixture<IssuerScopeAllowlistWebAppFactory>
{
    private const string Confined = IssuerScopeAllowlistWebAppFactory.ConfinedIssuer;

    private readonly IssuerScopeAllowlistWebAppFactory _factory;

    public IssuerScopeAllowlistTests(IssuerScopeAllowlistWebAppFactory factory) => _factory = factory;

    private async Task<HttpResponseMessage> ExportAsync(string token)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/fhir/r4/$export");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Prefer", "respond-async");
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task ConfinedIssuer_CannotGrantASystemScope()
        => (await ExportAsync(_factory.IssueToken("system/*.read", issuer: Confined)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task ConfinedIssuer_CannotGrantASystemScopeInScpEither()
        => (await ExportAsync(_factory.IssueToken(null, issuer: Confined,
                extraClaims: [new SecurityClaim("scp", "system/*.read")])))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task ConfinedIssuer_StillGrantsTheScopesItMay()
        => (await GetAsync("/fhir/r4/Patient/pat-001",
                _factory.IssueToken("patient/*.read system/*.read", patientId: "pat-001", issuer: Confined)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task IssuerWithoutAList_IsUnchanged()
        // Bulk export is off in the test host, so a system grant reaches the
        // controller and is answered 501, exactly as before G-39.
        => (await ExportAsync(_factory.IssueToken("system/*.read")))
            .StatusCode.Should().Be(HttpStatusCode.NotImplemented);
}

/// <summary>Unit rules of <see cref="IssuerScopePolicy"/> and its configuration.</summary>
public class IssuerScopePolicyTests
{
    private static TrustedIssuerOptions Issuer(params string[] allowed) => new()
    {
        Issuer = "https://idp.test.local", Audiences = ["fhir-api"], AllowedScopes = [.. allowed],
    };

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new SecurityClaim(c.Type, c.Value)), "test"));

    [Fact]
    public void NoList_LeavesEveryScope()
    {
        var principal = Principal(("scope", "system/*.read patient/*.read"));

        IssuerScopePolicy.Narrow(principal, Issuer()).Should().Be(0);
        SmartScopeClaims.Parse(principal).Should().BeEquivalentTo("system/*.read", "patient/*.read");
    }

    [Fact]
    public void List_RemovesScopesNotOnIt_FromBothClaims()
    {
        var principal = Principal(("scope", "openid system/*.read user/*.read"), ("scp", "user/*.read system/*.read openid"));

        IssuerScopePolicy.Narrow(principal, Issuer("user/*.read", "openid")).Should().Be(2);

        var result = SmartScopeClaims.Read(principal);
        result.Conflict.Should().BeFalse();
        result.Scopes.Should().BeEquivalentTo("openid", "user/*.read");
    }

    [Fact]
    public void List_IsLiteral_NotAPattern()
    {
        var principal = Principal(("scope", "system/Patient.read system/*.read"));

        IssuerScopePolicy.Narrow(principal, Issuer("system/*.read"));

        SmartScopeClaims.Parse(principal).Should().BeEquivalentTo("system/*.read");
    }

    [Fact]
    public void List_IsCaseSensitive()
    {
        var principal = Principal(("scope", "SYSTEM/*.READ"));

        IssuerScopePolicy.Narrow(principal, Issuer("system/*.read"));

        SmartScopeClaims.Parse(principal).Should().BeEmpty();
    }

    [Fact]
    public void AClaimLeftEmpty_StaysPresent_SoD32StillSeesBothClaims()
    {
        // scope narrows to nothing, scp keeps a scope: still a conflict, as
        // the two claims never agreed in the first place.
        var principal = Principal(("scope", "system/*.read"), ("scp", "user/*.read"));

        IssuerScopePolicy.Narrow(principal, Issuer("user/*.read"));

        principal.FindAll("scope").Should().ContainSingle().Which.Value.Should().BeEmpty();
        SmartScopeClaims.Read(principal).Conflict.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("user/*.read patient/*.read")]
    public void Validate_RefusesABlankOrMultiScopeEntry(string entry)
    {
        var issuer = Issuer(entry);
        issuer.RequireHttpsMetadata = false;

        var act = () => issuer.Validate(isDevelopmentHost: true);

        act.Should().Throw<SmartTrustValidationException>().WithMessage("*AllowedScopes*");
    }
}
