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
    public void Catalogue_grants_match_the_shared_table(string role, string permission, bool expected)
    {
        StandardRoles.HasPermission([role], permission, StandardRoles.All).Should().Be(expected);
        ChoRolePermissions.Satisfies(ChoRolePermissions.ForRole(role), permission).Should().Be(expected);
    }
}
