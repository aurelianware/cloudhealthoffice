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

    [Theory]
    [InlineData(ChoRolePermissions.ClaimsSupervisor, "encounters:read", true)]
    [InlineData(ChoRolePermissions.ClaimsSupervisor, "encounters:write", true)]
    [InlineData(ChoRolePermissions.Finance, "encounters:read", true)]
    [InlineData(ChoRolePermissions.Finance, "encounters:write", false)]
    [InlineData(ChoRolePermissions.ClaimsExaminer, "encounters:read", false)]
    [InlineData(ChoRolePermissions.EnrollmentSpecialist, "benefits:read", true)]
    [InlineData(ChoRolePermissions.EnrollmentSpecialist, "benefits:write", false)]
    [InlineData(ChoRolePermissions.EnrollmentSpecialist, "providers:read", true)]
    [InlineData(ChoRolePermissions.EnrollmentSpecialist, "providers:write", false)]
    [InlineData(ChoRolePermissions.MemberServices, "providers:read", true)]
    [InlineData(ChoRolePermissions.MemberServices, "providers:write", false)]
    public void Encounter_and_enrollment_grants(string role, string permission, bool expected)
    {
        var granted = ChoRolePermissions.Expand([role]);

        Assert.Equal(expected, ChoRolePermissions.Satisfies(granted, permission));
    }

    [Theory]
    // clinical:read: USCDI clinical FHIR resources.
    [InlineData(ChoRolePermissions.UMCoordinator, "clinical:read", true)]
    [InlineData(ChoRolePermissions.TenantAdmin, "clinical:read", true)]        // *:*
    [InlineData(ChoRolePermissions.PlatformAdmin, "clinical:read", true)]      // *:*
    [InlineData(ChoRolePermissions.ComplianceOfficer, "clinical:read", true)]  // *:read
    [InlineData(ChoRolePermissions.MemberServices, "clinical:read", false)]
    [InlineData(ChoRolePermissions.ClaimsExaminer, "clinical:read", false)]
    [InlineData(ChoRolePermissions.ClaimsSupervisor, "clinical:read", false)]
    [InlineData(ChoRolePermissions.EnrollmentSpecialist, "clinical:read", false)]
    [InlineData(ChoRolePermissions.ProviderRelations, "clinical:read", false)]
    [InlineData(ChoRolePermissions.Finance, "clinical:read", false)]
    [InlineData(ChoRolePermissions.FinanceApprover, "clinical:read", false)]
    [InlineData(ChoRolePermissions.ComplianceViewer, "clinical:read", false)]
    // payer-to-payer:initiate: fhir-service PayerToPayer/$initiate.
    [InlineData(ChoRolePermissions.MemberServices, "payer-to-payer:initiate", true)]
    [InlineData(ChoRolePermissions.EnrollmentSpecialist, "payer-to-payer:initiate", true)]
    [InlineData(ChoRolePermissions.TenantAdmin, "payer-to-payer:initiate", true)]
    [InlineData(ChoRolePermissions.PlatformAdmin, "payer-to-payer:initiate", true)]
    [InlineData(ChoRolePermissions.ComplianceOfficer, "payer-to-payer:initiate", false)] // *:read is not an action
    [InlineData(ChoRolePermissions.UMCoordinator, "payer-to-payer:initiate", false)]
    [InlineData(ChoRolePermissions.ClaimsExaminer, "payer-to-payer:initiate", false)]
    [InlineData(ChoRolePermissions.ClaimsSupervisor, "payer-to-payer:initiate", false)]
    [InlineData(ChoRolePermissions.ProviderRelations, "payer-to-payer:initiate", false)]
    [InlineData(ChoRolePermissions.Finance, "payer-to-payer:initiate", false)]
    [InlineData(ChoRolePermissions.FinanceApprover, "payer-to-payer:initiate", false)]
    [InlineData(ChoRolePermissions.ComplianceViewer, "payer-to-payer:initiate", false)]
    public void Clinical_and_payer_to_payer_grants(string role, string permission, bool expected)
    {
        var granted = ChoRolePermissions.Expand([role]);

        Assert.Equal(expected, ChoRolePermissions.Satisfies(granted, permission));
    }

    [Fact]
    public void No_role_holds_a_bulk_export_permission_by_name()
    {
        // Bulk export is deliberately SMART/system-only until a `bulk-export`
        // permission is approved; nothing grants one by name.
        foreach (var role in ChoRolePermissions.BuiltInRoles)
            Assert.DoesNotContain(ChoRolePermissions.ForRole(role),
                p => p.StartsWith("bulk-export", StringComparison.OrdinalIgnoreCase));
    }
}
