using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared;
using MudBlazor;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Shared;

public class PermissionGateTests : TestContext
{
    private readonly Mock<IUserContextService> _userContextService = new();

    public PermissionGateTests()
    {
        Services.AddSingleton(_userContextService.Object);
        Services.AddMudServices();
        // Register MudBlazor's internal services needed by MudPopoverProvider
        JSInterop.SetupVoid("mudPopover.initialize", _ => true);
        JSInterop.SetupVoid("mudKeyInterceptor.connect", _ => true);
        JSInterop.SetupVoid("mudElementReference.saveFocus", _ => true);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private UserContext CreateUserWithPermissions(params string[] permissions)
    {
        return new UserContext
        {
            UserId = "user-1",
            Email = "user@test.com",
            DisplayName = "Test User",
            TenantId = "tenant-1",
            Roles = new List<string> { "ClaimsExaminer" },
            Permissions = new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase)
        };
    }

    [Fact]
    public void RendersChildContent_WhenUserHasRequiredPermission()
    {
        var user = CreateUserWithPermissions("claims:read", "claims:write");
        _userContextService.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(user);
        _userContextService.Setup(s => s.HasPermission("claims:read")).Returns(true);

        var cut = RenderComponent<PermissionGate>(parameters => parameters
            .Add(p => p.Permission, "claims:read")
            .AddChildContent("<p>Authorized content</p>"));

        cut.Markup.Should().Contain("Authorized content");
        cut.Markup.Should().NotContain("don't have access");
    }

    [Fact]
    public void ShowsAccessDeniedAlert_WhenUserLacksPermission()
    {
        var user = CreateUserWithPermissions("members:read");
        _userContextService.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(user);
        _userContextService.Setup(s => s.HasPermission("claims:read")).Returns(false);

        var cut = RenderComponent<PermissionGate>(parameters => parameters
            .Add(p => p.Permission, "claims:read")
            .AddChildContent("<p>Secret content</p>"));

        cut.Markup.Should().NotContain("Secret content");
        cut.Markup.Should().Contain("don't have access");
    }

    [Fact]
    public void ShowsAccessDenied_WhenUserIsNotAuthenticated()
    {
        _userContextService.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync((UserContext?)null);

        var cut = RenderComponent<PermissionGate>(parameters => parameters
            .Add(p => p.Permission, "claims:read")
            .AddChildContent("<p>Secret</p>"));

        cut.Markup.Should().NotContain("Secret");
        // Unauthenticated user: userContext is null, _hasAccess stays false, _loading becomes false
        cut.Markup.Should().Contain("don't have access");
    }

    [Fact]
    public void HandlesCommaSeparatedPermissions_AnyMatchGrantsAccess()
    {
        var user = CreateUserWithPermissions("reports:read");
        _userContextService.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(user);
        _userContextService.Setup(s => s.HasPermission("claims:read")).Returns(false);
        _userContextService.Setup(s => s.HasPermission("reports:read")).Returns(true);

        var cut = RenderComponent<PermissionGate>(parameters => parameters
            .Add(p => p.Permission, "claims:read, reports:read")
            .AddChildContent("<p>Visible</p>"));

        cut.Markup.Should().Contain("Visible");
    }

    [Fact]
    public void AuthenticatedUserWithRoles_NoPermissionParameter_GetsAccess()
    {
        var user = new UserContext
        {
            UserId = "user-1",
            Email = "user@test.com",
            DisplayName = "Test User",
            TenantId = "tenant-1",
            Roles = new List<string> { "ClaimsExaminer" },
            Permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };
        _userContextService.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(user);

        var cut = RenderComponent<PermissionGate>(parameters => parameters
            .Add(p => p.Permission, "")
            .AddChildContent("<p>Dashboard content</p>"));

        cut.Markup.Should().Contain("Dashboard content");
    }

    [Fact]
    public void AuthenticatedUserWithNoRoles_NoPermissionParameter_Denied()
    {
        var user = new UserContext
        {
            UserId = "user-1",
            Email = "user@test.com",
            DisplayName = "Test User",
            TenantId = "tenant-1",
            Roles = new List<string>(),
            Permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };
        _userContextService.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(user);

        var cut = RenderComponent<PermissionGate>(parameters => parameters
            .Add(p => p.Permission, "")
            .AddChildContent("<p>Secret</p>"));

        cut.Markup.Should().NotContain("Secret");
        cut.Markup.Should().Contain("don't have access");
    }

    [Fact]
    public void AccessDeniedMessage_ShowsRoleName_WhenProvided()
    {
        var user = CreateUserWithPermissions();
        _userContextService.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(user);
        _userContextService.Setup(s => s.HasPermission(It.IsAny<string>())).Returns(false);

        var cut = RenderComponent<PermissionGate>(parameters => parameters
            .Add(p => p.Permission, "claims:read")
            .Add(p => p.RoleName, "Claims Examiner")
            .AddChildContent("<p>Secret</p>"));

        cut.Markup.Should().Contain("Claims Examiner");
    }

    [Fact]
    public void AccessDeniedMessage_ShowsPermission_WhenNoRoleName()
    {
        var user = CreateUserWithPermissions();
        _userContextService.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync(user);
        _userContextService.Setup(s => s.HasPermission(It.IsAny<string>())).Returns(false);

        var cut = RenderComponent<PermissionGate>(parameters => parameters
            .Add(p => p.Permission, "claims:read")
            .AddChildContent("<p>Secret</p>"));

        cut.Markup.Should().Contain("claims:read");
    }

    [Fact]
    public void AccessDenied_ShowsBackToDashboardButton()
    {
        _userContextService.Setup(s => s.GetCurrentUserAsync()).ReturnsAsync((UserContext?)null);

        var cut = RenderComponent<PermissionGate>(parameters => parameters
            .Add(p => p.Permission, "claims:read")
            .AddChildContent("<p>Secret</p>"));

        cut.Markup.Should().Contain("Back to Dashboard");
        cut.Markup.Should().Contain("/dashboard");
    }
}
