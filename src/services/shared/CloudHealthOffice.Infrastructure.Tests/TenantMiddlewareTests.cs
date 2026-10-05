using System.Security.Claims;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests;

/// <summary>
/// The tenant comes from the validated token only (review item P6 / §8.10).
/// Several of these cases passed the opposite way before the fix: a bare
/// X-Tenant-ID or X-Dev-Tenant-ID header selected the tenant, a header beat a
/// token-less principal, and a request with neither landed in "default-tenant".
/// </summary>
public class TenantMiddlewareTests
{
    private static TenantMiddleware CreateMiddleware(RequestDelegate next, TenantMiddlewareOptions? options = null)
        => new(next, NullLogger<TenantMiddleware>.Instance, options ?? new TenantMiddlewareOptions());

    private static DefaultHttpContext Authenticated(params Claim[] claims)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        return context;
    }

    private static async Task<StandardErrorResponse?> ReadError(HttpContext context)
    {
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return JsonSerializer.Deserialize<StandardErrorResponse>(body,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    [Fact]
    public async Task TokenTenantClaim_SetsTenant()
    {
        string? captured = null;
        var middleware = CreateMiddleware(ctx => { captured = ctx.Items["TenantId"] as string; return Task.CompletedTask; });

        await middleware.InvokeAsync(Authenticated(new Claim("tenant_id", "tenant-a")));

        captured.Should().Be("tenant-a");
    }

    [Fact]
    public async Task HeaderMatchingToken_IsAccepted()
    {
        string? captured = null;
        var middleware = CreateMiddleware(ctx => { captured = ctx.Items["TenantId"] as string; return Task.CompletedTask; });
        var context = Authenticated(new Claim("tenant_id", "tenant-a"));
        context.Request.Headers["X-Tenant-ID"] = "tenant-a";

        await middleware.InvokeAsync(context);

        captured.Should().Be("tenant-a");
    }

    [Fact]
    public async Task HeaderContradictingToken_Returns403_AndNeverReachesNext()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = Authenticated(new Claim("tenant_id", "tenant-a"));
        context.Request.Headers["X-Tenant-ID"] = "tenant-b";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(403);
        (await ReadError(context))!.Code.Should().Be("TENANT_CONTEXT_CONFLICT");
    }

    [Fact]
    public async Task AuthenticatedWithoutTenantClaim_HeaderDoesNotSupplyIt_Returns401()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = Authenticated(new Claim("sub", "someone"));
        context.Request.Headers["X-Tenant-ID"] = "tenant-b";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(401);
        (await ReadError(context))!.Code.Should().Be("TENANT_CONTEXT_MISSING");
    }

    [Fact]
    public async Task DevTenantHeader_IsNeverHonoured()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = Authenticated(new Claim("sub", "someone"));
        context.Request.Headers["X-Dev-Tenant-ID"] = "dev-tenant";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Unauthenticated_HeaderOnly_DoesNotEstablishTenant()
    {
        string? captured = "unset";
        var middleware = CreateMiddleware(ctx => { captured = ctx.Items["TenantId"] as string; return Task.CompletedTask; });
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-ID"] = "tenant-b";

        await middleware.InvokeAsync(context);

        // Passed on so authorization can challenge; with no tenant and no default.
        captured.Should().BeNull();
    }

    [Fact]
    public void Options_HaveNoDefaultTenant()
    {
        typeof(TenantMiddlewareOptions).GetProperty("DefaultTenantId").Should().BeNull();
        typeof(TenantMiddlewareOptions).GetProperty("RequireTenantId").Should().BeNull();
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    [InlineData("/ready")]
    [InlineData("/live")]
    [InlineData("/metrics")]
    public async Task PassthroughPaths_SkipTenantResolution(string path)
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = Authenticated(new Claim("sub", "probe"));
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Swagger_IsNoLongerAPassthroughPath()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = Authenticated(new Claim("sub", "someone"));
        context.Request.Path = "/swagger/v1/swagger.json";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ExtensionTenantClaim_IsAccepted()
    {
        string? captured = null;
        var middleware = CreateMiddleware(ctx => { captured = ctx.Items["TenantId"] as string; return Task.CompletedTask; });

        await middleware.InvokeAsync(Authenticated(new Claim("extension_TenantId", "ext-tenant")));

        captured.Should().Be("ext-tenant");
    }
}
