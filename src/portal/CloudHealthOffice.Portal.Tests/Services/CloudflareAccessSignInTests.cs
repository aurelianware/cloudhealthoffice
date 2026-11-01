using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Portal.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static CloudHealthOffice.Portal.Tests.Services.ChoTokenTestSupport;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// Authentication:Mode=CloudflareAccess: the person Cloudflare Access signed in
/// is the portal user, with no second sign-in, and the token service turns their
/// Access token into a CHO token.
/// </summary>
public class CloudflareAccessSignInTests
{
    private const string TeamDomain = "cho-test.cloudflareaccess.com";
    private const string Aud = "aud-tag-portal";
    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "access-1" };

    private static string Jwks()
    {
        var p = Key.Rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[] { new { kid = Key.KeyId, kty = "RSA", alg = "RS256", use = "sig", e = Base64UrlEncoder.Encode(p.Exponent), n = Base64UrlEncoder.Encode(p.Modulus) } },
        });
    }

    private static string AccessToken(string email = "pat@acme.example", string aud = Aud, SecurityKey? key = null, DateTime? expires = null)
    {
        var exp = expires ?? DateTime.UtcNow.AddHours(1);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://" + TeamDomain,
            Audience = aud,
            IssuedAt = exp.AddHours(-2),
            NotBefore = exp.AddHours(-2),
            Expires = exp,
            Claims = new Dictionary<string, object> { ["sub"] = "access-user-1", ["email"] = email, ["type"] = "app" },
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.RsaSha256),
        });
    }

    private static readonly CloudflareAccessOptions AccessOptions = new() { TeamDomain = TeamDomain, Audiences = { Aud } };

    private static CloudflareAccessTokenValidator Validator() => new(AccessOptions, new StaticCloudflareAccessKeys(Jwks()));

    private static Task<AuthenticateResult> Authenticate(string? header)
        => Authenticate(header, CloudflareAccessOptions.AssertionHeader, AccessOptions, Validator());

    private static async Task<AuthenticateResult> Authenticate(
        string? header, string headerName, CloudflareAccessOptions options, ICloudflareAccessTokenValidator validator)
    {
        var handler = new CloudflareAccessAuthenticationHandler(
            new StaticOptionsMonitor(), NullLoggerFactory.Instance, UrlEncoder.Default, validator, options);
        var context = new DefaultHttpContext();
        if (header != null)
            context.Request.Headers[headerName] = header;
        await handler.InitializeAsync(
            new AuthenticationScheme(CloudflareAccessAuthenticationHandler.SchemeName, null, typeof(CloudflareAccessAuthenticationHandler)),
            context);
        return await handler.AuthenticateAsync();
    }

    // ── The portal's own check of the Access header ──

    [Fact]
    public async Task A_valid_access_header_signs_the_person_in()
    {
        var token = AccessToken();
        var result = await Authenticate(token);

        result.Succeeded.Should().BeTrue();
        var user = result.Principal!;
        user.Identity!.IsAuthenticated.Should().BeTrue();
        user.FindFirst(ClaimTypes.Email)!.Value.Should().Be("pat@acme.example");
        user.HasClaim(CloudflareAccessAuthenticationHandler.MarkerClaim, "true").Should().BeTrue();
        user.FindFirst(CloudflareAccessAuthenticationHandler.AssertionClaim)!.Value.Should().Be(token);
        ChoTokenProvider.UserCacheKey(user).Should().Be("cloudflare-access|access-user-1");
    }

    [Fact]
    public async Task No_header_means_not_signed_in()
        => (await Authenticate(null)).None.Should().BeTrue();

    [Theory]
    [InlineData("other_application")]
    [InlineData("other_key")]
    [InlineData("expired")]
    [InlineData("garbage")]
    public async Task A_header_that_does_not_validate_is_refused(string variant)
    {
        var header = variant switch
        {
            "other_application" => AccessToken(aud: "some-other-app"),
            "other_key" => AccessToken(key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = Key.KeyId }),
            "expired" => AccessToken(expires: DateTime.UtcNow.AddMinutes(-10)),
            _ => "not-a-jwt",
        };

        var result = await Authenticate(header);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
    }

    // ── Behind Google Cloud IAP instead of Cloudflare ──

    private const string IapAudience = "/projects/123456789012/global/backendServices/987654321";
    private static readonly ECDsaSecurityKey IapKey = new(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = "iap-1" };

    private static string IapJwks()
    {
        var p = IapKey.ECDsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[] { new { kid = IapKey.KeyId, kty = "EC", alg = "ES256", use = "sig", crv = "P-256", x = Base64UrlEncoder.Encode(p.Q.X), y = Base64UrlEncoder.Encode(p.Q.Y) } },
        });
    }

    private static string IapToken() => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = CloudflareAccessOptions.GoogleIapIssuer,
        Audience = IapAudience,
        IssuedAt = DateTime.UtcNow.AddMinutes(-1),
        Expires = DateTime.UtcNow.AddMinutes(9),
        Claims = new Dictionary<string, object> { ["sub"] = "accounts.google.com:1234567890", ["email"] = "pat@acme.example", ["hd"] = "acme.example" },
        SigningCredentials = new SigningCredentials(IapKey, SecurityAlgorithms.EcdsaSha256),
    });

    private static readonly CloudflareAccessOptions IapOptions = new() { Provider = EdgeAccessProvider.GoogleIap, Audiences = { IapAudience } };

    [Fact]
    public async Task Behind_google_iap_the_iap_header_signs_the_person_in()
    {
        var validator = new CloudflareAccessTokenValidator(IapOptions, new StaticCloudflareAccessKeys(IapJwks()));
        var token = IapToken();

        var result = await Authenticate(token, CloudflareAccessOptions.GoogleIapAssertionHeader, IapOptions, validator);

        result.Succeeded.Should().BeTrue();
        result.Principal!.FindFirst(ClaimTypes.Email)!.Value.Should().Be("pat@acme.example");
        result.Principal.FindFirst(CloudflareAccessAuthenticationHandler.AssertionClaim)!.Value.Should().Be(token);
    }

    [Fact]
    public async Task Behind_google_iap_a_cloudflare_header_is_ignored()
    {
        var validator = new CloudflareAccessTokenValidator(IapOptions, new StaticCloudflareAccessKeys(IapJwks()));

        (await Authenticate(IapToken(), CloudflareAccessOptions.AssertionHeader, IapOptions, validator)).None.Should().BeTrue();
    }

    [Fact]
    public void Behind_google_iap_sign_out_clears_the_iap_cookie()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PortalAuthMode.ConfigurationKey] = PortalAuthMode.CloudflareAccess,
            ["Authentication:CloudflareAccess:Provider"] = "GoogleIap",
        }).Build();

        PortalAuthMode.SignOutPath(configuration).Should().Be("/?gcp-iap-mode=CLEAR_LOGIN_COOKIE");
        PortalAuthMode.SignInPath(configuration).Should().Be(PortalAuthMode.CloudflareAccessSignInPath);
    }

    // ── Token exchange ──

    private readonly List<(string Path, string? Bearer, string? Tenant)> _calls = new();

    private ChoTokenProvider Provider(ClaimsPrincipal user, IHttpContextAccessor? accessor = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:TokenService"] = TokenServiceUrl,
                ["Authentication:Mode"] = PortalAuthMode.CloudflareAccess,
            })
            .Build();
        var tokenService = new FakeHandler(request =>
        {
            _calls.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.Parameter,
                request.Method == HttpMethod.Post ? RequestedTenant(request) : null));
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/token/exchange/cloudflare-access" => Json(HttpStatusCode.OK, ExchangeJson(tenantId: "demo", tenantName: "Demo Health Plan")),
                "/v1/token/tenants/cloudflare-access" => Json(HttpStatusCode.OK, """[{"tenantId":"demo","tenantName":"Demo Health Plan","azureTenantId":"demo"}]"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        return new ChoTokenProvider(
            new StaticAuthenticationStateProvider(user),
            new SingleHandlerHttpClientFactory(tokenService),
            new MemoryCache(new MemoryCacheOptions()),
            configuration,
            new TestHostEnvironment("Production"),
            NullLogger<ChoTokenProvider>.Instance,
            tokenAcquisition: null,
            httpContextAccessor: accessor);
    }

    private static async Task<ClaimsPrincipal> AccessUser(string? token = null)
        => (await Authenticate(token ?? AccessToken())).Principal!;

    [Fact]
    public async Task The_access_token_is_exchanged_at_the_access_endpoint_with_no_entra_involved()
    {
        var token = AccessToken();
        var sut = Provider(await AccessUser(token));

        var result = await sut.GetTokenAsync();

        result.Succeeded.Should().BeTrue();
        result.Token!.TenantId.Should().Be("demo");
        _calls.Should().ContainSingle().Which.Should().Be(("/v1/token/exchange/cloudflare-access", token, (string?)null));
    }

    [Fact]
    public async Task The_tenant_list_comes_from_the_access_endpoint()
    {
        var sut = Provider(await AccessUser());

        var tenants = await sut.GetTenantsAsync();

        tenants!.Select(t => t.TenantId).Should().Equal("demo");
        _calls.Single().Path.Should().Be("/v1/token/tenants/cloudflare-access");
    }

    [Fact]
    public async Task A_fresh_header_on_the_current_request_is_preferred_to_the_one_the_circuit_started_with()
    {
        var circuitToken = AccessToken();
        var freshToken = AccessToken(expires: DateTime.UtcNow.AddHours(2));
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = await AccessUser(freshToken) } };
        var sut = Provider(await AccessUser(circuitToken), accessor);

        await sut.GetTokenAsync();

        _calls.Single().Bearer.Should().Be(freshToken);
    }

    [Fact]
    public async Task A_request_header_for_a_different_person_is_never_used_for_the_circuit_user()
    {
        var circuitToken = AccessToken();
        var otherPerson = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://" + TeamDomain,
            Audience = Aud,
            Expires = DateTime.UtcNow.AddHours(1),
            Claims = new Dictionary<string, object> { ["sub"] = "access-user-2", ["email"] = "other@acme.example" },
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
        });
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = await AccessUser(otherPerson) } };
        var sut = Provider(await AccessUser(circuitToken), accessor);

        await sut.GetTokenAsync();

        _calls.Single().Bearer.Should().Be(circuitToken);
    }

    [Fact]
    public async Task Invitations_and_self_service_signup_are_not_offered_to_access_users()
    {
        var sut = Provider(await AccessUser());

        (await sut.RedeemInvitationAsync("code-1")).Status.Should().Be(ChoInvitationStatus.Unavailable);
        (await sut.SignupAsync(new ChoSignupRequest { OrganizationName = "x", Tier = "starter" })).Status
            .Should().Be(ChoSignupStatus.NotAuthenticated);
        _calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_access_principal_is_ignored_when_the_portal_is_not_in_access_mode()
    {
        // Entra mode: a principal carrying the marker gets no special treatment
        // (no token acquisition is configured here, so no token is obtained).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Services:TokenService"] = TokenServiceUrl, ["TokenService:Scope"] = Scope })
            .Build();
        var calls = 0;
        var sut = new ChoTokenProvider(
            new StaticAuthenticationStateProvider(await AccessUser()),
            new SingleHandlerHttpClientFactory(new FakeHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); })),
            new MemoryCache(new MemoryCacheOptions()),
            configuration,
            new TestHostEnvironment("Production"),
            NullLogger<ChoTokenProvider>.Instance);

        (await sut.GetTokenAsync()).Succeeded.Should().BeFalse();
        calls.Should().Be(0);
    }

    // ── Sign-in and sign-out addresses ──

    [Fact]
    public void Access_mode_signs_out_of_cloudflare_access()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:Mode"] = "CloudflareAccess" })
            .Build();

        PortalAuthMode.SignInPath(configuration).Should().Be("/access/sign-in");
        PortalAuthMode.SignOutPath(configuration).Should().Be("/cdn-cgi/access/logout");
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("/claims/123?tab=lines", "/claims/123?tab=lines")]
    [InlineData("https://demo.example.com/members", "/members")]
    [InlineData("https://evil.example.net/members", "/")]
    [InlineData("//evil.example.net", "/")]
    [InlineData("/\\evil.example.net", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/access/sign-in?redirectUri=/x", "/")]
    [InlineData("/\t/evil.example.net", "/")]
    [InlineData("/\n/evil.example.net", "/")]
    [InlineData("/a\\b", "/")]
    [InlineData("https://demo.example.com/%09/evil", "/%09/evil")]
    public void Sign_in_returns_only_to_this_portal(string? redirect, string expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Host = new HostString("demo.example.com");

        CloudflareAccessPortalRegistration.LocalRedirect(redirect, request).Should().Be(expected);
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
