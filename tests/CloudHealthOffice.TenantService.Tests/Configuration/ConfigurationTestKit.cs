using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanConfiguration;
using Microsoft.Extensions.Logging.Abstractions;
using TenantService.Configuration;
using TenantService.Hierarchy;

namespace CloudHealthOffice.TenantService.Tests.Configuration;

public sealed class TestActor(string userId, string tenantId, params string[] roles) : ICurrentActor
{
    private readonly HashSet<string> _permissions = ChoRolePermissions.Expand(roles);
    public bool IsAuthenticated => true;
    public string UserId { get; } = userId;
    public string? DisplayName => UserId;
    public string? Email => null;
    public string TenantId { get; } = tenantId;
    public bool IsService => false;
    public IReadOnlyCollection<string> Roles { get; } = roles;
    public bool HasPermission(string permission) => ChoRolePermissions.Satisfies(_permissions, permission);
}

public sealed class FixedClock(DateTime now) : TimeProvider
{
    public DateTime Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
}

public sealed class TestCatalog(params SettingDefinition[] definitions) : ISettingCatalog
{
    private readonly Dictionary<string, SettingDefinition> _byKey = definitions.ToDictionary(d => d.Key);
    public IReadOnlyCollection<SettingDefinition> All => _byKey.Values;
    public SettingDefinition? Find(string key) => _byKey.GetValueOrDefault(key);
}

public sealed class RecordingPublisher : IConfigurationChangePublisher
{
    public List<ConfigurationChangedEvent> Published { get; } = new();
    public bool Fail { get; set; }

    public Task PublishAsync(ConfigurationChangedEvent change, CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("broker down");
        Published.Add(change);
        return Task.CompletedTask;
    }
}

/// <summary>Same contract as the Mongo store, in memory and tenant-scoped.</summary>
public sealed class InMemoryConfigurationStore : IConfigurationStore
{
    private readonly object _gate = new();
    public List<SettingValue> Values { get; } = new();
    public List<PendingConfigurationChange> Pending { get; } = new();
    public List<ConfigurationChangeRecord> Changes { get; } = new();

    /// <summary>Fail the next append (after nothing was written), to simulate an approval stopping part-way.</summary>
    public bool FailNextAppend { get; set; }

    /// <summary>Fail the next Approving → Approved update (after the version was written).</summary>
    public bool FailNextComplete { get; set; }

    public Task<IReadOnlyList<SettingValue>> GetVersionsAsync(string tenantId, string key, IReadOnlyCollection<(SettingLevel Level, string ScopeId)> nodes, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<SettingValue>>(Values.Where(v => v.TenantId == tenantId && v.Key == key
                && nodes.Any(n => n.Level == v.Level && n.ScopeId == v.ScopeId)).ToList());
    }

    public Task<IReadOnlyList<SettingValue>> GetVersionsAtOrBelowAsync(string tenantId, string key, SettingLevel level, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<SettingValue>>(Values.Where(v => v.TenantId == tenantId && v.Key == key && v.Level >= level).ToList());
    }

