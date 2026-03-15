using System.Security.Claims;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests;

public class TenantMiddlewareTests
{
    private readonly ILogger<TenantMiddleware> _logger = NullLogger<TenantMiddleware>.Instance;

    private static TenantMiddleware CreateMiddleware(
        RequestDelegate next,
        TenantMiddlewareOptions? options = null,
        ILogger<TenantMiddleware>? logger = null)
    {
        return new TenantMiddleware(
            next,
            logger ?? NullLogger<TenantMiddleware>.Instance,
            options ?? new TenantMiddlewareOptions());
    }

    [Fact]
    public async Task InvokeAsync_WithXTenantIdHeader_SetsTenantInContext()
    {
        // Arrange
        string? capturedTenantId = null;
        var middleware = CreateMiddleware(ctx =>
        {
            capturedTenantId = ctx.Items["TenantId"]?.ToString();
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-ID"] = "tenant-123";

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        capturedTenantId.Should().Be("tenant-123");
    }

    [Fact]
    public async Task InvokeAsync_WithDevTenantIdHeader_SetsTenantInContext()
    {
        string? capturedTenantId = null;
        var middleware = CreateMiddleware(ctx =>
        {
            capturedTenantId = ctx.Items["TenantId"]?.ToString();
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Headers["X-Dev-Tenant-ID"] = "dev-tenant-456";

        await middleware.InvokeAsync(context);

        capturedTenantId.Should().Be("dev-tenant-456");
    }

    [Fact]
    public async Task InvokeAsync_WithJwtTenantClaim_PreferClaimOverHeader()
    {
        string? capturedTenantId = null;
        var middleware = CreateMiddleware(ctx =>
        {
            capturedTenantId = ctx.Items["TenantId"]?.ToString();
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-ID"] = "header-tenant";
        var claims = new[] { new Claim("tenant_id", "jwt-tenant") };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        await middleware.InvokeAsync(context);

        capturedTenantId.Should().Be("jwt-tenant");
    }

    [Fact]
    public async Task InvokeAsync_WithExtensionTenantIdClaim_ExtractsTenant()
    {
        string? capturedTenantId = null;
        var middleware = CreateMiddleware(ctx =>
        {
            capturedTenantId = ctx.Items["TenantId"]?.ToString();
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        var claims = new[] { new Claim("extension_TenantId", "ext-tenant") };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        await middleware.InvokeAsync(context);

        capturedTenantId.Should().Be("ext-tenant");
    }

    [Fact]
    public async Task InvokeAsync_LenientMode_NoTenant_UsesDefault()
    {
        string? capturedTenantId = null;
        var options = new TenantMiddlewareOptions { RequireTenantId = false, DefaultTenantId = "my-default" };
        var middleware = CreateMiddleware(ctx =>
        {
            capturedTenantId = ctx.Items["TenantId"]?.ToString();
            return Task.CompletedTask;
        }, options);

        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        capturedTenantId.Should().Be("my-default");
    }

    [Fact]
    public async Task InvokeAsync_StrictMode_NoTenant_Returns401WithJsonError()
    {
        var nextCalled = false;
        var options = new TenantMiddlewareOptions { RequireTenantId = true };
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, options);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(401);
        context.Response.ContentType.Should().Be("application/json");

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var error = JsonSerializer.Deserialize<StandardErrorResponse>(body,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        error.Should().NotBeNull();
        error!.Code.Should().Be("TENANT_CONTEXT_MISSING");
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/ready")]
    [InlineData("/live")]
    [InlineData("/swagger")]
    public async Task InvokeAsync_PassthroughPaths_SkipsTenantResolution(string path)
    {
        var nextCalled = false;
        var options = new TenantMiddlewareOptions { RequireTenantId = true };
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, options);

        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task InvokeAsync_CustomPassthroughPaths_AreRespected()
    {
        var nextCalled = false;
        var options = new TenantMiddlewareOptions
        {
            RequireTenantId = true,
            PassthroughPaths = ["/custom-health"]
        };
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, options);

        var context = new DefaultHttpContext();
        context.Request.Path = "/custom-health";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_HeaderPrecedence_XTenantIdBeforeXDevTenantId()
    {
        string? capturedTenantId = null;
        var middleware = CreateMiddleware(ctx =>
        {
            capturedTenantId = ctx.Items["TenantId"]?.ToString();
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-ID"] = "primary";
        context.Request.Headers["X-Dev-Tenant-ID"] = "dev-fallback";

        await middleware.InvokeAsync(context);

        capturedTenantId.Should().Be("primary");
    }
}
