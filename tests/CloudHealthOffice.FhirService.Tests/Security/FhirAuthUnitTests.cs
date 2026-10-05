using System.Net;
using System.Security.Claims;
using System.Text;
using CloudHealthOffice.Consent.Contracts;
using CloudHealthOffice.Infrastructure.Security;
using FhirService.Services.Consent;
using FhirService.Services.Identity;
using FhirService.Services.PayerToPayer;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.FhirService.Tests.Security;

/// <summary>
/// Unit-level pins for the CHO/SMART boundary: scheme routing, startup refusal
/// of an issuer trusted by both schemes, SMART tenant resolution, and the
/// consent registry failing closed on a refusal.
/// </summary>
public class FhirAuthUnitTests
{
    // ── Consent registry: a refusal is a denial ───────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ConsentRegistryRefusal_DeniesTheExchange(HttpStatusCode status)
    {
        // The registry body would grant consent if it were read; a refusal must
        // never be read as "consent present".
        var handler = new StubHandler(status, ActiveConsentBody("pat-001"));
        var source = new HttpConsentRegistryConsentSource(new StubFactory(handler),
            NullLogger<HttpConsentRegistryConsentSource>.Instance);
        var gate = new ConsentRegistryPayerToPayerConsentGate(
            new RegistryConsentEvaluator(source, NullLogger<RegistryConsentEvaluator>.Instance));

        var decision = await gate.EvaluateAsync("tenant-a", "pat-001");

        decision.Allowed.Should().BeFalse();
        handler.TenantHeader.Should().Be("tenant-a", "the call names the tenant being decided for");
    }

    [Fact]
    public async Task ConsentRegistryGrant_IsHonoured_SoTheDenialAboveIsNotVacuous()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ActiveConsentBody("pat-001"));
        var source = new HttpConsentRegistryConsentSource(new StubFactory(handler),
            NullLogger<HttpConsentRegistryConsentSource>.Instance);
        var gate = new ConsentRegistryPayerToPayerConsentGate(
            new RegistryConsentEvaluator(source, NullLogger<RegistryConsentEvaluator>.Instance));

        (await gate.EvaluateAsync("tenant-a", "pat-001")).Allowed.Should().BeTrue();
    }

    private static string ActiveConsentBody(string memberId) =>
        "{\"items\":[{\"tenantId\":\"tenant-a\",\"memberId\":\"" + memberId + "\",\"consentId\":\"c1\","
        + "\"purposeOfUse\":\"PayerToPayerExchange\",\"status\":\"Active\","
        + "\"effectiveAt\":\"2020-01-01T00:00:00Z\"}]}";

    // ── Scheme routing ────────────────────────────────────────────────────────

    [Fact]
    public void ACHOIssuedToken_IsRoutedToTheChoScheme_EverythingElseToSmart()
    {
        var cho = new HashSet<string>(StringComparer.Ordinal) { ChoDevelopmentAuth.UserIssuer };

        FhirCallerSchemes.SelectScheme(Request(ChoDevelopmentAuth.UserToken("t", "MemberServices")), cho)
            .Should().Be(FhirCallerSchemes.Cho);
        FhirCallerSchemes.SelectScheme(Request(null), cho).Should().Be(FhirCallerSchemes.Smart);
        FhirCallerSchemes.SelectScheme(Request("not-a-jwt"), cho).Should().Be(FhirCallerSchemes.Smart);
    }

    [Fact]
    public void AnIssuerTrustedByBothSchemes_FailsStartup()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ChoAuth:Issuers:0:Issuer"] = "https://shared.example.com",
            ["ChoAuth:Issuers:0:SymmetricKey"] = ChoDevelopmentAuth.SymmetricKey,
            ["SmartAuth:Mode"] = "Demo",
            ["SmartAuth:Issuer"] = "https://shared.example.com",
        }).Build();

        var act = () => new ServiceCollection().AddFhirCallerSchemes(config);

        act.Should().Throw<SmartTrustValidationException>().WithMessage("*https://shared.example.com*");
    }

    [Fact]
    public void MarkerIdentities_DecideTheCallerKind_NotClaims()
    {
        var smartLooking = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("scope", "patient/*.read"), new Claim("roles", "TenantAdmin")], "Bearer"));
        FhirCallerSchemes.IsCho(smartLooking).Should().BeFalse();
        FhirCallerSchemes.IsSmart(smartLooking).Should().BeFalse();

        FhirCallerSchemes.Mark(smartLooking, FhirCallerSchemes.SmartMarker);
        FhirCallerSchemes.IsSmart(smartLooking).Should().BeTrue();
        FhirCallerSchemes.IsCho(smartLooking).Should().BeFalse();

        // Both markers is no kind at all.
        FhirCallerSchemes.Mark(smartLooking, FhirCallerSchemes.ChoMarker);
        FhirCallerSchemes.IsSmart(smartLooking).Should().BeFalse();
        FhirCallerSchemes.IsCho(smartLooking).Should().BeFalse();
    }

    // ── SMART tenant ──────────────────────────────────────────────────────────

    private static TrustedIssuerRegistry Registry(params string[] tenants) => new(new SmartTrustOptions
    {
        Mode = SmartTrustMode.ExternalIssuer,
        TrustedIssuers =
        [
            new TrustedIssuerOptions
            {
                Issuer = "https://idp.example.com",
                Audiences = ["fhir-api"],
                Tenants = [.. tenants],
                Claims = new IssuerClaimMappingOptions { TenantClaim = "org" },
            },
        ],
    });

    private static AuthenticatedCaller Caller(string? mappedTenant) => new()
    {
        Issuer = "https://idp.example.com",
        CallerType = SmartCallerType.Patient,
        Scopes = new HashSet<string>(),
        TenantClaim = mappedTenant,
    };

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "Smart"));

    [Fact]
    public void SmartTenant_MappedClaimWins_AndIsAddedForTheSharedMiddleware()
    {
        var result = SmartTenant.Resolve(Principal(), Caller("tenant-a"), Registry());
        result.Tenant.Should().Be("tenant-a");
        result.ClaimToAdd.Should().Be("tenant-a");
    }

    [Fact]
    public void SmartTenant_TwoDifferentTenantsInOneToken_IsAConflict()
        => SmartTenant.Resolve(Principal(new Claim("tenant_id", "tenant-b")), Caller("tenant-a"), Registry())
            .Conflict.Should().BeTrue();

    [Fact]
    public void SmartTenant_IssuerConfinedToOneTenant_SuppliesIt()
        => SmartTenant.Resolve(Principal(), Caller(null), Registry("tenant-only")).Tenant
            .Should().Be("tenant-only");

    [Fact]
    public void SmartTenant_NoTenantAnywhere_IsNone_NeverAHeader()
        => SmartTenant.Resolve(Principal(), Caller(null), Registry("tenant-a", "tenant-b")).Tenant
            .Should().BeNull();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpContext Request(string? bearer)
    {
        var context = new DefaultHttpContext();
        if (bearer != null) context.Request.Headers.Authorization = "Bearer " + bearer;
        return context;
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? TenantHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            TenantHeader = request.Headers.TryGetValues("X-Tenant-ID", out var v) ? v.FirstOrDefault() : null;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://consent-service.cloudhealthoffice/") };
    }
}