    public Task<SettingValue> AppendAsync(SettingValue value, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (FailNextAppend)
            {
                FailNextAppend = false;
                throw new InvalidOperationException("store unavailable");
            }
            if (value.ApprovedFromPendingId is { } pendingId
                && Values.FirstOrDefault(v => v.TenantId == value.TenantId && v.ApprovedFromPendingId == pendingId) is { } already)
                return Task.FromResult(already);
            value.Version = Values.Where(v => v.TenantId == value.TenantId && v.Key == value.Key && v.Level == value.Level && v.ScopeId == value.ScopeId)
                .Select(v => v.Version).DefaultIfEmpty(0).Max() + 1;
            value.Id = SettingValue.IdFor(value.TenantId, value.Key, value.Level, value.ScopeId, value.Version);
            Values.Add(value);
            return Task.FromResult(value);
        }
    }

    public Task<SettingValue?> GetVersionForPendingAsync(string tenantId, string pendingId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Values.FirstOrDefault(v => v.TenantId == tenantId && v.ApprovedFromPendingId == pendingId));
    }

    public Task AddPendingAsync(PendingConfigurationChange change, CancellationToken ct = default)
    {
        lock (_gate) Pending.Add(change);
        return Task.CompletedTask;
    }

    public Task<PendingConfigurationChange?> GetPendingAsync(string tenantId, string id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Pending.FirstOrDefault(p => p.TenantId == tenantId && p.Id == id));
    }

    public Task<IReadOnlyList<PendingConfigurationChange>> ListPendingAsync(string tenantId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<PendingConfigurationChange>>(Pending.Where(p => p.TenantId == tenantId && p.Status is PendingChangeStatus.Pending or PendingChangeStatus.Approving).ToList());
    }

    public Task<bool> TryRejectAsync(string tenantId, string id, string decidedBy, DateTime at, string? note, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var p = Pending.FirstOrDefault(x => x.TenantId == tenantId && x.Id == id && x.Status == PendingChangeStatus.Pending);
            if (p is null) return Task.FromResult(false);
            p.Status = PendingChangeStatus.Rejected;
            p.DecidedBy = decidedBy;
            p.DecidedAt = at;
            p.DecisionNote = note;
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryClaimApprovalAsync(string tenantId, string id, string approvedBy, DateTime at, string? note, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var p = Pending.FirstOrDefault(x => x.TenantId == tenantId && x.Id == id && x.Status == PendingChangeStatus.Pending);
            if (p is null) return Task.FromResult(false);
            p.Status = PendingChangeStatus.Approving;
            p.DecidedBy = approvedBy;
            p.DecidedAt = at;
            p.DecisionNote = note;
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryCompleteApprovalAsync(string tenantId, string id, SettingValue version, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (FailNextComplete)
            {
                FailNextComplete = false;
                throw new InvalidOperationException("store unavailable");
            }
            var p = Pending.FirstOrDefault(x => x.TenantId == tenantId && x.Id == id && x.Status == PendingChangeStatus.Approving);
            if (p is null) return Task.FromResult(false);
            p.Status = PendingChangeStatus.Approved;
            p.AppliedVersion = version.Version;
            p.AppliedVersionId = version.Id;
            return Task.FromResult(true);
        }
    }

    public Task AddChangeAsync(ConfigurationChangeRecord change, CancellationToken ct = default)
    {
        lock (_gate)
            if (Changes.All(c => c.Id != change.Id)) Changes.Add(change);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConfigurationChangeRecord>> ListChangesAsync(string tenantId, string? key, int limit, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ConfigurationChangeRecord>>(Changes.Where(c => c.TenantId == tenantId && (key == null || c.Key == key))
                .OrderByDescending(c => c.At).Take(limit).ToList());
    }
}

public static class TestSettings
{
    /// <summary>A two-person, tenant-only boolean.</summary>
    public static readonly SettingDefinition Control = new()
    {
        Key = "test.control",
        Type = SettingType.Boolean,
        Owner = "test",
        Description = "A control",
        Default = "true",
        AllowedLevels = new HashSet<SettingLevel> { SettingLevel.Tenant },
        ChangeControl = ChangeControl.TwoPersonApproval,
    };

    /// <summary>A day count with a Medicaid floor of 10 and an all-lines maximum of 90.</summary>
    public static readonly SettingDefinition Days = new()
    {
        Key = "test.days",
        Type = SettingType.Integer,
        Owner = "test",
        Description = "Days",
        Default = "30",
        DefaultsByLineOfBusiness = new Dictionary<string, string> { [LinesOfBusiness.Commercial] = "60" },
        AllowedLevels = new HashSet<SettingLevel> { SettingLevel.Tenant, SettingLevel.LineOfBusiness, SettingLevel.Sponsor },
        Constraints = new SettingConstraints
        {
            Min = 0,
            Max = 365,
            Floors = [new SettingFloor(LinesOfBusiness.Medicaid, 10, null, "State rule X"), new SettingFloor(null, null, 90, "Federal cap Y")],
        },
    };

    /// <summary>A setting a node at every level may set, for the tests that are about nodes rather than values.</summary>
    public static readonly SettingDefinition Anywhere = new()
    {
        Key = "test.anywhere",
        Type = SettingType.Integer,
        Owner = "test",
        Description = "A value any node may set",
        Default = "0",
        AllowedLevels = new HashSet<SettingLevel>(Enum.GetValues<SettingLevel>()),
    };

    public static ConfigurationService Service(InMemoryConfigurationStore store, RecordingPublisher publisher, FixedClock clock,
        ISponsorLineOfBusinessLookup? sponsors = null, IHierarchyDescendants? descendants = null) =>
        new(new TestCatalog(Control, Days, Anywhere, PlanSettings.OrphanGraceDays), store, publisher, clock,
            NullLogger<ConfigurationService>.Instance, sponsors, descendants);
}

/// <summary>
/// The hierarchy as the narrowing reads it: programs by carrier, plans by
/// program, and a sponsor's policies with their classes. Throws for
/// <see cref="Fail"/>, as an unreachable tenant-service, benefit-plan-service
/// or sponsor-service does.
/// </summary>
public sealed class FakeDescendants : IHierarchyDescendants
{
    /// <summary>Program id → (carrier id, line of business).</summary>
    public Dictionary<string, (string Carrier, string LineOfBusiness)> Programs { get; } = new();

    /// <summary>Plan id → program id.</summary>
    public Dictionary<string, string> Plans { get; } = new();

    /// <summary>Fail the program read.</summary>
    public bool Fail { get; set; }

    /// <summary>Group number → its policies, each with its class codes.</summary>
    public Dictionary<string, List<(string PolicyId, string[] ClassCodes)>> Policies { get; } = new();

    /// <summary>Fail only the plan read, so that a partly narrowed answer is possible.</summary>
    public bool FailPlans { get; set; }

    /// <summary>Fail only the contract read, as an unreachable sponsor-service does.</summary>
    public bool FailPolicies { get; set; }

    public int PolicyReads { get; private set; }

    public int ProgramReads { get; private set; }

    public Task<IReadOnlyList<ProgramNode>> ProgramsAsync(string tenantId, string? carrierId, CancellationToken ct = default)
    {
        if (Fail) throw new HttpRequestException("tenant-service unavailable");
        ProgramReads++;
        return Task.FromResult<IReadOnlyList<ProgramNode>>(Programs
            .Where(p => carrierId is null || p.Value.Carrier == carrierId)
            .Select(p => new ProgramNode(p.Key, p.Value.LineOfBusiness))
            .ToList());
    }

    public Task<IReadOnlyList<string>> BenefitPlanIdsAsync(string tenantId, IReadOnlyCollection<string> programIds, CancellationToken ct = default)
    {
        if (Fail || FailPlans) throw new HttpRequestException("benefit-plan-service unavailable");
        return Task.FromResult<IReadOnlyList<string>>(Plans.Where(p => programIds.Contains(p.Value)).Select(p => p.Key).ToList());
    }

    public Task<IReadOnlyList<SponsorPolicyNode>> PoliciesAsync(string tenantId, string groupNumber, CancellationToken ct = default)
    {
        if (Fail || FailPolicies) throw new HttpRequestException("sponsor-service unavailable");
        PolicyReads++;
        if (!Policies.TryGetValue(groupNumber, out var policies)) return Task.FromResult<IReadOnlyList<SponsorPolicyNode>>([]);
        return Task.FromResult<IReadOnlyList<SponsorPolicyNode>>(policies
            .Select(p => new SponsorPolicyNode(
                p.PolicyId,
                p.ClassCodes.Select(c => SponsorScopeIds.ForClass(p.PolicyId, c)).ToList()))
            .ToList());
    }
}

/// <summary>Sponsor lines of business by group number; throws for <see cref="Fail"/>.</summary>
public sealed class FakeSponsorLines : ISponsorLineOfBusinessLookup
{
    public Dictionary<string, string> Lines { get; } = new();
    public bool Fail { get; set; }
    public int Calls { get; private set; }

    public Task<string?> GetAsync(string tenantId, string groupNumber, CancellationToken ct = default)
    {
        Calls++;
        if (Fail) throw new HttpRequestException("sponsor-service unavailable");
        return Task.FromResult(Lines.GetValueOrDefault(groupNumber));
    }
}
