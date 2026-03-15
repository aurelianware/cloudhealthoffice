using CloudHealthOffice.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;

namespace CloudHealthOffice.Infrastructure.Tests;

public class HttpContextExtensionsTests
{
    [Fact]
    public void GetTenantId_WhenSet_ReturnsTenantId()
    {
        var context = new DefaultHttpContext();
        context.Items["TenantId"] = "tenant-abc";

        var result = context.GetTenantId();

        result.Should().Be("tenant-abc");
    }

    [Fact]
    public void GetTenantId_WhenMissing_ThrowsTenantContextMissingException()
    {
        var context = new DefaultHttpContext();

        var act = () => context.GetTenantId();

        act.Should().Throw<TenantContextMissingException>()
           .WithMessage("*Tenant context not found*");
    }

    [Fact]
    public void GetTenantIdOrDefault_WhenSet_ReturnsTenantId()
    {
        var context = new DefaultHttpContext();
        context.Items["TenantId"] = "tenant-xyz";

        var result = context.GetTenantIdOrDefault();

        result.Should().Be("tenant-xyz");
    }

    [Fact]
    public void GetTenantIdOrDefault_WhenMissing_ReturnsNull()
    {
        var context = new DefaultHttpContext();

        var result = context.GetTenantIdOrDefault();

        result.Should().BeNull();
    }
}
