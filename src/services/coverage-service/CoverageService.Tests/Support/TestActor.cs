using CloudHealthOffice.Infrastructure.Security;

namespace CoverageService.Tests.Support;

/// <summary>The authenticated user a controller test acts as.</summary>
public sealed class TestActor : ICurrentActor
{
    public const string DefaultUserId = "token-user";

    public TestActor(string userId = DefaultUserId, string tenantId = "t1")
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
    public IReadOnlyCollection<string> Roles => [];
    public bool HasPermission(string permission) => true;
}
