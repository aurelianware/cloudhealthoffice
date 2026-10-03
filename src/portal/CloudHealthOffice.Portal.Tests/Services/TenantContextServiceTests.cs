using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Web;
using CloudHealthOffice.Portal.Services;
using static CloudHealthOffice.Portal.Tests.Services.ChoTokenTestSupport;

namespace CloudHealthOffice.Portal.Tests.Services;

public class TenantContextServiceTests
{
    private readonly Mock<AuthenticationStateProvider> _authStateProvider = new();
    private readonly Mock<IChoTokenProvider> _tokenProvider = new();
    private readonly Mock<ITenantService> _tenantService = new();
    private readonly Mock<ILogger<TenantContextService>> _logger = new();
    private IConfiguration _configuration = new ConfigurationBuilder().Build();

    private TenantContextService CreateService()
        => new(_authStateProvider.Object, _tokenProvider.Object, _tenantService.Object, _logger.Object, _configuration);

    private void SignIn(params Claim[] claims)
    {
        var identity = claims.Length > 0 ? new ClaimsIdentity(claims, "TestAuth") : new ClaimsIdentity();
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));
    }

    private static Claim[] EntraClaims(string tid = "azure-home", string email = "user@example.com")
        => new[] { new Claim("tid", tid), new Claim("oid", "oid-1"), new Claim(ClaimTypes.Email, email) };

    private static ChoTokenExchangeResponse Token(
        string tenantId = "cho-tenant-456", string? tenantName = "ACME Health Plan",
        string? email = "admin@acme.com", params string[] permissions)
        => new()
        {
            AccessToken = "cho-token", ExpiresIn = 3600, TenantId = tenantId, TenantName = tenantName,
            Roles = new List<string> { "ClaimsExaminer" },
            Permissions = permissions.Length > 0 ? permissions.ToList() : new List<string> { "claims:read" },
            User = new ChoTokenUser { Id = "usr-1", Email = email }
        };

    private void Exchange(ChoTokenExchangeResponse token)
        => _tokenProvider.Setup(x => x.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoTokenResult.Success(token));

    private void ExchangeFails(ChoTokenStatus status)
        => _tokenProvider.Setup(x => x.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoTokenResult.Failure(status));

    private void ListTenants(params ChoTenantInfo[] tenants)
        => _tokenProvider.Setup(x => x.GetTenantsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenants);

    private static ChoTenantInfo Tenant(string tenantId, string name, string azureTenantId)
        => new() { TenantId = tenantId, TenantName = name, AzureTenantId = azureTenantId };

    // ── Resolution ──

    [Fact]
    public async Task GetCurrentTenantContextAsync_WhenUserNotAuthenticated_ReturnsNull()
    {
        SignIn();

        var result = await CreateService().GetCurrentTenantContextAsync();

        result.Should().BeNull();
        _tokenProvider.Verify(x => x.GetTokenAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(ChoTokenStatus.NoAccess)]
    [InlineData(ChoTokenStatus.Unavailable)]
    [InlineData(ChoTokenStatus.TenantRequired)]
    [InlineData(ChoTokenStatus.InvalidToken)]
    [InlineData(ChoTokenStatus.ConsentRequired)]
    public async Task GetCurrentTenantContextAsync_WithoutACHOToken_HasNoTenant(ChoTokenStatus status)
    {
        // Replaces the removed "no subscription found → use the Azure tid as the tenant" fallback.
        SignIn(EntraClaims("azure-tenant-123"));
        ExchangeFails(status);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(It.IsAny<string>()))
            .ReturnsAsync((TenantSubscription?)null);

        var sut = CreateService();
        var result = await sut.GetCurrentTenantContextAsync();

        result.Should().BeNull();
        (await sut.GetTenantIdAsync()).Should().BeNull();
        sut.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentTenantContextAsync_TakesTheTenantFromTheToken()
    {
        SignIn(EntraClaims("azure-tenant-123"));
        Exchange(Token(tenantId: "cho-tenant-456", tenantName: "ACME Health Plan"));
        ListTenants(Tenant("cho-tenant-456", "ACME Health Plan", "azure-tenant-123"));

        var result = await CreateService().GetCurrentTenantContextAsync();

        result.Should().NotBeNull();
        result!.TenantId.Should().Be("cho-tenant-456");
        result.TenantName.Should().Be("ACME Health Plan");
        result.AzureTenantId.Should().Be("azure-tenant-123");
        result.UserEmail.Should().Be("admin@acme.com");
    }

    [Fact]
    public async Task GetCurrentTenantContextAsync_ShowsSubscriptionDetailsForTheTokensTenant()
    {
        SignIn(EntraClaims("azure-tenant-demo"));
        Exchange(Token(tenantId: "demo-tenant", tenantName: "Demo Payer"));
        ListTenants(Tenant("demo-tenant", "Demo Payer", "azure-tenant-demo"));
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync("azure-tenant-demo"))
            .ReturnsAsync(new TenantSubscription
            {
                TenantId = "demo-tenant", AzureTenantId = "azure-tenant-demo", OrganizationName = "Demo Payer",
                SubscriptionStatus = "Active", Tier = "starter", IsDemo = true
            });

        var sut = CreateService();
        var result = await sut.GetCurrentTenantContextAsync();

        result!.IsDemo.Should().BeTrue();
        result.SubscriptionTier.Should().Be("starter");
        result.SubscriptionStatus.Should().Be("Active");
        sut.IsDemo.Should().BeTrue();
    }

    [Fact]
    public async Task GetCurrentTenantContextAsync_IgnoresASubscriptionOfAnotherTenant()
    {
        SignIn(EntraClaims("azure-home"));
        Exchange(Token(tenantId: "cho-tenant-456"));
        ListTenants(Tenant("cho-tenant-456", "ACME", "azure-home"));
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync("azure-home"))
            .ReturnsAsync(new TenantSubscription { TenantId = "some-other-tenant", IsDemo = true, Tier = "enterprise" });

        var result = await CreateService().GetCurrentTenantContextAsync();

        result!.TenantId.Should().Be("cho-tenant-456");
        result.IsDemo.Should().BeFalse();
        result.SubscriptionTier.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCurrentTenantContextAsync_WhenSubscriptionLookupFails_StillResolvesTheTenant()
    {
        SignIn(EntraClaims("azure-home"));
        Exchange(Token(tenantId: "cho-tenant-456"));
        ListTenants(Tenant("cho-tenant-456", "ACME", "azure-home"));
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(It.IsAny<string>()))
            .ThrowsAsync(new TimeoutException("mongo down"));

        var result = await CreateService().GetCurrentTenantContextAsync();

        result!.TenantId.Should().Be("cho-tenant-456");
    }

    [Fact]
    public async Task GetCurrentTenantContextAsync_CachesResult_DoesNotExchangeTwice()
    {
        SignIn(EntraClaims());
        Exchange(Token());
        ListTenants();
        var sut = CreateService();

        var result1 = await sut.GetCurrentTenantContextAsync();
        var result2 = await sut.GetCurrentTenantContextAsync();

        result1.Should().BeSameAs(result2);
        _tokenProvider.Verify(x => x.GetTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetTenantIdAsync_ReturnsCurrentTenantId()
    {
        SignIn(EntraClaims());
        Exchange(Token(tenantId: "cho-tenant-456"));
        ListTenants();

        (await CreateService().GetTenantIdAsync()).Should().Be("cho-tenant-456");
    }

    [Fact]
    public void TenantId_Property_ReturnsNull_WhenNotInitialized()
        => CreateService().TenantId.Should().BeNull();

    [Fact]
    public void IsDemo_Property_ReturnsFalse_WhenNotInitialized()
        => CreateService().IsDemo.Should().BeFalse();

    [Theory]
    [InlineData(ClaimTypes.Email, "user@example.com")]
    [InlineData("preferred_username", "user@example.com")]
    [InlineData("upn", "user@example.com")]
    public async Task GetCurrentTenantContextAsync_WhenResponseHasNoEmail_UsesTheEmailClaim(string claimType, string email)
    {
        SignIn(new Claim("tid", "azure-tenant-123"), new Claim(claimType, email));
        Exchange(Token(email: null));
        ListTenants();

        var result = await CreateService().GetCurrentTenantContextAsync();

        result!.UserEmail.Should().Be(email);
    }

    // ── Available tenants come from the token service ──

    [Fact]
    public async Task GetAvailableTenantsAsync_ListsTheTokenServiceTenants()
    {
        SignIn(EntraClaims());
        ListTenants(Tenant("t-1", "Alpha", "az-1"), Tenant("t-2", "Beta", "az-2"));
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync("az-2"))
            .ReturnsAsync(new TenantSubscription { TenantId = "t-2", Tier = "professional", IsDemo = true });

        var tenants = await CreateService().GetAvailableTenantsAsync();

        tenants.Select(t => (t.TenantId, t.OrganizationName, t.AzureTenantId))
            .Should().Equal(("t-1", "Alpha", "az-1"), ("t-2", "Beta", "az-2"));
        tenants[1].Tier.Should().Be("professional");
        tenants[1].IsDemo.Should().BeTrue();
        // No email-based tenant matching in the portal any more.
        _tenantService.Verify(x => x.GetTenantsForUserAsync(It.IsAny<string>()), Times.Never);
        _tenantService.Verify(x => x.IsMemberOfTenantAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetAvailableTenantsAsync_WhenTokenServiceUnavailable_ReturnsEmptyList()
    {
        SignIn(EntraClaims());
        _tokenProvider.Setup(x => x.GetTenantsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ChoTenantInfo>?)null);

        (await CreateService().GetAvailableTenantsAsync()).Should().BeEmpty();
    }

    // ── Switching ──

    [Fact]
    public async Task SwitchTenantAsync_ListedTenant_ReExchangesAndUpdatesContext()
    {
        SignIn(EntraClaims("azure-home"));
        Exchange(Token(tenantId: "t-home", tenantName: "Home"));
        ListTenants(Tenant("t-home", "Home", "azure-home"), Tenant("t-2", "Beta", "az-2"));
        _tokenProvider.Setup(x => x.SwitchTenantAsync("t-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoTokenResult.Success(Token(tenantId: "t-2", tenantName: "Beta")));
        var sut = CreateService();
        await sut.GetCurrentTenantContextAsync();

        var success = await sut.SwitchTenantAsync("t-2");

        success.Should().BeTrue();
        var context = await sut.GetCurrentTenantContextAsync();
        context!.TenantId.Should().Be("t-2");
        context.TenantName.Should().Be("Beta");
        context.AzureTenantId.Should().Be("az-2");
        context.UserEmail.Should().Be("admin@acme.com");
        sut.IsImpersonating.Should().BeFalse("a member switching between their tenants is not impersonating");
    }

    [Fact]
    public async Task SwitchTenantAsync_WhenRefused_KeepsTheCurrentContext()
    {
        SignIn(EntraClaims());
        Exchange(Token(tenantId: "t-home"));
        ListTenants(Tenant("t-home", "Home", "azure-home"));
        _tokenProvider.Setup(x => x.SwitchTenantAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoTokenResult.Failure(ChoTokenStatus.NoAccess));
        var sut = CreateService();
        await sut.GetCurrentTenantContextAsync();

        var success = await sut.SwitchTenantAsync("t-other");

        success.Should().BeFalse();
        (await sut.GetTenantIdAsync()).Should().Be("t-home");
    }

    [Fact]
    public async Task SwitchTenantAsync_PlatformAdminRoleClaim_DoesNotBypassTheTokenService()
    {
        // Removed: IsInRole("PlatformAdmin") used to allow switching to any subscription.
        SignIn(EntraClaims().Append(new Claim(ClaimTypes.Role, "PlatformAdmin"))
            .Append(new Claim("permissions", "platform:admin")).ToArray());
        Exchange(Token(tenantId: "t-home"));
        ListTenants(Tenant("t-home", "Home", "azure-home"));
        _tokenProvider.Setup(x => x.SwitchTenantAsync("t-elsewhere", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoTokenResult.Failure(ChoTokenStatus.NoAccess));
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(It.IsAny<string>()))
            .ReturnsAsync(new TenantSubscription { TenantId = "t-elsewhere", AzureTenantId = "az-elsewhere" });
        var sut = CreateService();

        var success = await sut.SwitchTenantAsync("t-elsewhere");

        success.Should().BeFalse();
        sut.IsImpersonating.Should().BeFalse();
    }

    [Fact]
    public async Task SwitchTenantAsync_PlatformAdminIntoAnotherOrganisation_IsShownAsImpersonating()
    {
        SignIn(EntraClaims("azure-home"));
        Exchange(Token(tenantId: "t-home", permissions: new[] { "platform:admin", "*:*" }));
        ListTenants(Tenant("t-home", "Home", "azure-home"), Tenant("t-client", "Client", "azure-client"));
        _tokenProvider.Setup(x => x.SwitchTenantAsync("t-client", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoTokenResult.Success(Token(tenantId: "t-client", permissions: new[] { "platform:admin", "*:*" })));
        var sut = CreateService();
        await sut.GetCurrentTenantContextAsync();
        sut.IsImpersonating.Should().BeFalse();

        (await sut.SwitchTenantAsync("t-client")).Should().BeTrue();
        sut.IsImpersonating.Should().BeTrue();

        _tokenProvider.Setup(x => x.SwitchTenantAsync("t-home", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoTokenResult.Success(Token(tenantId: "t-home", permissions: new[] { "platform:admin", "*:*" })));
        (await sut.SwitchTenantAsync("t-home")).Should().BeTrue();
        sut.IsImpersonating.Should().BeFalse();
    }

    [Fact]
    public void IsImpersonating_ReturnsFalse_WhenNotInitialized()
        => CreateService().IsImpersonating.Should().BeFalse();

    [Fact]
    public async Task SwitchTenantAsync_WithTheRealProvider_RefusesUnlistedTenantsAndReExchangesListedOnes()
    {
        var exchanged = new List<string?>();
        var tokenService = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/token/tenants" => Json(HttpStatusCode.OK,
                """[{"tenantId":"tenant-1","tenantName":"Acme","azureTenantId":"entra-tid-1"},{"tenantId":"tenant-2","tenantName":"Beta","azureTenantId":"entra-tid-2"}]"""),
            "/v1/token/exchange" => Respond(RequestedTenant(request)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        HttpResponseMessage Respond(string? tenant)
        {
            exchanged.Add(tenant);
            return Json(HttpStatusCode.OK, ExchangeJson(tenantId: tenant ?? "tenant-1", tenantName: tenant ?? "Acme"));
        }

        var tokenAcquisition = new Mock<ITokenAcquisition>();
        tokenAcquisition
            .Setup(t => t.GetAccessTokenForUserAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<ClaimsPrincipal?>(), It.IsAny<TokenAcquisitionOptions?>()))
            .ReturnsAsync("entra-token");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:TokenService"] = TokenServiceUrl,
            ["TokenService:Scope"] = Scope,
        }).Build();
        var auth = new StaticAuthenticationStateProvider(EntraUser());
        var provider = new ChoTokenProvider(auth, new SingleHandlerHttpClientFactory(tokenService),
            new MemoryCache(new MemoryCacheOptions()), configuration, new TestHostEnvironment("Production"),
            NullLogger<ChoTokenProvider>.Instance, tokenAcquisition.Object);
        var sut = new TenantContextService(auth, provider, _tenantService.Object,
            NullLogger<TenantContextService>.Instance, configuration);

        (await sut.GetTenantIdAsync()).Should().Be("tenant-1");
        (await sut.SwitchTenantAsync("tenant-9")).Should().BeFalse();
        (await sut.SwitchTenantAsync("tenant-2")).Should().BeTrue();

        (await sut.GetTenantIdAsync()).Should().Be("tenant-2");
        exchanged.Should().Equal(null, "tenant-2");
    }

    // ── LocalDemo ──

    [Fact]
    public async Task LocalDemo_ShowsTheDemoTenant()
    {
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "LocalDemo",
            ["Authentication:LocalDemo:TenantName"] = "Local Demo Tenant",
            ["Authentication:LocalDemo:AzureTenantId"] = "local-demo",
        }).Build();
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(LocalDemoUser()));
        Exchange(Token(tenantId: "demo", tenantName: "Local Demo Tenant"));

        var result = await CreateService().GetCurrentTenantContextAsync();

        result!.TenantId.Should().Be("demo");
        result.TenantName.Should().Be("Local Demo Tenant");
        result.AzureTenantId.Should().Be("local-demo");
        result.IsDemo.Should().BeTrue();
        result.SubscriptionTier.Should().Be("local-demo");
    }
}
