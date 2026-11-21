using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using FhirService.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace CloudHealthOffice.FhirService.Tests.Security;

/// <summary>
/// The CDS Hooks 2.0 client JWT: an EHR CHO trusts, signing each call with a
/// key from its configured JWK Set, for the service URL it calls.
/// </summary>
public class CdsHooksClientAuthenticationTests
{
    private const string Issuer = "http://ehr.example/custom/crd_server_v221";
    private const string JwksUrl = "http://ehr.example/custom/crd_server_v221/jwks.json";
    private const string Service = "http://cho-fhir-service:8080/cds-services/cho-order-sign";

    private readonly ECDsaSecurityKey _key = new(ECDsa.Create(ECCurve.NamedCurves.nistP384)) { KeyId = "ehr-1" };

    [Fact]
    public async Task ASignedCallFromATrustedClient_Authenticates_InItsConfiguredTenant()
    {
        var result = await AuthenticateAsync(Token());

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        FhirCallerSchemes.IsCdsHooksClient(result.Principal).Should().BeTrue();
        FhirCallerSchemes.IsSmart(result.Principal).Should().BeFalse();
        FhirCallerSchemes.IsCho(result.Principal).Should().BeFalse();
        result.Principal!.FindFirst(ChoClaimTypes.TenantId)!.Value.Should().Be("interop");
    }

    [Fact]
    public async Task AReplayedToken_IsRefused()
    {
        var services = Services();
        var token = Token();

        (await AuthenticateAsync(token, services)).Succeeded.Should().BeTrue();
        (await AuthenticateAsync(token, services)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ATokenForAnotherService_IsRefused()
        => (await AuthenticateAsync(Token(aud: "http://cho-fhir-service:8080/cds-services/other"))).Succeeded.Should().BeFalse();

    [Fact]
    public async Task ATokenPointingAtAnotherKeySet_IsRefused()
        => (await AuthenticateAsync(Token(jku: "http://attacker.example/jwks.json"))).Succeeded.Should().BeFalse();

    [Fact]
    public async Task ATokenSignedByAnotherKey_IsRefused()
    {
        var other = new ECDsaSecurityKey(ECDsa.Create(ECCurve.NamedCurves.nistP384)) { KeyId = "ehr-1" };
        (await AuthenticateAsync(Token(signer: other))).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task AnExpiredToken_IsRefused()
        => (await AuthenticateAsync(Token(expires: DateTime.UtcNow.AddMinutes(-5)))).Succeeded.Should().BeFalse();

    [Fact]
    public void ATrustedClientsToken_IsRoutedToTheCdsHooksScheme_OnlyOnCdsServices()
    {
        var cds = new HashSet<string>(StringComparer.Ordinal) { Issuer };
        var none = new HashSet<string>(StringComparer.Ordinal);

        FhirCallerSchemes.SelectScheme(Request(Token(), "/cds-services/cho-order-sign"), none, cds)
            .Should().Be(FhirCallerSchemes.CdsHooks);
        FhirCallerSchemes.SelectScheme(Request(Token(), "/fhir/r4/Patient"), none, cds)
            .Should().Be(FhirCallerSchemes.Smart);
    }

    [Fact]
    public async Task TheCrdPolicy_AdmitsACdsHooksClient_AndAPlainPolicyDoesNot()
    {
        var principal = (await AuthenticateAsync(Token())).Principal!;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddFhirAccessPolicies();
        var authorization = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();

        async Task<bool> Allowed(FhirAccessAttribute attribute)
            => (await authorization.AuthorizeAsync(principal, attribute.Policy!)).Succeeded;

        (await Allowed(new FhirAccessAttribute(smart: true, cho: "authorizations:read", cdsHooks: true))).Should().BeTrue();
        (await Allowed(new FhirAccessAttribute(smart: true, cho: "authorizations:read"))).Should().BeFalse();
    }

    [Fact]
    public void AClientWithoutATenant_IsRefusedAtStartup()
    {
        var options = new CdsHooksOptions { TrustedClients = [new() { Issuer = Issuer, JwksUrl = JwksUrl }] };
        options.Invoking(o => o.Validate(isDevelopmentHost: true)).Should().Throw<SmartTrustValidationException>();
    }

    [Fact]
    public void AnHttpKeySet_IsRefusedOutsideDevelopment()
    {
        var options = new CdsHooksOptions { TrustedClients = [Client()] };
        options.Invoking(o => o.Validate(isDevelopmentHost: false)).Should().Throw<SmartTrustValidationException>();
        options.Invoking(o => o.Validate(isDevelopmentHost: true)).Should().NotThrow();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CdsHooksTrustedClient Client() => new() { Issuer = Issuer, JwksUrl = JwksUrl, TenantId = "interop" };

    private string Token(string aud = Service, string? jku = JwksUrl, SecurityKey? signer = null, DateTime? expires = null)
    {
        var now = DateTime.UtcNow;
        var exp = expires ?? now.AddMinutes(5);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = aud,
            IssuedAt = exp.AddMinutes(-5),
            NotBefore = exp.AddMinutes(-5),
            Expires = exp,
            Claims = new Dictionary<string, object> { ["jti"] = Guid.NewGuid().ToString() },
            SigningCredentials = new SigningCredentials(signer ?? _key, SecurityAlgorithms.EcdsaSha384),
            AdditionalHeaderClaims = jku is null ? null : new Dictionary<string, object> { ["jku"] = jku },
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private ServiceProvider Services()
    {
        var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(_key);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = jwk.Kty, crv = jwk.Crv, x = jwk.X, y = jwk.Y, kid = jwk.Kid, alg = "ES384", use = "sig" } },
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IHttpClientFactory>(new StubFactory(new StubHandler(jwks)));
        services.Configure<CdsHooksOptions>(o => o.TrustedClients = [Client()]);
        services.AddSingleton<CdsHooksClientKeys>();
        services.AddSingleton<CdsHooksReplayCache>();
        services.AddAuthentication()
            .AddScheme<CdsHooksClientSchemeOptions, CdsHooksClientHandler>(FhirCallerSchemes.CdsHooks, _ => { });
        return services.BuildServiceProvider();
    }

    private async Task<AuthenticateResult> AuthenticateAsync(string token, ServiceProvider? services = null)
    {
        var context = Request(token, "/cds-services/cho-order-sign");
        // One scope per request, as ASP.NET Core does: handlers are cached per scope.
        context.RequestServices = (services ?? Services()).CreateScope().ServiceProvider;
        return await context.AuthenticateAsync(FhirCallerSchemes.CdsHooks);
    }

    private static HttpContext Request(string bearer, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("cho-fhir-service:8080");
        context.Request.Path = path;
        context.Request.Headers.Authorization = "Bearer " + bearer;
        return context;
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.RequestUri!.ToString() == JwksUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
