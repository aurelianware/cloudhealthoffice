using Microsoft.AspNetCore.Http;
using MemberService.Middleware;

namespace MemberService.Tests.Middleware;

public class TenantMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WithXTenantIdHeader_SetsTenantContext()
    {
        // Arrange
        var tenantId = "tenant-123";
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-ID"] = tenantId;

        var nextCalled = false;
        RequestDelegate next = (HttpContext ctx) =>
        {
            nextCalled = true;
            ctx.Items["TenantId"].Should().Be(tenantId);
            return Task.CompletedTask;
        };

        var middleware = new TenantMiddleware(next);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.Should().BeTrue();
        context.Items.Should().ContainKey("TenantId");
        context.Items["TenantId"].Should().Be(tenantId);
    }


    [Fact]
    public async Task InvokeAsync_PreservesOtherHttpContextItems()
    {
        // Arrange
        var tenantId = "tenant-123";
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-ID"] = tenantId;
        context.Items["ExistingKey"] = "ExistingValue";

        RequestDelegate next = (HttpContext ctx) => Task.CompletedTask;
        var middleware = new TenantMiddleware(next);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Items.Should().ContainKey("TenantId");
        context.Items.Should().ContainKey("ExistingKey");
        context.Items["ExistingKey"].Should().Be("ExistingValue");
    }

    [Theory]
    [InlineData("tenant-001")]
    [InlineData("tenant-with-dashes")]
    [InlineData("tenant_with_underscores")]
    [InlineData("TENANT-UPPERCASE")]
    [InlineData("tenant-123-456")]
    public async Task InvokeAsync_AcceptsDifferentTenantIdFormats(string tenantId)
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-ID"] = tenantId;

        var nextCalled = false;
        RequestDelegate next = (HttpContext ctx) =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new TenantMiddleware(next);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.Should().BeTrue();
        context.Items["TenantId"].Should().Be(tenantId);
    }
}
