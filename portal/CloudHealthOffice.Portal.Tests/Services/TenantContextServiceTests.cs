using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class TenantContextServiceTests
{
    private readonly Mock<AuthenticationStateProvider> _authStateProvider;
    private readonly Mock<ITenantService> _tenantService;
    private readonly Mock<ILogger<TenantContextService>> _logger;
    private readonly TenantContextService _sut;

    public TenantContextServiceTests()
    {
        _authStateProvider = new Mock<AuthenticationStateProvider>();
        _tenantService = new Mock<ITenantService>();
        _logger = new Mock<ILogger<TenantContextService>>();
        _sut = new TenantContextService(_authStateProvider.Object, _tenantService.Object, _logger.Object);
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
    public async Task GetCurrentTenantContextAsync_WhenSubscriptionNotFound_ReturnsNull()
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

        // Assert
        result.Should().BeNull();
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
}
