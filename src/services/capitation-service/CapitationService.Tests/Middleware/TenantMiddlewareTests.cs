using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using CapitationService.Middleware;

namespace CapitationService.Tests.Middleware;

public class TenantMiddlewareTests
{
    private readonly Mock<ILogger<TenantMiddleware>> _logger;

    public TenantMiddlewareTests()
    {
        _logger = new Mock<ILogger<TenantMiddleware>>();
    }

    private TenantMiddleware CreateMiddleware(RequestDelegate? next = null)
    {
        next ??= _ => Task.CompletedTask;
        return new TenantMiddleware(next, _logger.Object);
    }

    private static DefaultHttpContext CreateContext(
        string path = "/api/test",
        string? xTenantHeader = null,
        string? xDevTenantHeader = null,
        ClaimsPrincipal? user = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (xTenantHeader != null)
            context.Request.Headers["X-Tenant-ID"] = xTenantHeader;
        if (xDevTenantHeader != null)
            context.Request.Headers["X-Dev-Tenant-ID"] = xDevTenantHeader;
        if (user != null)
            context.User = user;
        return context;
    }

    private static ClaimsPrincipal CreateAuthenticatedUser(string? tenantClaim = null, string? claimType = "tenant_id")
    {
        var claims = new List<Claim>();
        if (tenantClaim != null && claimType != null)
            claims.Add(new Claim(claimType, tenantClaim));

        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    #region Health Check Bypass

    [Fact]
    public async Task InvokeAsync_HealthPath_SkipsTenantExtraction()
    {
        bool nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateContext("/health/live");

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Items.Should().NotContainKey("TenantId");
    }

    [Fact]
    public async Task InvokeAsync_SwaggerPath_SkipsTenantExtraction()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext("/swagger/v1/swagger.json");

        await middleware.InvokeAsync(context);

        context.Items.Should().NotContainKey("TenantId");
    }

    #endregion

    #region JWT Claims Extraction

    [Fact]
    public async Task InvokeAsync_JwtTenantIdClaim_ExtractsTenantId()
    {
        var middleware = CreateMiddleware();
        var user = CreateAuthenticatedUser("tenant-from-jwt", "tenant_id");
        var context = CreateContext(user: user);

        await middleware.InvokeAsync(context);

        context.Items["TenantId"].Should().Be("tenant-from-jwt");
    }

    [Fact]
    public async Task InvokeAsync_JwtExtensionTenantId_ExtractsTenantId()
    {
        var middleware = CreateMiddleware();
        var user = CreateAuthenticatedUser("tenant-ext", "extension_TenantId");
        var context = CreateContext(user: user);

        await middleware.InvokeAsync(context);

        context.Items["TenantId"].Should().Be("tenant-ext");
    }

    #endregion

    #region Header Fallback

    [Fact]
    public async Task InvokeAsync_XTenantIDHeader_ExtractsTenantId()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext(xTenantHeader: "tenant-from-header");

        await middleware.InvokeAsync(context);

        context.Items["TenantId"].Should().Be("tenant-from-header");
    }

    [Fact]
    public async Task InvokeAsync_XDevTenantIDHeader_ExtractsTenantId()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext(xDevTenantHeader: "dev-tenant-123");

        await middleware.InvokeAsync(context);

        context.Items["TenantId"].Should().Be("dev-tenant-123");
    }

    [Fact]
    public async Task InvokeAsync_XTenantIDTakesPrecedenceOverDevHeader()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext(xTenantHeader: "prod-tenant", xDevTenantHeader: "dev-tenant");

        await middleware.InvokeAsync(context);

        context.Items["TenantId"].Should().Be("prod-tenant");
    }

    [Fact]
    public async Task InvokeAsync_JwtTakesPrecedenceOverHeaders()
    {
        var middleware = CreateMiddleware();
        var user = CreateAuthenticatedUser("jwt-tenant");
        var context = CreateContext(xTenantHeader: "header-tenant", user: user);

        await middleware.InvokeAsync(context);

        context.Items["TenantId"].Should().Be("jwt-tenant");
    }

    #endregion

    #region Default Fallback

    [Fact]
    public async Task InvokeAsync_NoTenantInfo_DefaultsToDefaultTenant()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext();

        await middleware.InvokeAsync(context);

        context.Items["TenantId"].Should().Be("default-tenant");
    }

    #endregion

    #region Next Delegate

    [Fact]
    public async Task InvokeAsync_CallsNextDelegate()
    {
        bool nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateContext(xTenantHeader: "test-tenant");

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    #endregion
}
