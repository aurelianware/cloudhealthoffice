using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using TenantService.Models;

namespace CloudHealthOffice.TenantService.Tests;

/// <summary>
/// The role catalogue tenant-service shows must grant the same clinical,
/// Payer-to-Payer, billing, reference-data and risk-adjustment permissions as
/// the shared table the services enforce.
/// </summary>
public class StandardRolesTests
{
    [Theory]
    [InlineData("UMCoordinator", "clinical:read", true)]
    [InlineData("TenantAdmin", "clinical:read", true)]
    [InlineData("ComplianceOfficer", "clinical:read", true)]
    [InlineData("MemberServices", "clinical:read", false)]
    [InlineData("ClaimsExaminer", "clinical:read", false)]
    [InlineData("EnrollmentSpecialist", "clinical:read", false)]
    [InlineData("MemberServices", "payer-to-payer:initiate", true)]
    [InlineData("EnrollmentSpecialist", "payer-to-payer:initiate", true)]
    [InlineData("TenantAdmin", "payer-to-payer:initiate", true)]
    [InlineData("ComplianceOfficer", "payer-to-payer:initiate", false)]
    [InlineData("UMCoordinator", "payer-to-payer:initiate", false)]
    [InlineData("ClaimsExaminer", "payer-to-payer:initiate", false)]
    // FinanceApprover reviews billing before releasing sponsor debits.
    [InlineData("FinanceApprover", "billing:read", true)]
    [InlineData("FinanceApprover", "billing:run", false)]
    [InlineData("FinanceApprover", "payments:approve", true)]
    [InlineData("FinanceApprover", "payments:run", false)]
    [InlineData("Finance", "billing:read", true)]
    [InlineData("Finance", "payments:approve", false)]
    [InlineData("ProviderRelations", "billing:read", false)]
    [InlineData("ProviderRelations", "claims:read", true)]
    [InlineData("ProviderRelations", "claims:note", true)]
    [InlineData("ProviderRelations", "claims:work", false)]
    // Code-set lookups (not PHI) for the finance and compliance-viewer roles.
    [InlineData("Finance", "reference-data:read", true)]
    [InlineData("FinanceApprover", "reference-data:read", true)]
    [InlineData("ComplianceViewer", "reference-data:read", true)]
    [InlineData("ComplianceViewer", "settings:manage", false)]
    // Finance calculates and submits risk scores.
    [InlineData("Finance", "risk-adjustment:read", true)]
    [InlineData("Finance", "risk-adjustment:write", true)]
    [InlineData("FinanceApprover", "risk-adjustment:write", false)]
    [InlineData("ComplianceViewer", "risk-adjustment:write", false)]
    [InlineData("ComplianceOfficer", "risk-adjustment:write", false)]
    // Legal holds: compliance (and admins through *:*), never *:read or members:write.
    [InlineData("ComplianceOfficer", "records:legal-hold", true)]
    [InlineData("TenantAdmin", "records:legal-hold", true)]
    [InlineData("ComplianceViewer", "records:legal-hold", false)]
    [InlineData("EnrollmentSpecialist", "records:legal-hold", false)]
    [InlineData("MemberServices", "records:legal-hold", false)]
    // Plan configuration (plan-configuration.md §9a).
    [InlineData("EnrollmentSpecialist", "config:read", true)]
    [InlineData("EnrollmentSpecialist", "config:sponsor", false)]
    [InlineData("ClaimsSupervisor", "config:read", true)]
    [InlineData("MemberServices", "config:read", true)]
    [InlineData("ComplianceOfficer", "config:read", true)]
    [InlineData("ComplianceOfficer", "config:approve", false)]
    [InlineData("ComplianceViewer", "config:read", true)]
    [InlineData("AccountManager", "config:sponsor", true)]
    [InlineData("AccountManager", "config:enterprise", false)]
    [InlineData("AccountManager", "config:approve", false)]
    [InlineData("Underwriter", "config:approve", true)]
    [InlineData("Underwriter", "config:sponsor", false)]
    [InlineData("ConfigurationAnalyst", "config:product", true)]
    [InlineData("ConfigurationAnalyst", "config:enterprise", false)]
    [InlineData("ConfigurationManager", "config:enterprise", true)]
    [InlineData("ConfigurationManager", "config:approve", true)]
    [InlineData("TenantAdmin", "config:enterprise", true)]
    [InlineData("ClaimsExaminer", "config:read", false)]
    public void Catalogue_grants_match_the_shared_table(string role, string permission, bool expected)
    {
        StandardRoles.HasPermission([role], permission, StandardRoles.All).Should().Be(expected);
        ChoRolePermissions.Satisfies(ChoRolePermissions.ForRole(role), permission).Should().Be(expected);
    }

    [Theory]
    [InlineData("AccountManager")]
    [InlineData("Underwriter")]
    [InlineData("ConfigurationAnalyst")]
    [InlineData("ConfigurationManager")]
    public void Configuration_roles_match_the_shared_table(string role) =>
        StandardRoles.All.Single(r => r.RoleName == role).Permissions.Should().BeEquivalentTo(ChoRolePermissions.ForRole(role));

    [Fact]
    public void ComplianceOfficer_permissions_match_the_shared_table()
    {
        StandardRoles.ComplianceOfficer.Permissions.Should()
            .BeEquivalentTo(ChoRolePermissions.ForRole(ChoRolePermissions.ComplianceOfficer));
    }
}
