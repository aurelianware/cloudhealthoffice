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

    [Fact]
    public void Finance_prepares_payments_but_cannot_approve_them()
    {
        var granted = ChoRolePermissions.Expand([ChoRolePermissions.Finance]);

        Assert.False(ChoRolePermissions.Satisfies(granted, "payments:approve"));
        foreach (var kept in new[]
                 {
                     "payments:read", "payments:run", "billing:read", "billing:run",
                     "finance:read", "finance:write", "reports:financial"
                 })
            Assert.True(ChoRolePermissions.Satisfies(granted, kept), kept);
    }

    [Fact]
    public void FinanceApprover_approves_payments_but_cannot_run_them()
    {
        Assert.Contains(ChoRolePermissions.FinanceApprover, ChoRolePermissions.BuiltInRoles);
        var granted = ChoRolePermissions.Expand([ChoRolePermissions.FinanceApprover]);

        Assert.Equal(
            new[] { "finance:read", "payments:approve", "payments:read", "reports:financial" },
            granted.OrderBy(p => p, StringComparer.Ordinal));
        Assert.False(ChoRolePermissions.Satisfies(granted, "payments:run"));
        Assert.False(ChoRolePermissions.Satisfies(granted, "billing:run"));
    }
}
