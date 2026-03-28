using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PremiumBillingService.Middleware;

namespace PremiumBillingService.Tests.Middleware;

public class TenantMiddlewareTests
{
    private readonly Mock<ILogger<TenantMiddleware>> _logger;

    public TenantMiddlewareTests()
    {
        _logger = new Mock<ILogger<TenantMiddleware>>();
    }

    private TenantMiddleware CreateMiddleware(RequestDelegate next)
    {
        return new TenantMiddleware(next, _logger.Object);
    }

    [Fact]
    public async Task InvokeAsync_HealthCheckPath_SkipsTenantExtraction()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = "/health";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Items.Should().NotContainKey("TenantId");
    }

    [Fact]
    public async Task InvokeAsync_SwaggerPath_SkipsTenantExtraction()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = "/swagger/index.html";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Items.Should().NotContainKey("TenantId");
    }

    [Fact]
    public async Task InvokeAsync_AuthenticatedWithTenantIdClaim_ExtractsTenantId()
    {
        string? capturedTenantId = null;
        var middleware = CreateMiddleware(ctx =>
        {
            capturedTenantId = ctx.Items["TenantId"]?.ToString();
            return Task.CompletedTask;
        });

        var claims = new List<Claim> { new("tenant_id", "tenant-123") };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(identity);
        context.Request.Path = "/api/v1/billing-runs";

        await middleware.InvokeAsync(context);

        capturedTenantId.Should().Be("tenant-123");
    }

    [Fact]
    public async Task InvokeAsync_AuthenticatedWithExtensionTenantIdClaim_ExtractsTenantId()
    {
        string? capturedTenantId = null;
        var middleware = CreateMiddleware(ctx =>
        {
            capturedTenantId = ctx.Items["TenantId"]?.ToString();
            return Task.CompletedTask;
        });

        var claims = new List<Claim> { new("extension_TenantId", "tenant-456") };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(identity);
        context.Request.Path = "/api/v1/billing-runs";

        await middleware.InvokeAsync(context);

        capturedTenantId.Should().Be("tenant-456");
    }

    [Fact]
    public async Task InvokeAsync_XTenantIDHeader_ExtractsTenantId()
    {
        string? capturedTenantId = null;
        var middleware = CreateMiddleware(ctx =>
        {
            capturedTenantId = ctx.Items["TenantId"]?.ToString();
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/billing-runs";
        context.Request.Headers["X-Tenant-ID"] = "tenant-header";

        await middleware.InvokeAsync(context);

        capturedTenantId.Should().Be("tenant-header");
    }

    [Fact]
    public async Task InvokeAsync_XDevTenantIDHeader_ExtractsTenantId()
    {
        string? capturedTenantId = null;
        var middleware = CreateMiddleware(ctx =>
        {
            capturedTenantId = ctx.Items["TenantId"]?.ToString();
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/billing-runs";
        context.Request.Headers["X-Dev-Tenant-ID"] = "dev-tenant";

        await middleware.InvokeAsync(context);

        capturedTenantId.Should().Be("dev-tenant");
    }

    [Fact]
    public async Task InvokeAsync_NoTenantInfo_DefaultsTenantId()
    {
        string? capturedTenantId = null;
        var middleware = CreateMiddleware(ctx =>
        {
            capturedTenantId = ctx.Items["TenantId"]?.ToString();
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/billing-runs";

        await middleware.InvokeAsync(context);

        capturedTenantId.Should().Be("default-tenant");
    }

    [Fact]
    public async Task InvokeAsync_JwtTakesPrecedenceOverHeader()
    {
        string? capturedTenantId = null;
        var middleware = CreateMiddleware(ctx =>
        {
            capturedTenantId = ctx.Items["TenantId"]?.ToString();
            return Task.CompletedTask;
        });

        var claims = new List<Claim> { new("tenant_id", "jwt-tenant") };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(identity);
        context.Request.Path = "/api/v1/billing-runs";
        context.Request.Headers["X-Tenant-ID"] = "header-tenant";

        await middleware.InvokeAsync(context);

        capturedTenantId.Should().Be("jwt-tenant");
    }

    [Fact]
    public async Task InvokeAsync_CallsNextMiddleware()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/billing-runs";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }
}
