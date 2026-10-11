using System.Reflection;
using BenefitPlanService.Controllers;
using CloudHealthOffice.Infrastructure.Security;

namespace BenefitPlanService.Tests.Controllers;

/// <summary>
/// T-5 / G-10. <c>POST api/v1/prior-auth-rules/seed-platform</c> writes PLATFORM
/// rules, which every tenant's PA decisions fall back to. It carried no
/// permission of its own, so benefit-plan-service's default write permission
/// (<c>settings:manage</c>, Program.cs) applied, and every tenant's TenantAdmin
/// holds that. Platform writes are a platform act: the existing
/// <c>platform:admin</c> permission (held only by PlatformAdmin; the reserved
/// <c>platform</c> resource is excluded from <c>*:*</c>) is what other services
/// use for shared/global writes, and this endpoint now requires it.
/// </summary>
public class PriorAuthRulesControllerAuthorizationTests
{
    /// <summary>benefit-plan-service's DefaultWritePermission (Program.cs).</summary>
    private const string ServiceDefaultWritePermission = "settings:manage";

    /// <summary>
    /// What the shared DefaultPermissionConvention resolves for the action: its
    /// own (or its controller's) [RequirePermission], else the service default.
    /// </summary>
    private static IReadOnlyList<string> EffectivePermissionsForSeed()
    {
        var action = typeof(PriorAuthRulesController).GetMethod(nameof(PriorAuthRulesController.SeedPlatformRules))!;
        var declared = action.GetCustomAttributes<RequirePermissionAttribute>()
            .Concat(typeof(PriorAuthRulesController).GetCustomAttributes<RequirePermissionAttribute>())
            .SelectMany(a => a.Permission.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        return declared.Count > 0 ? declared : [ServiceDefaultWritePermission];
    }

    private static bool RoleMaySeed(string role)
        => EffectivePermissionsForSeed().Any(required =>
            ChoRolePermissions.ForRole(role).Any(granted => ChoRolePermissions.Matches(granted, required)));

    [Fact]
    public void SeedPlatform_IsNotAvailableToATenantAdmin()
        => RoleMaySeed(ChoRolePermissions.TenantAdmin).Should().BeFalse(
            "a tenant administrator must not be able to write the platform rules every tenant falls back to");

    [Fact]
    public void SeedPlatform_IsAvailableToAPlatformAdmin()
        => RoleMaySeed(ChoRolePermissions.PlatformAdmin).Should().BeTrue();

    [Fact]
    public void SeedPlatform_RequiresTheExistingPlatformAdminPermission()
        => EffectivePermissionsForSeed().Should().Equal("platform:admin");
}
