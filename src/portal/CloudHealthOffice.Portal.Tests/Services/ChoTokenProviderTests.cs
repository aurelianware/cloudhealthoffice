using System.Net;
using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static CloudHealthOffice.Portal.Tests.Services.ChoTokenTestSupport;

namespace CloudHealthOffice.Portal.Tests.Services;

public class ChoTokenProviderTests
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IDistributedCache _preferences =
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly Mock<ITokenAcquisition> _tokenAcquisition = new();
    private readonly Mock<IChoReauthenticationHandler> _reauthentication = new();
    private readonly List<ExchangeCall> _exchanges = new();
    private readonly List<HttpRequestMessage> _tenantListCalls = new();
    private Func<HttpRequestMessage, HttpResponseMessage> _exchangeResponse =
        _ => Json(HttpStatusCode.OK, ExchangeJson());
    private Func<HttpRequestMessage, HttpResponseMessage> _tenantsResponse =
        _ => Json(HttpStatusCode.OK, """[{"tenantId":"tenant-1","tenantName":"Acme Health","azureTenantId":"entra-tid-1"},{"tenantId":"tenant-2","tenantName":"Beta Plan","azureTenantId":"entra-tid-2"}]""");

    public ChoTokenProviderTests()
    {
        _tokenAcquisition
            .Setup(t => t.GetAccessTokenForUserAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<ClaimsPrincipal?>(), It.IsAny<TokenAcquisitionOptions?>()))
            .ReturnsAsync((IEnumerable<string> _, string? _, string? _, ClaimsPrincipal? user, TokenAcquisitionOptions? _)
                => "entra-token-for-" + user?.FindFirst("oid")?.Value);
    }

    private FakeHandler TokenService() => new(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/v1/token/exchange")
        {
            _exchanges.Add(new ExchangeCall(request.Method, request.RequestUri!,
                request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter,
                RequestedTenant(request)));
            return _exchangeResponse(request);
        }
        if (path == "/v1/token/tenants")
        {
            _tenantListCalls.Add(request);
            return _tenantsResponse(request);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    private ChoTokenProvider CreateProvider(
        ClaimsPrincipal? user = null,
        string environment = "Production",
        string authMode = "Entra",
        HttpMessageHandler? tokenService = null,
        bool withTokenAcquisition = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:TokenService"] = TokenServiceUrl,
                ["TokenService:Scope"] = Scope,
                ["Authentication:Mode"] = authMode,
                ["Authentication:LocalDemo:TenantId"] = "demo",
                ["Authentication:LocalDemo:TenantName"] = "Local Demo Tenant",
            })
            .Build();

        return new ChoTokenProvider(
            new StaticAuthenticationStateProvider(user ?? EntraUser()),
            new SingleHandlerHttpClientFactory(tokenService ?? TokenService()),
            _cache,
            configuration,
            new TestHostEnvironment(environment),
            NullLogger<ChoTokenProvider>.Instance,
            withTokenAcquisition ? _tokenAcquisition.Object : null,
            _reauthentication.Object,
            _preferences);
    }

    // ── Exchange ──

    [Fact]
    public async Task GetTokenAsync_ExchangesTheUsersEntraTokenForTheConfiguredScope()
    {
        var user = EntraUser();
        var sut = CreateProvider(user);

        var result = await sut.GetTokenAsync();

        result.Succeeded.Should().BeTrue();
        result.Token!.AccessToken.Should().Be("cho-token-1");
        result.Token.TenantId.Should().Be("tenant-1");
        sut.CurrentTenantId.Should().Be("tenant-1");

        var exchange = _exchanges.Should().ContainSingle().Subject;
        exchange.Method.Should().Be(HttpMethod.Post);
        exchange.RequestUri!.AbsoluteUri.Should().Be($"{TokenServiceUrl}/v1/token/exchange");
        exchange.Scheme.Should().Be("Bearer");
        exchange.Bearer.Should().Be("entra-token-for-oid-1");
        exchange.Tenant.Should().BeNull("no tenant chosen yet means the home tenant");

        // Blazor Server: the circuit's user is passed to token acquisition explicitly.
        _tokenAcquisition.Verify(t => t.GetAccessTokenForUserAsync(
            It.Is<IEnumerable<string>>(s => s.SequenceEqual(new[] { Scope })),
            null, null, user, null), Times.Once);
    }

    [Fact]
    public async Task GetTokenAsync_WhenNotSignedIn_DoesNotCallTheTokenService()
    {
        var sut = CreateProvider(Anonymous());

        var result = await sut.GetTokenAsync();

        result.Status.Should().Be(ChoTokenStatus.NotAuthenticated);
        _exchanges.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"invalid_token"}""", ChoTokenStatus.InvalidToken)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":"no_access"}""", ChoTokenStatus.NoAccess)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":"unavailable"}""", ChoTokenStatus.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, "oops", ChoTokenStatus.Unavailable)]
    public async Task GetTokenAsync_MapsExchangeErrors(HttpStatusCode status, string body, ChoTokenStatus expected)
    {
        _exchangeResponse = _ => Json(status, body);
        var sut = CreateProvider();

        var result = await sut.GetTokenAsync();

        result.Succeeded.Should().BeFalse();
        result.Status.Should().Be(expected);
        result.Token.Should().BeNull();
    }

    [Fact]
    public async Task GetTokenAsync_WhenTokenServiceUnreachable_IsUnavailable()
    {
        var sut = CreateProvider(tokenService: new FakeHandler(_ => throw new HttpRequestException("connection refused")));

        var result = await sut.GetTokenAsync();

        result.Status.Should().Be(ChoTokenStatus.Unavailable);
    }

    [Fact]
    public async Task GetTokenAsync_WhenTokenIsForAnotherTenantThanRequested_DiscardsIt()
    {
        _exchangeResponse = request => RequestedTenant(request) == null
            ? Json(HttpStatusCode.OK, ExchangeJson(tenantId: "tenant-1"))
            : Json(HttpStatusCode.OK, ExchangeJson(tenantId: "tenant-other"));
        var sut = CreateProvider();

        var result = await sut.SwitchTenantAsync("tenant-2");

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task GetTokenAsync_WhenTenantRequired_UsesTheFirstOfferedTenant()
    {
        _exchangeResponse = request => RequestedTenant(request) switch
        {
            null => Json(HttpStatusCode.Conflict,
                """{"error":"tenant_required","tenants":[{"tenantId":"tenant-2","tenantName":"Beta","azureTenantId":"x"},{"tenantId":"tenant-3","tenantName":"Gamma","azureTenantId":"y"}]}"""),
            var t => Json(HttpStatusCode.OK, ExchangeJson(tenantId: t!, accessToken: "token-" + t)),
        };
        var sut = CreateProvider();

        var result = await sut.GetTokenAsync();

        result.Succeeded.Should().BeTrue();
        result.Token!.TenantId.Should().Be("tenant-2");
        _exchanges.Select(e => e.Tenant).Should().Equal(null, "tenant-2");
    }

    [Fact]
    public async Task GetTokenAsync_WhenTenantRequiredWithNoTenants_HasNoToken()
    {
        _exchangeResponse = _ => Json(HttpStatusCode.Conflict, """{"error":"tenant_required","tenants":[]}""");
        var sut = CreateProvider();

        var result = await sut.GetTokenAsync();

        result.Status.Should().Be(ChoTokenStatus.TenantRequired);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task GetTokenAsync_WhenTokenServiceNotConfigured_IsUnavailable()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TokenService:Scope"] = Scope,
        }).Build();
        var sut = new ChoTokenProvider(
            new StaticAuthenticationStateProvider(EntraUser()),
            new SingleHandlerHttpClientFactory(TokenService()),
            _cache, configuration, new TestHostEnvironment("Production"),
            NullLogger<ChoTokenProvider>.Instance, _tokenAcquisition.Object);

        (await sut.GetTokenAsync()).Status.Should().Be(ChoTokenStatus.Unavailable);
        _exchanges.Should().BeEmpty();
    }

    // ── Consent / re-sign-in ──

    [Fact]
    public async Task GetTokenAsync_WhenEntraNeedsConsent_ChallengesTheUserAndHasNoToken()
    {
        var challenge = new MicrosoftIdentityWebChallengeUserException(
            new MsalUiRequiredException("invalid_grant", "AADSTS65001: consent required"), new[] { Scope });
        _tokenAcquisition
            .Setup(t => t.GetAccessTokenForUserAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<ClaimsPrincipal?>(), It.IsAny<TokenAcquisitionOptions?>()))
            .ThrowsAsync(challenge);
        var user = EntraUser();
        var sut = CreateProvider(user);

        var result = await sut.GetTokenAsync();

        result.Status.Should().Be(ChoTokenStatus.ConsentRequired);
        _reauthentication.Verify(r => r.Challenge(challenge, user), Times.Once);
        _exchanges.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTokenAsync_WhenMsalNeedsInteraction_ChallengesTheUser()
    {
        _tokenAcquisition
            .Setup(t => t.GetAccessTokenForUserAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<ClaimsPrincipal?>(), It.IsAny<TokenAcquisitionOptions?>()))
            .ThrowsAsync(new MsalUiRequiredException("user_null", "No account in the cache"));
        var sut = CreateProvider();

        var result = await sut.GetTokenAsync();

        result.Status.Should().Be(ChoTokenStatus.ConsentRequired);
        _reauthentication.Verify(r => r.Challenge(
            It.Is<MicrosoftIdentityWebChallengeUserException>(e => e.Scopes.SequenceEqual(new[] { Scope })),
            It.IsAny<ClaimsPrincipal>()), Times.Once);
    }

    // ── Caching ──

    [Fact]
    public async Task GetTokenAsync_SameUserAndTenant_ReusesTheCachedTokenAcrossCircuits()
    {
        var first = await CreateProvider(EntraUser()).GetTokenAsync();
        var second = await CreateProvider(EntraUser()).GetTokenAsync();

        second.Token!.AccessToken.Should().Be(first.Token!.AccessToken);
        _exchanges.Should().ContainSingle();
    }

    [Fact]
    public async Task GetTokenAsync_DifferentUsers_NeverShareATokenEvenInTheSameTenant()
    {
        var n = 0;
        _exchangeResponse = _ => Json(HttpStatusCode.OK, ExchangeJson(accessToken: $"cho-token-{++n}"));

        var jane = await CreateProvider(EntraUser(oid: "oid-jane")).GetTokenAsync();
        var bob = await CreateProvider(EntraUser(oid: "oid-bob")).GetTokenAsync();
        var otherOrgSameOid = await CreateProvider(EntraUser(tid: "entra-tid-9", oid: "oid-jane")).GetTokenAsync();
        var janeAgain = await CreateProvider(EntraUser(oid: "oid-jane")).GetTokenAsync();

        new[] { jane, bob, otherOrgSameOid }.Select(r => r.Token!.AccessToken)
            .Should().OnlyHaveUniqueItems("every distinct (Entra tid, oid) gets its own exchange");
        janeAgain.Token!.AccessToken.Should().Be(jane.Token!.AccessToken);
        _exchanges.Select(e => e.Bearer).Should().Equal(
            "entra-token-for-oid-jane", "entra-token-for-oid-bob", "entra-token-for-oid-jane");
    }

    [Fact]
    public async Task GetTokenAsync_LongFormEntraClaims_IdentifyTheUserForCaching()
    {
        ClaimsPrincipal User() => new(new ClaimsIdentity(new[]
        {
            new Claim("http://schemas.microsoft.com/identity/claims/tenantid", "entra-tid-1"),
            new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", "oid-1"),
            new Claim(ClaimTypes.Email, "jane@acme.com"),
        }, "TestAuth"));

        await CreateProvider(User()).GetTokenAsync();
        await CreateProvider(User()).GetTokenAsync();

        _exchanges.Should().ContainSingle();
    }

    [Fact]
    public async Task GetTokenAsync_UserWithoutTidOrOid_IsNeverCached()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, "x@acme.com") }, "TestAuth"));

        await CreateProvider(user).GetTokenAsync();
        await CreateProvider(user).GetTokenAsync();

        _exchanges.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetTokenAsync_TokenExpiringWithinSixtySeconds_IsNotCached()
    {
        _exchangeResponse = _ => Json(HttpStatusCode.OK, ExchangeJson(expiresIn: 60));

        await CreateProvider().GetTokenAsync();
        await CreateProvider().GetTokenAsync();

        _exchanges.Should().HaveCount(2);
    }

    [Fact]
    public async Task Invalidate_DropsTheCachedTokenSoTheNextCallExchangesAgain()
    {
        var n = 0;
        _exchangeResponse = _ => Json(HttpStatusCode.OK, ExchangeJson(accessToken: $"cho-token-{++n}"));
        var sut = CreateProvider();
        var first = await sut.GetTokenAsync();

        sut.Invalidate(first.Token!);
        var second = await sut.GetTokenAsync();

        second.Token!.AccessToken.Should().Be("cho-token-2");
        _exchanges.Should().HaveCount(2);
    }

    [Fact]
    public async Task Invalidate_WithAnOlderToken_KeepsTheNewerCachedToken()
    {
        var n = 0;
        _exchangeResponse = _ => Json(HttpStatusCode.OK, ExchangeJson(accessToken: $"cho-token-{++n}"));
        var sut = CreateProvider();
        var first = await sut.GetTokenAsync();
        sut.Invalidate(first.Token!);
        await sut.GetTokenAsync();

        sut.Invalidate(first.Token!); // stale: already replaced
        var third = await sut.GetTokenAsync();

        third.Token!.AccessToken.Should().Be("cho-token-2");
        _exchanges.Should().HaveCount(2);
    }

    // ── Tenants and switching ──

    [Fact]
    public async Task GetTenantsAsync_ReadsTheTokenServiceListWithTheEntraToken()
    {
        var sut = CreateProvider();

        var tenants = await sut.GetTenantsAsync();

        tenants!.Select(t => t.TenantId).Should().Equal("tenant-1", "tenant-2");
        var call = _tenantListCalls.Should().ContainSingle().Subject;
        call.Method.Should().Be(HttpMethod.Get);
        call.Headers.Authorization!.Parameter.Should().Be("entra-token-for-oid-1");
    }

    [Fact]
    public async Task GetTenantsAsync_WhenTokenServiceFails_ReturnsNull()
    {
        _tenantsResponse = _ => Json(HttpStatusCode.ServiceUnavailable, """{"error":"unavailable"}""");

        (await CreateProvider().GetTenantsAsync()).Should().BeNull();
    }

    [Fact]
    public async Task SwitchTenantAsync_ListedTenant_ReExchangesForThatTenant()
    {
        _exchangeResponse = request => Json(HttpStatusCode.OK,
            ExchangeJson(tenantId: RequestedTenant(request) ?? "tenant-1", accessToken: "token-" + (RequestedTenant(request) ?? "home")));
        var sut = CreateProvider();
        await sut.GetTokenAsync();

        var result = await sut.SwitchTenantAsync("tenant-2");

        result.Succeeded.Should().BeTrue();
        result.Token!.AccessToken.Should().Be("token-tenant-2");
        sut.CurrentTenantId.Should().Be("tenant-2");
        (await sut.GetTokenAsync()).Token!.AccessToken.Should().Be("token-tenant-2");
        _exchanges.Select(e => e.Tenant).Should().Equal(null, "tenant-2");
    }

    [Fact]
    public async Task SwitchTenantAsync_DropsCachedTokenForTheTargetTenant()
    {
        var n = 0;
        _exchangeResponse = request => Json(HttpStatusCode.OK,
            ExchangeJson(tenantId: RequestedTenant(request) ?? "tenant-1", accessToken: $"token-{++n}"));
        var sut = CreateProvider();
        await sut.SwitchTenantAsync("tenant-2");
        await sut.SwitchTenantAsync("tenant-1");

        var again = await sut.SwitchTenantAsync("tenant-2");

        again.Token!.AccessToken.Should().Be("token-3", "a switch always asks the token service again");
        _exchanges.Should().HaveCount(3);
    }

    [Fact]
    public async Task SwitchTenantAsync_UnlistedTenant_IsRefusedWithoutAnExchange()
    {
        var sut = CreateProvider();
        await sut.GetTokenAsync();
        _exchanges.Clear();

        var result = await sut.SwitchTenantAsync("tenant-not-listed");

        result.Succeeded.Should().BeFalse();
        result.Status.Should().Be(ChoTokenStatus.NoAccess);
        _exchanges.Should().BeEmpty();
        sut.CurrentTenantId.Should().Be("tenant-1");
    }

    [Fact]
    public async Task SwitchTenantAsync_WhenTokenServiceRefusesTheListedTenant_KeepsTheCurrentTenant()
    {
        _exchangeResponse = request => RequestedTenant(request) == "tenant-2"
            ? Json(HttpStatusCode.Forbidden, """{"error":"no_access"}""")
            : Json(HttpStatusCode.OK, ExchangeJson());
        var sut = CreateProvider();
        await sut.GetTokenAsync();

        var result = await sut.SwitchTenantAsync("tenant-2");

        result.Status.Should().Be(ChoTokenStatus.NoAccess);
        sut.CurrentTenantId.Should().Be("tenant-1");
    }

    [Fact]
    public async Task SwitchTenantAsync_IsRememberedForTheUsersNextCircuit()
    {
        _exchangeResponse = request => Json(HttpStatusCode.OK,
            ExchangeJson(tenantId: RequestedTenant(request) ?? "tenant-1", accessToken: "token-" + (RequestedTenant(request) ?? "home")));
        await CreateProvider(EntraUser()).SwitchTenantAsync("tenant-2");

        var nextCircuit = await CreateProvider(EntraUser()).GetTokenAsync();
        var otherUser = await CreateProvider(EntraUser(oid: "oid-2")).GetTokenAsync();

        nextCircuit.Token!.TenantId.Should().Be("tenant-2");
        otherUser.Token!.TenantId.Should().Be("tenant-1", "the choice belongs to the user who made it");
    }

    [Fact]
    public async Task GetTokenAsync_WhenRememberedTenantIsNoLongerAllowed_FallsBackToHomeTenant()
    {
        var revoked = false;
        _exchangeResponse = request => (RequestedTenant(request), revoked) switch
        {
            ("tenant-2", true) => Json(HttpStatusCode.Forbidden, """{"error":"no_access"}"""),
            (var t, _) => Json(HttpStatusCode.OK, ExchangeJson(tenantId: t ?? "tenant-1", accessToken: "token-" + (t ?? "home"), expiresIn: 30)),
        };
        await CreateProvider().SwitchTenantAsync("tenant-2");
        revoked = true;

        var result = await CreateProvider().GetTokenAsync();

        result.Token!.TenantId.Should().Be("tenant-1");
    }

    // ── LocalDemo ──

    [Fact]
    public async Task LocalDemo_InDevelopment_MintsADevelopmentSignedTokenWithoutTheTokenService()
    {
        var sut = CreateProvider(LocalDemoUser(), environment: "Development", authMode: "LocalDemo", withTokenAcquisition: false);

        var result = await sut.GetTokenAsync();

        result.Succeeded.Should().BeTrue();
        _exchanges.Should().BeEmpty();
        result.Token!.TenantId.Should().Be("demo");
        result.Token.Roles.Should().BeEquivalentTo(ChoTokenProvider.LocalDemoRoles);
        result.Token.Permissions.Should().BeEquivalentTo(ChoRolePermissions.Expand(ChoTokenProvider.LocalDemoRoles));

        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(result.Token.AccessToken, new TokenValidationParameters
        {
            ValidIssuer = ChoDevelopmentAuth.UserIssuer,
            ValidAudience = ChoDevelopmentAuth.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(ChoDevelopmentAuth.SymmetricKey)),
        });
        validation.IsValid.Should().BeTrue();
        validation.Claims[ChoClaimTypes.TenantId].Should().Be("demo");
        validation.Claims[ChoClaimTypes.Subject].Should().Be("local-demo-admin");
    }

    [Theory]
    [InlineData("Production", "LocalDemo")]
    [InlineData("Staging", "LocalDemo")]
    public async Task LocalDemo_OutsideDevelopment_NeverMintsAToken(string environment, string mode)
    {
        var sut = CreateProvider(LocalDemoUser(), environment: environment, authMode: mode, withTokenAcquisition: false);

        var result = await sut.GetTokenAsync();

        result.Succeeded.Should().BeFalse();
        _exchanges.Should().BeEmpty();
    }

    [Fact]
    public async Task LocalDemoClaim_InEntraMode_IsNotMintedLocally()
    {
        var sut = CreateProvider(LocalDemoUser(), environment: "Development", authMode: "Entra");

        var result = await sut.GetTokenAsync();

        // The claim alone means nothing: the token service is asked as for any user.
        _exchanges.Should().ContainSingle();
        result.Token!.AccessToken.Should().Be("cho-token-1");
    }

    [Fact]
    public async Task LocalDemoMode_ForAUserWithoutTheLocalDemoClaim_DoesNotMint()
    {
        var sut = CreateProvider(EntraUser(), environment: "Development", authMode: "LocalDemo");

        var result = await sut.GetTokenAsync();

        result.Token?.AccessToken.Should().Be("cho-token-1", "only the token service could have issued it");
        _exchanges.Should().ContainSingle();
    }

    [Fact]
    public async Task LocalDemo_ListsOnlyTheDemoTenant_AndRefusesOthers()
    {
        var sut = CreateProvider(LocalDemoUser(), environment: "Development", authMode: "LocalDemo", withTokenAcquisition: false);

        var tenants = await sut.GetTenantsAsync();
        var switched = await sut.SwitchTenantAsync("tenant-2");

        tenants!.Select(t => t.TenantId).Should().Equal("demo");
        switched.Succeeded.Should().BeFalse();
        _tenantListCalls.Should().BeEmpty();
    }

    private sealed record ExchangeCall(HttpMethod Method, Uri RequestUri, string? Scheme, string? Bearer, string? Tenant);
}
