using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Services;

namespace CloudHealthOffice.PaymentService.Tests.Security;

/// <summary>A fixed acting caller for service-level tests (no HTTP pipeline).</summary>
internal sealed class TestActor : ICurrentActor
{
    public TestActor(string userId, string tenantId = "test-tenant", bool isService = false)
    {
        UserId = userId;
        TenantId = tenantId;
        IsService = isService;
    }

    /// <summary>A user who did not create the runs the service tests execute.</summary>
    public static TestActor Approver() => new("approver-1");

    public bool IsAuthenticated => true;
    public string UserId { get; }
    public string? DisplayName => null;
    public string? Email => null;
    public string TenantId { get; }
    public bool IsService { get; }
    public IReadOnlyCollection<string> Roles => Array.Empty<string>();
    public bool HasPermission(string permission) => true;

    public RunSeparationOfDuties SeparationOfDuties() => new(this, NullLogger<RunSeparationOfDuties>.Instance);
}
