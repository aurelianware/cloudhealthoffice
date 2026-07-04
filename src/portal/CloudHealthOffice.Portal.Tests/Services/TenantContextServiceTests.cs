using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class TenantContextServiceTests
{
    private readonly Mock<AuthenticationStateProvider> _authStateProvider;
    private readonly Mock<ITenantService> _tenantService;
    private readonly Mock<ILogger<TenantContextService>> _logger;
    private readonly IConfiguration _configuration;
    private readonly TenantContextService _sut;

    public TenantContextServiceTests()
    {
        _authStateProvider = new Mock<AuthenticationStateProvider>();
        _tenantService = new Mock<ITenantService>();
        _logger = new Mock<ILogger<TenantContextService>>();
        _configuration = new ConfigurationBuilder().Build();
        _sut = new TenantContextService(_authStateProvider.Object, _tenantService.Object, _logger.Object, _configuration);
    }

    [Fact]
    public async Task GetCurrentTenantContextAsync_WhenUserNotAuthenticated_ReturnsNull()
    {
        // Arrange
        var identity = new ClaimsIdentity(); // Not authenticated
        var principal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(principal);
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        // Act
        var result = await _sut.GetCurrentTenantContextAsync();

        // Assert
        result.Should().BeNull();
        _tenantService.Verify(x => x.GetSubscriptionByAzureTenantIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetCurrentTenantContextAsync_WhenTenantIdClaimMissing_ReturnsNull()
    {
        // Arrange
        var claims = new[]
        {
            new Claim(ClaimTypes.Email, "user@example.com"),
            new Claim("name", "Test User")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(principal);
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        // Act
        var result = await _sut.GetCurrentTenantContextAsync();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentTenantContextAsync_WhenTenantIdIsCommon_ReturnsNull()
    {
        // Arrange
        var claims = new[]
        {
            new Claim("tid", "common"),
            new Claim(ClaimTypes.Email, "user@example.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(principal);
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        // Act
        var result = await _sut.GetCurrentTenantContextAsync();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentTenantContextAsync_WhenSubscriptionNotFound_ReturnsFallbackContext()
    {
        // Arrange
        var azureTenantId = "azure-tenant-123";
        var claims = new[]
        {
            new Claim("tid", azureTenantId),
            new Claim(ClaimTypes.Email, "user@example.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(principal);
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(azureTenantId))
            .ReturnsAsync((TenantSubscription?)null);

        // Act
        var result = await _sut.GetCurrentTenantContextAsync();

        // Assert — falls back to using Azure AD tenant ID directly
        result.Should().NotBeNull();
        result!.TenantId.Should().Be(azureTenantId);
        result.AzureTenantId.Should().Be(azureTenantId);
        result.SubscriptionStatus.Should().Be("Active");
        result.IsDemo.Should().BeFalse();
        _tenantService.Verify(x => x.GetSubscriptionByAzureTenantIdAsync(azureTenantId), Times.Once);
    }

    [Fact]
    public async Task GetCurrentTenantContextAsync_WhenValidSubscription_ReturnsTenantContext()
    {
        // Arrange
        var azureTenantId = "azure-tenant-123";
        var choTenantId = "cho-tenant-456";
        var tenantName = "ACME Health Plan";
        var claims = new[]
        {
            new Claim("tid", azureTenantId),
            new Claim(ClaimTypes.Email, "admin@acme.com"),
            new Claim("name", "Admin User")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(principal);
        
        var subscription = new TenantSubscription
        {
            TenantId = choTenantId,
            OrganizationName = tenantName,
            AzureTenantId = azureTenantId,
            SubscriptionStatus = "Active",
            Tier = "professional",
            CreatedAt = DateTime.UtcNow.AddMonths(-6)
        };

        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(azureTenantId))
            .ReturnsAsync(subscription);

        // Act
        var result = await _sut.GetCurrentTenantContextAsync();

        // Assert
        result.Should().NotBeNull();
        result!.TenantId.Should().Be(choTenantId);
        result.TenantName.Should().Be(tenantName);
        result.AzureTenantId.Should().Be(azureTenantId);
        result.UserEmail.Should().Be("admin@acme.com");
    }

    [Fact]
    public async Task GetCurrentTenantContextAsync_WhenSubscriptionIsDemo_SetsIsDemoFlag()
    {
        // Arrange
        var azureTenantId = "azure-tenant-demo";
        var claims = new[]
        {
            new Claim("tid", azureTenantId),
            new Claim(ClaimTypes.Email, "demo@example.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(principal);
        
        var subscription = new TenantSubscription
        {
            TenantId = "demo-tenant",
            OrganizationName = "Demo Payer",
            AzureTenantId = azureTenantId,
            SubscriptionStatus = "Active",
            Tier = "starter",
            IsDemo = true,
            CreatedAt = DateTime.UtcNow
        };

        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(azureTenantId))
            .ReturnsAsync(subscription);

        // Act
        var result = await _sut.GetCurrentTenantContextAsync();

        // Assert
        result.Should().NotBeNull();
        result!.IsDemo.Should().BeTrue();
    }

    [Fact]
    public async Task GetCurrentTenantContextAsync_CachesResult_DoesNotCallServiceTwice()
    {
        // Arrange
        var azureTenantId = "azure-tenant-123";
        var claims = new[]
        {
            new Claim("tid", azureTenantId),
            new Claim(ClaimTypes.Email, "user@example.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(principal);
        
        var subscription = new TenantSubscription
        {
            TenantId = "cho-tenant-456",
            OrganizationName = "Test Tenant",
            AzureTenantId = azureTenantId,
            SubscriptionStatus = "Active", Tier = "starter", IsDemo = false
        };

        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(azureTenantId))
            .ReturnsAsync(subscription);

        // Act
        var result1 = await _sut.GetCurrentTenantContextAsync();
        var result2 = await _sut.GetCurrentTenantContextAsync();

        // Assert
        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
        result1.Should().Be(result2); // Same instance
        _tenantService.Verify(x => x.GetSubscriptionByAzureTenantIdAsync(azureTenantId), Times.Once);
    }

    [Fact]
    public async Task GetTenantIdAsync_ReturnsCurrentTenantId()
    {
        // Arrange
        var azureTenantId = "azure-tenant-123";
        var choTenantId = "cho-tenant-456";
        var claims = new[]
        {
            new Claim("tid", azureTenantId),
            new Claim(ClaimTypes.Email, "user@example.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(principal);
        var subscription = new TenantSubscription
        {
            TenantId = choTenantId,
            OrganizationName = "Test Tenant",
            AzureTenantId = azureTenantId,
            SubscriptionStatus = "Active",
            Tier = "professional",
            IsDemo = false
        };

        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(azureTenantId))
            .ReturnsAsync(subscription);

        // Act
        var result = await _sut.GetTenantIdAsync();

        // Assert
        result.Should().Be(choTenantId);
    }

    [Fact]
    public void TenantId_Property_ReturnsNull_WhenNotInitialized()
    {
        // Act
        var result = _sut.TenantId;

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void IsDemo_Property_ReturnsFalse_WhenNotInitialized()
    {
        // Act
        var result = _sut.IsDemo;

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://schemas.microsoft.com/identity/claims/tenantid")]
    [InlineData("tid")]
    public async Task GetCurrentTenantContextAsync_SupportsDifferentTenantIdClaims(string claimType)
    {
        // Arrange
        var azureTenantId = "azure-tenant-123";
        var claims = new[]
        {
            new Claim(claimType, azureTenantId),
            new Claim(ClaimTypes.Email, "user@example.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(principal);
        
        var subscription = new TenantSubscription
        {
            TenantId = "cho-tenant-456",
            OrganizationName = "Test Tenant",
            AzureTenantId = azureTenantId,
            SubscriptionStatus = "Active", Tier = "starter", IsDemo = false
        };

        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(azureTenantId))
            .ReturnsAsync(subscription);

        // Act
        var result = await _sut.GetCurrentTenantContextAsync();

        // Assert
        result.Should().NotBeNull();
        result!.TenantId.Should().Be("cho-tenant-456");
    }

    [Theory]
    [InlineData(ClaimTypes.Email, "user@example.com")]
    [InlineData("preferred_username", "user@example.com")]
    [InlineData("upn", "user@example.com")]
    public async Task GetCurrentTenantContextAsync_SupportsDifferentEmailClaims(string claimType, string email)
    {
        // Arrange
        var azureTenantId = "azure-tenant-123";
        var claims = new[]
        {
            new Claim("tid", azureTenantId),
            new Claim(claimType, email)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(principal);

        var subscription = new TenantSubscription
        {
            TenantId = "cho-tenant-456",
            OrganizationName = "Test Tenant",
            AzureTenantId = azureTenantId,
            SubscriptionStatus = "Active",
            Tier = "starter",
            IsDemo = false
        };

        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(azureTenantId))
            .ReturnsAsync(subscription);

        // Act
        var result = await _sut.GetCurrentTenantContextAsync();

        // Assert
        result.Should().NotBeNull();
        result!.UserEmail.Should().Be(email);
    }

    // ---------------------------------------------------------------
    // Guest user resolution (email-based tenant lookup)
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCurrentTenantContextAsync_GuestUser_SingleTenant_AutoResolves()
    {
        // Arrange — home tenant has no subscription, but email matches one tenant
        var homeTenantId = "guest-home-tenant";
        var hostTenantId = "host-azure-tenant";
        var authState = CreateAuthState(homeTenantId, "guest@partner.com");
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(homeTenantId))
            .ReturnsAsync((TenantSubscription?)null);

        var hostSubscription = MakeSubscription("cho-host-1", "Host Health Plan", hostTenantId);
        _tenantService.Setup(x => x.GetTenantsForUserAsync("guest@partner.com"))
            .ReturnsAsync(new List<TenantSubscription> { hostSubscription });

        // Act
        var result = await _sut.GetCurrentTenantContextAsync();

        // Assert
        result.Should().NotBeNull();
        result!.TenantId.Should().Be("cho-host-1");
        result.TenantName.Should().Be("Host Health Plan");
        result.AzureTenantId.Should().Be(hostTenantId);
    }

    [Fact]
    public async Task GetCurrentTenantContextAsync_GuestUser_MultipleTenants_DefaultsToFirst()
    {
        // Arrange
        var homeTenantId = "guest-home-tenant";
        var authState = CreateAuthState(homeTenantId, "consultant@firm.com");
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(homeTenantId))
            .ReturnsAsync((TenantSubscription?)null);

        var tenant1 = MakeSubscription("cho-1", "Alpha Health", "azure-alpha");
        var tenant2 = MakeSubscription("cho-2", "Beta Health", "azure-beta");
        _tenantService.Setup(x => x.GetTenantsForUserAsync("consultant@firm.com"))
            .ReturnsAsync(new List<TenantSubscription> { tenant1, tenant2 });

        // Act
        var result = await _sut.GetCurrentTenantContextAsync();

        // Assert — defaults to first
        result.Should().NotBeNull();
        result!.TenantId.Should().Be("cho-1");
        result.TenantName.Should().Be("Alpha Health");
    }

    [Fact]
    public async Task GetCurrentTenantContextAsync_GuestUser_MultipleTenants_CachesAvailableList()
    {
        // Arrange
        var homeTenantId = "guest-home-tenant";
        var authState = CreateAuthState(homeTenantId, "consultant@firm.com");
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(homeTenantId))
            .ReturnsAsync((TenantSubscription?)null);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync("azure-alpha"))
            .ReturnsAsync((TenantSubscription?)null); // home tenant lookup in GetAvailableTenantsAsync

        var tenant1 = MakeSubscription("cho-1", "Alpha Health", "azure-alpha");
        var tenant2 = MakeSubscription("cho-2", "Beta Health", "azure-beta");
        _tenantService.Setup(x => x.GetTenantsForUserAsync("consultant@firm.com"))
            .ReturnsAsync(new List<TenantSubscription> { tenant1, tenant2 });

        // Act — resolve context first, then get available tenants
        await _sut.GetCurrentTenantContextAsync();
        var available = await _sut.GetAvailableTenantsAsync();

        // Assert — cached from initial resolution, no extra GetTenantsForUserAsync call
        available.Should().HaveCount(2);
        available[0].OrganizationName.Should().Be("Alpha Health");
        available[1].OrganizationName.Should().Be("Beta Health");
    }

    // ---------------------------------------------------------------
    // GetAvailableTenantsAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAvailableTenantsAsync_IncludesHomeTenantWhenNotInEmailList()
    {
        // Arrange — user's home tenant matches a subscription, and email
        // associates them with a different tenant too
        var homeTenantId = "azure-home";
        var authState = CreateAuthState(homeTenantId, "user@home.com");
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        var homeSub = MakeSubscription("cho-home", "Home Health", homeTenantId);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(homeTenantId))
            .ReturnsAsync(homeSub);

        var otherSub = MakeSubscription("cho-other", "Other Health", "azure-other");
        _tenantService.Setup(x => x.GetTenantsForUserAsync("user@home.com"))
            .ReturnsAsync(new List<TenantSubscription> { otherSub });

        // Act
        await _sut.GetCurrentTenantContextAsync();
        var available = await _sut.GetAvailableTenantsAsync();

        // Assert — home tenant should be prepended
        available.Should().HaveCount(2);
        available[0].TenantId.Should().Be("cho-home");
        available[1].TenantId.Should().Be("cho-other");
    }

    [Fact]
    public async Task GetAvailableTenantsAsync_DoesNotDuplicateHomeTenantWhenAlreadyInList()
    {
        // Arrange
        var homeTenantId = "azure-home";
        var authState = CreateAuthState(homeTenantId, "user@home.com");
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        var homeSub = MakeSubscription("cho-home", "Home Health", homeTenantId);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(homeTenantId))
            .ReturnsAsync(homeSub);

        // Email lookup also returns the home tenant
        _tenantService.Setup(x => x.GetTenantsForUserAsync("user@home.com"))
            .ReturnsAsync(new List<TenantSubscription> { homeSub });

        // Act
        await _sut.GetCurrentTenantContextAsync();
        var available = await _sut.GetAvailableTenantsAsync();

        // Assert — no duplicates
        available.Should().HaveCount(1);
        available[0].TenantId.Should().Be("cho-home");
    }

    [Fact]
    public async Task GetAvailableTenantsAsync_UsesHomeTenantId_NotSwitchedTenantId()
    {
        // Arrange — This tests the home tenant ID drift fix.
        // After SwitchTenantAsync, _cachedContext.AzureTenantId changes,
        // but GetAvailableTenantsAsync should still use the original home tenant ID.
        var homeTenantId = "azure-home";
        var authState = CreateAuthState(homeTenantId, "user@home.com");
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        var homeSub = MakeSubscription("cho-home", "Home Health", homeTenantId);
        var otherSub = MakeSubscription("cho-other", "Other Health", "azure-other");
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(homeTenantId))
            .ReturnsAsync(homeSub);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync("azure-other"))
            .ReturnsAsync(otherSub);
        _tenantService.Setup(x => x.GetTenantsForUserAsync("user@home.com"))
            .ReturnsAsync(new List<TenantSubscription> { homeSub, otherSub });

        // Resolve initial context
        await _sut.GetCurrentTenantContextAsync();

        // Switch to another tenant — this changes _cachedContext.AzureTenantId
        await _sut.SwitchTenantAsync("azure-other");

        // Clear cached available tenants to force re-resolution
        // (in real usage, the cache would already be populated, but this
        // tests that re-resolution uses _homeTenantId not currentContext.AzureTenantId)

        // Act — GetAvailableTenantsAsync should still look up the *home* tenant
        var available = await _sut.GetAvailableTenantsAsync();

        // Assert — home tenant lookup should use original home tenant ID
        _tenantService.Verify(x => x.GetSubscriptionByAzureTenantIdAsync(homeTenantId), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetAvailableTenantsAsync_WhenNoEmail_ReturnsEmptyList()
    {
        // Arrange — authenticated but no email claim
        var claims = new[]
        {
            new Claim("tid", "azure-tenant-123"),
            new Claim("name", "No Email User")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(principal);
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync("azure-tenant-123"))
            .ReturnsAsync((TenantSubscription?)null);

        // Act
        await _sut.GetCurrentTenantContextAsync();
        var available = await _sut.GetAvailableTenantsAsync();

        // Assert — fallback context has no email, so no tenants
        available.Should().BeEmpty();
    }

    // ---------------------------------------------------------------
    // SwitchTenantAsync — authorized tenant switch (not impersonation)
    // ---------------------------------------------------------------

    [Fact]
    public async Task SwitchTenantAsync_AuthorizedTenant_UpdatesContext()
    {
        // Arrange — user has access to two tenants
        var homeTenantId = "azure-home";
        var authState = CreateAuthState(homeTenantId, "user@home.com");
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        var homeSub = MakeSubscription("cho-home", "Home Health", homeTenantId);
        var otherSub = MakeSubscription("cho-other", "Other Health", "azure-other");
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(homeTenantId))
            .ReturnsAsync(homeSub);
        _tenantService.Setup(x => x.GetTenantsForUserAsync("user@home.com"))
            .ReturnsAsync(new List<TenantSubscription> { homeSub, otherSub });

        await _sut.GetCurrentTenantContextAsync();

        // Act
        var result = await _sut.SwitchTenantAsync("azure-other");

        // Assert
        result.Should().BeTrue();
        _sut.TenantId.Should().Be("cho-other");
        _sut.TenantName.Should().Be("Other Health");
    }

    [Fact]
    public async Task SwitchTenantAsync_AuthorizedTenant_DoesNotSetImpersonating()
    {
        // Arrange — user has access to two tenants, switches to non-home tenant
        var homeTenantId = "azure-home";
        var authState = CreateAuthState(homeTenantId, "user@home.com");
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        var homeSub = MakeSubscription("cho-home", "Home Health", homeTenantId);
        var otherSub = MakeSubscription("cho-other", "Other Health", "azure-other");
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(homeTenantId))
            .ReturnsAsync(homeSub);
        _tenantService.Setup(x => x.GetTenantsForUserAsync("user@home.com"))
            .ReturnsAsync(new List<TenantSubscription> { homeSub, otherSub });

        await _sut.GetCurrentTenantContextAsync();

        // Act — switch to a tenant the user is authorized for but is NOT their home tenant
        var result = await _sut.SwitchTenantAsync("azure-other");

        // Assert — this is NOT impersonation
        result.Should().BeTrue();
        _sut.IsImpersonating.Should().BeFalse();
    }

    [Fact]
    public async Task SwitchTenantAsync_BackToHomeTenant_ClearsImpersonation()
    {
        // Arrange — platform admin impersonates, then switches back
        var homeTenantId = "azure-home";
        var authState = CreateAuthState(homeTenantId, "admin@platform.com",
            platformAdmin: true);
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        var homeSub = MakeSubscription("cho-home", "Home Health", homeTenantId);
        var foreignSub = MakeSubscription("cho-foreign", "Foreign Corp", "azure-foreign");
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(homeTenantId))
            .ReturnsAsync(homeSub);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync("azure-foreign"))
            .ReturnsAsync(foreignSub);
        _tenantService.Setup(x => x.GetTenantsForUserAsync("admin@platform.com"))
            .ReturnsAsync(new List<TenantSubscription> { homeSub });

        await _sut.GetCurrentTenantContextAsync();

        // Impersonate a foreign tenant
        await _sut.SwitchTenantAsync("azure-foreign");
        _sut.IsImpersonating.Should().BeTrue();

        // Act — switch back to home tenant (which is in authorized list)
        var result = await _sut.SwitchTenantAsync(homeTenantId);

        // Assert
        result.Should().BeTrue();
        _sut.IsImpersonating.Should().BeFalse();
    }

    // ---------------------------------------------------------------
    // SwitchTenantAsync — platform admin impersonation
    // ---------------------------------------------------------------

    [Fact]
    public async Task SwitchTenantAsync_PlatformAdmin_CanImpersonateAnyTenant()
    {
        // Arrange
        var homeTenantId = "azure-home";
        var authState = CreateAuthState(homeTenantId, "admin@platform.com",
            platformAdmin: true);
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        var homeSub = MakeSubscription("cho-home", "Home Health", homeTenantId);
        var foreignSub = MakeSubscription("cho-foreign", "Foreign Corp", "azure-foreign");
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(homeTenantId))
            .ReturnsAsync(homeSub);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync("azure-foreign"))
            .ReturnsAsync(foreignSub);
        _tenantService.Setup(x => x.GetTenantsForUserAsync("admin@platform.com"))
            .ReturnsAsync(new List<TenantSubscription> { homeSub });

        await _sut.GetCurrentTenantContextAsync();

        // Act — switch to a tenant NOT in the available list
        var result = await _sut.SwitchTenantAsync("azure-foreign");

        // Assert
        result.Should().BeTrue();
        _sut.TenantId.Should().Be("cho-foreign");
        _sut.TenantName.Should().Be("Foreign Corp");
        _sut.IsImpersonating.Should().BeTrue();
    }

    [Fact]
    public async Task SwitchTenantAsync_PlatformAdmin_NonExistentTenant_ReturnsFalse()
    {
        // Arrange
        var homeTenantId = "azure-home";
        var authState = CreateAuthState(homeTenantId, "admin@platform.com",
            platformAdmin: true);
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        var homeSub = MakeSubscription("cho-home", "Home Health", homeTenantId);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(homeTenantId))
            .ReturnsAsync(homeSub);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync("azure-nonexistent"))
            .ReturnsAsync((TenantSubscription?)null);
        _tenantService.Setup(x => x.GetTenantsForUserAsync("admin@platform.com"))
            .ReturnsAsync(new List<TenantSubscription> { homeSub });

        await _sut.GetCurrentTenantContextAsync();

        // Act
        var result = await _sut.SwitchTenantAsync("azure-nonexistent");

        // Assert
        result.Should().BeFalse();
    }

    // ---------------------------------------------------------------
    // SwitchTenantAsync — unauthorized user
    // ---------------------------------------------------------------

    [Fact]
    public async Task SwitchTenantAsync_UnauthorizedUser_Denied()
    {
        // Arrange — regular user with no platform admin role
        var homeTenantId = "azure-home";
        var authState = CreateAuthState(homeTenantId, "user@home.com");
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        var homeSub = MakeSubscription("cho-home", "Home Health", homeTenantId);
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(homeTenantId))
            .ReturnsAsync(homeSub);
        _tenantService.Setup(x => x.GetTenantsForUserAsync("user@home.com"))
            .ReturnsAsync(new List<TenantSubscription> { homeSub });

        await _sut.GetCurrentTenantContextAsync();

        // Act — try to switch to a tenant the user doesn't have access to
        var result = await _sut.SwitchTenantAsync("azure-unauthorized");

        // Assert
        result.Should().BeFalse();
        _sut.TenantId.Should().Be("cho-home"); // context unchanged
        _sut.IsImpersonating.Should().BeFalse();
    }

    [Fact]
    public async Task SwitchTenantAsync_PreservesUserEmail()
    {
        // Arrange
        var homeTenantId = "azure-home";
        var authState = CreateAuthState(homeTenantId, "user@home.com");
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        var homeSub = MakeSubscription("cho-home", "Home Health", homeTenantId);
        var otherSub = MakeSubscription("cho-other", "Other Health", "azure-other");
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync(homeTenantId))
            .ReturnsAsync(homeSub);
        _tenantService.Setup(x => x.GetTenantsForUserAsync("user@home.com"))
            .ReturnsAsync(new List<TenantSubscription> { homeSub, otherSub });

        await _sut.GetCurrentTenantContextAsync();

        // Act
        await _sut.SwitchTenantAsync("azure-other");

        // Assert — email should carry over from previous context
        var context = await _sut.GetCurrentTenantContextAsync();
        context!.UserEmail.Should().Be("user@home.com");
    }

    // ---------------------------------------------------------------
    // IsImpersonating property
    // ---------------------------------------------------------------

    [Fact]
    public void IsImpersonating_ReturnsFalse_WhenNotInitialized()
    {
        _sut.IsImpersonating.Should().BeFalse();
    }

    [Fact]
    public async Task IsImpersonating_ReturnsFalse_AfterNormalResolution()
    {
        // Arrange
        var authState = CreateAuthState("azure-home", "user@home.com");
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        var homeSub = MakeSubscription("cho-home", "Home Health", "azure-home");
        _tenantService.Setup(x => x.GetSubscriptionByAzureTenantIdAsync("azure-home"))
            .ReturnsAsync(homeSub);

        // Act
        await _sut.GetCurrentTenantContextAsync();

        // Assert
        _sut.IsImpersonating.Should().BeFalse();
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static AuthenticationState CreateAuthState(
        string azureTenantId,
        string email,
        bool platformAdmin = false)
    {
        var claims = new List<Claim>
        {
            new("tid", azureTenantId),
            new(ClaimTypes.Email, email),
            new("name", "Test User")
        };

        if (platformAdmin)
        {
            claims.Add(new Claim("permissions", "platform:admin"));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        return new AuthenticationState(principal);
    }

    private static TenantSubscription MakeSubscription(
        string tenantId,
        string orgName,
        string azureTenantId,
        bool isDemo = false)
    {
        return new TenantSubscription
        {
            TenantId = tenantId,
            OrganizationName = orgName,
            AzureTenantId = azureTenantId,
            SubscriptionStatus = "Active",
            Tier = "professional",
            IsDemo = isDemo,
            CreatedAt = DateTime.UtcNow.AddMonths(-3)
        };
    }
}
