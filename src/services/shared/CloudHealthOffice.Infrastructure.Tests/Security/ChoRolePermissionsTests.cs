using CloudHealthOffice.Infrastructure.Security;

namespace CloudHealthOffice.Infrastructure.Tests.Security;

public class ChoRolePermissionsTests
{
    [Theory]
    [InlineData("platform:admin")]
    [InlineData("platform:tenants")]
    [InlineData("platform:inquiries")]
    [InlineData("PLATFORM:ADMIN")]
    public void TenantAdmin_wildcard_does_not_grant_platform_permissions(string permission)
    {
        var granted = ChoRolePermissions.Expand([ChoRolePermissions.TenantAdmin]);

        Assert.False(ChoRolePermissions.Satisfies(granted, permission));
    }

    [Fact]
    public void Read_wildcard_does_not_grant_platform_read()
    {
        var granted = ChoRolePermissions.Expand([ChoRolePermissions.ComplianceOfficer]);

        Assert.False(ChoRolePermissions.Satisfies(granted, "platform:read"));
        Assert.True(ChoRolePermissions.Satisfies(granted, "claims:read"));
    }

    [Theory]
    [InlineData("platform:admin")]
    [InlineData("platform:tenants")]
    [InlineData("platform:inquiries")]
    public void PlatformAdmin_has_platform_permissions(string permission)
    {
        var granted = ChoRolePermissions.Expand([ChoRolePermissions.PlatformAdmin]);

        Assert.True(ChoRolePermissions.Satisfies(granted, permission));
    }

    [Fact]
    public void Explicit_platform_wildcard_grants_platform_permissions()
    {
        Assert.True(ChoRolePermissions.Matches("platform:*", "platform:admin"));
    }

    [Fact]
    public void TenantAdmin_wildcard_still_grants_tenant_permissions()
    {
        var granted = ChoRolePermissions.Expand([ChoRolePermissions.TenantAdmin]);

        Assert.True(ChoRolePermissions.Satisfies(granted, "claims:void"));
        Assert.True(ChoRolePermissions.Satisfies(granted, "payments:approve"));
    }
}
