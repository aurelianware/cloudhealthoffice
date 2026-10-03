using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using TenantService.Models;

namespace CloudHealthOffice.TenantService.Tests;

/// <summary>
/// The role catalogue tenant-service shows must grant the same clinical and
/// Payer-to-Payer permissions as the shared table the services enforce.
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
    public void Catalogue_grants_match_the_shared_table(string role, string permission, bool expected)
    {
        StandardRoles.HasPermission([role], permission, StandardRoles.All).Should().Be(expected);
        ChoRolePermissions.Satisfies(ChoRolePermissions.ForRole(role), permission).Should().Be(expected);
    }
}
