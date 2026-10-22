using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanConfiguration;
using Microsoft.Extensions.Logging.Abstractions;
using TenantService.Configuration;

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
            value.Version = Values.Where(v => v.TenantId == value.TenantId && v.Key == value.Key && v.Level == value.Level && v.ScopeId == value.ScopeId)
                .Select(v => v.Version).DefaultIfEmpty(0).Max() + 1;
            value.Id = SettingValue.IdFor(value.TenantId, value.Key, value.Level, value.ScopeId, value.Version);
            Values.Add(value);
            return Task.FromResult(value);
        }
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
            return Task.FromResult<IReadOnlyList<PendingConfigurationChange>>(Pending.Where(p => p.TenantId == tenantId && p.Status == PendingChangeStatus.Pending).ToList());
    }

    public Task<bool> TryDecideAsync(string tenantId, string id, PendingChangeStatus status, string decidedBy, DateTime at, string? note, int? appliedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var p = Pending.FirstOrDefault(x => x.TenantId == tenantId && x.Id == id && x.Status == PendingChangeStatus.Pending);
            if (p is null) return Task.FromResult(false);
            p.Status = status;
            p.DecidedBy = decidedBy;
            p.DecidedAt = at;
            p.DecisionNote = note;
            return Task.FromResult(true);
        }
    }

    public Task AddChangeAsync(ConfigurationChangeRecord change, CancellationToken ct = default)
    {
        lock (_gate) Changes.Add(change);
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

    public static ConfigurationService Service(InMemoryConfigurationStore store, RecordingPublisher publisher, FixedClock clock) =>
        new(new TestCatalog(Control, Days, PlanSettings.OrphanGraceDays), store, publisher, clock, NullLogger<ConfigurationService>.Instance);
}
