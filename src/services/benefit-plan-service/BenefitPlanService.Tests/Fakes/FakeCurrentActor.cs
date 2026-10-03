using CloudHealthOffice.Infrastructure.Security;

namespace BenefitPlanService.Tests.Fakes;

/// <summary>An authenticated caller for controller unit tests (the token subject).</summary>
public sealed class FakeCurrentActor : ICurrentActor
{
    public FakeCurrentActor(string userId = "test-user", string tenantId = "tenant-a")
    {
        UserId = userId;
        TenantId = tenantId;
    }

    public bool IsAuthenticated => true;
    public string UserId { get; }
    public string? DisplayName => null;
    public string? Email => null;
    public string TenantId { get; }
    public bool IsService => false;
    public IReadOnlyCollection<string> Roles => new[] { ChoRolePermissions.TenantAdmin };
    public bool HasPermission(string permission) => true;
}
