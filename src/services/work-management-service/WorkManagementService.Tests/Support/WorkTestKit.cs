using CloudHealthOffice.Events;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WorkManagementService.Models;
using WorkManagementService.Repositories;
using WorkManagementService.Services;

namespace WorkManagementService.Tests.Support;

/// <summary>A clock tests move by hand.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null) => _now = start ?? new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>An authenticated caller with a role's permissions (as a CHO token would carry).</summary>
public sealed class TestActor : ICurrentActor
{
    private readonly HashSet<string> _permissions;

    public TestActor(string userId, string tenantId = WorkTestKit.Tenant, params string[] roles)
    {
        UserId = userId;
        TenantId = tenantId;
        Roles = roles.Length == 0 ? [ChoRolePermissions.ClaimsExaminer] : roles;
        _permissions = ChoRolePermissions.Expand(Roles);
    }

    public bool IsAuthenticated => true;
    public string UserId { get; }
    public string? DisplayName => UserId;
    public string? Email => null;
    public string TenantId { get; }
    public bool IsService => false;
    public IReadOnlyCollection<string> Roles { get; }
    public bool HasPermission(string permission) => ChoRolePermissions.Satisfies(_permissions, permission);
}

/// <summary>A scripted claims-service snapshot source.</summary>
public sealed class FakeClaimsWorkSource : IClaimsWorkSource
{
    public List<ClaimWorkEvent> Pended { get; } = new();
    public bool Fail { get; set; }
    public DateTime ObservedAt { get; set; } = DateTime.UtcNow;
    public int Calls { get; private set; }

    public Task<ClaimWorkSnapshotPage> GetPendedPageAsync(string tenantId, int page, int pageSize, CancellationToken ct = default)
    {
        Calls++;
        if (Fail) throw new HttpRequestException("claims-service down");
        var items = Pended.Skip((page - 1) * pageSize).Take(pageSize).Select(e =>
        {
            e.TenantId = tenantId;
            e.SourceVersion = ObservedAt.Ticks;
            return e;
        }).ToList();
        return Task.FromResult(new ClaimWorkSnapshotPage
        {
            Page = page, PageSize = pageSize, TotalCount = Pended.Count, ObservedAt = ObservedAt, Items = items,
        });
    }
}

/// <summary>
/// A scripted entitlement directory. Grants everything by default, so tests
/// about routing and distribution say nothing about roles; a test about the
/// check sets <see cref="Deny"/>, <see cref="Unanswerable"/> or
/// <see cref="DenyAll"/>.
/// </summary>
public sealed class FakeEntitlementDirectory : IWorkEntitlementDirectory
{
    /// <summary>(userId, permission) pairs answered Denied.</summary>
    public HashSet<(string User, string Permission)> Deny { get; } = new();

    /// <summary>User ids the directory cannot answer for at all.</summary>
    public HashSet<string> Unanswerable { get; } = new(StringComparer.Ordinal);

    /// <summary>Denies every check, whoever asks.</summary>
    public bool DenyAll { get; set; }

    public List<(string Tenant, string User, string Permission)> Calls { get; } = new();

    public Task<Entitlement> CheckAsync(string tenantId, string userId, string permission, CancellationToken ct = default)
    {
        Calls.Add((tenantId, userId, permission));
        if (Unanswerable.Contains(userId)) return Task.FromResult(Entitlement.Unknown);
        if (DenyAll || Deny.Contains((userId, permission))) return Task.FromResult(Entitlement.Denied);
        return Task.FromResult(Entitlement.Granted);
    }
}

/// <summary>Builds the engine over a store, with a manual clock.</summary>
public sealed class WorkTestKit
{
    public const string Tenant = "tenant-a";

    public IWorkStore Store { get; }
    public ManualTimeProvider Clock { get; } = new();
    public WorkManagementOptions Options { get; } = new();
    public WorkSeedTracker Seeds { get; } = new();
    public FakeEntitlementDirectory Entitlements { get; } = new();

    private long _version = 1;

    public WorkTestKit(IWorkStore? store = null) => Store = store ?? new InMemoryWorkStore();

    public DateTime Now => Clock.GetUtcNow().UtcDateTime;

    public WorkConfigService Config() =>
        new(Store, Seeds, Entitlements, Clock, NullLogger<WorkConfigService>.Instance, Microsoft.Extensions.Options.Options.Create(Options));

    public WorkIntakeService Intake() => new(Store, Config(), Clock, NullLogger<WorkIntakeService>.Instance);

    public WorkDistributionService Distribution() =>
        new(Store, Config(), Entitlements, Microsoft.Extensions.Options.Options.Create(Options), Clock, NullLogger<WorkDistributionService>.Instance);

    /// <summary>A claim work-needed event, newer than any before it.</summary>
    public ClaimWorkEvent Needed(string claimId, string pendCode = "NCCI", decimal amount = 100m, string tenant = Tenant,
        DateTime? received = null, bool expedited = false, string? lob = "Commercial", long? version = null) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        EventType = ClaimWorkEventTopics.WorkNeeded,
        TenantId = tenant,
        ClaimId = claimId,
        ClaimNumber = "CN-" + claimId,
        ClaimStatus = "Pended",
        SourceVersion = version ?? Now.Ticks + _version++,
        PrimaryPendCode = pendCode,
        PendCodes = [pendCode],
        PendFingerprint = $"fp-{pendCode}-{amount}",
        LineOfBusiness = lob,
        ClaimType = "Professional",
        TotalChargeAmount = amount,
        BillingProviderNpi = "1999999984",
        Expedited = expedited,
        ReceivedDate = received ?? Now.AddDays(-1),
    };

    public ClaimWorkEvent Resolved(string claimId, string resolution = "Approved", string tenant = Tenant, long? version = null) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        EventType = ClaimWorkEventTopics.WorkResolved,
        TenantId = tenant,
        ClaimId = claimId,
        ClaimNumber = "CN-" + claimId,
        ClaimStatus = resolution,
        Resolution = resolution,
        ActorId = "examiner-1",
        SourceVersion = version ?? Now.Ticks + _version++,
    };

    public static string ItemId(string claimId, string tenant = Tenant) => WorkItem.IdFor(tenant, WorkType.Claim, claimId);

    public static string QueueId(string key, string tenant = Tenant) => $"{tenant}-{key}";

    /// <summary>Gives <paramref name="userId"/> a direct membership.</summary>
    public async Task JoinAsync(string userId, string queueKey, int rank = 1, bool overflow = false, decimal? limit = null, string tenant = Tenant)
    {
        await Config().EnsureSeededAsync(tenant);
        var result = await Config().SaveMembershipAsync(tenant, null, new MembershipInput
        {
            UserId = userId, QueueId = QueueId(queueKey, tenant), Rank = rank, IsOverflow = overflow, AuthorityLimit = limit,
        }, "admin");
        result.Error.Should().BeNull();
    }
}
