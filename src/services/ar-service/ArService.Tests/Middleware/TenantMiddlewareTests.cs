using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ArService.Middleware;

namespace ArService.Tests.Middleware;

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

    #region JWT Claims

    [Fact]
    public async Task InvokeAsync_TenantIdFromJwtClaim_SetsTenantId()
    {
        var middleware = CreateMiddleware();
        var context = new DefaultHttpContext();
        var claims = new[] { new Claim("tenant_id", "jwt-tenant-123") };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        await middleware.InvokeAsync(context);

        context.Items["TenantId"].Should().Be("jwt-tenant-123");
    }

    [Fact]
    public async Task InvokeAsync_ExtensionTenantIdFromJwtClaim_SetsTenantId()
    {
        var middleware = CreateMiddleware();
        var context = new DefaultHttpContext();
        var claims = new[] { new Claim("extension_TenantId", "ext-tenant-456") };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        await middleware.InvokeAsync(context);

        context.Items["TenantId"].Should().Be("ext-tenant-456");
    }

    [Fact]
    public async Task InvokeAsync_TenantIdClaimTakesPrecedenceOverExtension()
    {
        var middleware = CreateMiddleware();
        var context = new DefaultHttpContext();
        var claims = new[]
        {
            new Claim("tenant_id", "primary-tenant"),
            new Claim("extension_TenantId", "extension-tenant")
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        await middleware.InvokeAsync(context);

        context.Items["TenantId"].Should().Be("primary-tenant");
    }

    #endregion

    #region Headers

    [Fact]
    public async Task InvokeAsync_TenantIdFromXTenantIDHeader_SetsTenantId()
    {
        var middleware = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-ID"] = "header-tenant-789";

        await middleware.InvokeAsync(context);

        context.Items["TenantId"].Should().Be("header-tenant-789");
    }

    [Fact]
    public async Task InvokeAsync_TenantIdFromXDevTenantIDHeader_SetsTenantId()
    {
        var middleware = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Dev-Tenant-ID"] = "dev-tenant-abc";

        await middleware.InvokeAsync(context);

        context.Items["TenantId"].Should().Be("dev-tenant-abc");
    }

    [Fact]
    public async Task InvokeAsync_XTenantIDTakesPrecedenceOverXDevTenantID()
    {
        var middleware = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-ID"] = "prod-tenant";
        context.Request.Headers["X-Dev-Tenant-ID"] = "dev-tenant";

        await middleware.InvokeAsync(context);

        context.Items["TenantId"].Should().Be("prod-tenant");
    }

    [Fact]
    public async Task InvokeAsync_JwtTakesPrecedenceOverHeaders()
    {
        var middleware = CreateMiddleware();
        var context = new DefaultHttpContext();
        var claims = new[] { new Claim("tenant_id", "jwt-tenant") };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        context.Request.Headers["X-Tenant-ID"] = "header-tenant";

        await middleware.InvokeAsync(context);

        context.Items["TenantId"].Should().Be("jwt-tenant");
    }

    #endregion

    #region Default Fallback

    [Fact]
    public async Task InvokeAsync_NoTenantSource_UsesDefaultTenant()
    {
        var middleware = CreateMiddleware();
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        context.Items["TenantId"].Should().Be("default-tenant");
    }

    [Fact]
    public async Task InvokeAsync_UnauthenticatedUserNoHeaders_UsesDefaultTenant()
    {
        var middleware = CreateMiddleware();
        var context = new DefaultHttpContext();
        // User is not authenticated (default)
        context.User = new ClaimsPrincipal(new ClaimsIdentity());

        await middleware.InvokeAsync(context);

        context.Items["TenantId"].Should().Be("default-tenant");
    }

    #endregion

    #region Health Check & Swagger Bypass

    [Fact]
    public async Task InvokeAsync_HealthCheckPath_SkipsTenantExtraction()
    {
        var middleware = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Path = "/health";

        await middleware.InvokeAsync(context);

        context.Items.Should().NotContainKey("TenantId");
    }

    [Fact]
    public async Task InvokeAsync_HealthCheckSubPath_SkipsTenantExtraction()
    {
        var middleware = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Path = "/health/ready";

        await middleware.InvokeAsync(context);

        context.Items.Should().NotContainKey("TenantId");
    }

    [Fact]
    public async Task InvokeAsync_SwaggerPath_SkipsTenantExtraction()
    {
        var middleware = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Path = "/swagger";

        await middleware.InvokeAsync(context);

        context.Items.Should().NotContainKey("TenantId");
    }

    [Fact]
    public async Task InvokeAsync_SwaggerSubPath_SkipsTenantExtraction()
    {
        var middleware = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Path = "/swagger/v1/swagger.json";

        await middleware.InvokeAsync(context);

        context.Items.Should().NotContainKey("TenantId");
    }

    #endregion

    #region Next Delegate

    [Fact]
    public async Task InvokeAsync_CallsNextDelegate()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_HealthCheckPath_StillCallsNextDelegate()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext();
        context.Request.Path = "/health";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    #endregion
}
