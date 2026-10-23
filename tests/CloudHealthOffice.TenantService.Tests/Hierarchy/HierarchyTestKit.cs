using CloudHealthOffice.PlanConfiguration;
using Microsoft.Extensions.Logging.Abstractions;
using TenantService.Hierarchy;
using TenantService.Models;
using TenantService.Services;

namespace CloudHealthOffice.TenantService.Tests.Hierarchy;

/// <summary>Same contract as the Mongo store, in memory and tenant-scoped.</summary>
public sealed class InMemoryHierarchyStore : IHierarchyStore
{
    private readonly object _gate = new();
    public List<Carrier> Carriers { get; } = new();
    public List<PlanProgram> Programs { get; } = new();

    /// <summary>Makes the next carrier insert fail as a duplicate, as a concurrent insert would.</summary>
    public bool FailNextCarrierInsert { get; set; }

    public Task<IReadOnlyList<Carrier>> ListCarriersAsync(string tenantId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<Carrier>>(Carriers.Where(c => c.TenantId == tenantId).OrderBy(c => c.Code).ToList());
    }

    public Task<Carrier?> GetCarrierAsync(string tenantId, string id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Carriers.FirstOrDefault(c => c.TenantId == tenantId && c.Id == id));
    }

    public Task<bool> TryInsertCarrierAsync(Carrier carrier, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (FailNextCarrierInsert)
            {
                FailNextCarrierInsert = false;
                return Task.FromResult(false);
            }
            if (Carriers.Any(c => c.Id == carrier.Id)) return Task.FromResult(false);
            Carriers.Add(carrier);
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryReplaceCarrierAsync(Carrier carrier, long expectedRevision, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var index = Carriers.FindIndex(c => c.Id == carrier.Id && c.TenantId == carrier.TenantId && c.Revision == expectedRevision);
            if (index < 0) return Task.FromResult(false);
            carrier.Revision = expectedRevision + 1;
            Carriers[index] = carrier;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<PlanProgram>> ListProgramsAsync(string tenantId, string? carrierId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<PlanProgram>>(Programs
                .Where(p => p.TenantId == tenantId && (string.IsNullOrWhiteSpace(carrierId) || p.CarrierId == carrierId))
                .OrderBy(p => p.CarrierId).ThenBy(p => p.Code).ToList());
    }

    public Task<PlanProgram?> GetProgramAsync(string tenantId, string id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Programs.FirstOrDefault(p => p.TenantId == tenantId && p.Id == id));
    }

    public Task<bool> TryInsertProgramAsync(PlanProgram program, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Programs.Any(p => p.Id == program.Id)) return Task.FromResult(false);
            Programs.Add(program);
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryReplaceProgramAsync(PlanProgram program, long expectedRevision, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var index = Programs.FindIndex(p => p.Id == program.Id && p.TenantId == program.TenantId && p.Revision == expectedRevision);
            if (index < 0) return Task.FromResult(false);
            program.Revision = expectedRevision + 1;
            Programs[index] = program;
            return Task.FromResult(true);
        }
    }
}

/// <summary>Just the tenants a hierarchy test needs.</summary>
public sealed class StubTenants(params Tenant[] tenants) : ITenantRepository
{
    private readonly List<Tenant> _tenants = tenants.ToList();

    public Task<Tenant?> GetByTenantIdAsync(string tenantId) =>
        Task.FromResult(_tenants.FirstOrDefault(t => t.TenantId == tenantId));

    public Task<Tenant?> GetByIdAsync(string tenantId) => GetByTenantIdAsync(tenantId);
    public Task<IEnumerable<Tenant>> GetAllAsync(int pageSize = 100, string? continuationToken = null) => Task.FromResult<IEnumerable<Tenant>>(_tenants);
    public Task<IEnumerable<Tenant>> GetByStatusAsync(string status) => Task.FromResult(_tenants.Where(t => t.Status == status));
    public Task<Tenant> CreateAsync(Tenant tenant) => throw new NotSupportedException();
    public Task<Tenant> UpdateAsync(Tenant tenant) => throw new NotSupportedException();
    public Task DeleteAsync(string tenantId) => throw new NotSupportedException();
    public Task<bool> ExistsAsync(string tenantId) => Task.FromResult(_tenants.Any(t => t.TenantId == tenantId));
    public Task<Tenant?> GetByApiKeyHashAsync(string keyHash) => Task.FromResult<Tenant?>(null);
    public Task<bool> TryActivatePendingAsync(string tenantId, string actor) => throw new NotSupportedException();
}

/// <summary>Benefit plans' program ids; throws for <see cref="Fail"/>, as an unreachable benefit-plan-service does.</summary>
public sealed class FakeBenefitPlans : IBenefitPlanProgramLookup
{
    public Dictionary<string, string?> Programs { get; } = new();
    public bool Fail { get; set; }

    public Task<string?> GetProgramIdAsync(string tenantId, string planId, CancellationToken ct = default)
    {
        if (Fail) throw new HttpRequestException("benefit-plan-service unavailable");
        return Task.FromResult(Programs.GetValueOrDefault(planId));
    }
}

public static class HierarchyTestKit
{
    public const string Tenant = "t1";

    public static Tenant TenantRecord(string tenantId = Tenant) => new()
    {
        TenantId = tenantId,
        TenantName = "Acme Health",
        OrganizationName = "Acme Health Plan, Inc.",
        ContactInfo = new ContactInfo { Address = new Address { Street = "1 Main", City = "Austin", State = "TX", ZipCode = "78701" } },
    };

    public static HierarchyService Service(InMemoryHierarchyStore store, Configuration.FixedClock clock,
        IBenefitPlanProgramLookup? benefitPlans = null, ITenantRepository? tenants = null) =>
        new(store, tenants ?? new StubTenants(TenantRecord()), clock, NullLogger<HierarchyService>.Instance, benefitPlans);

    /// <summary>A configuration analyst: <c>config:product</c> without <c>config:enterprise</c>.</summary>
    public static Configuration.TestActor Analyst(string tenantId = Tenant) => new("analyst", tenantId, "ConfigurationAnalyst");

    /// <summary>A configuration manager: enterprise and product configuration.</summary>
    public static Configuration.TestActor Manager(string tenantId = Tenant) => new("manager", tenantId, "ConfigurationManager");

    public static ProgramWrite TexasStar => new()
    {
        Code = "star",
        Name = "Texas STAR",
        LineOfBusiness = LinesOfBusiness.Medicaid,
        StateCode = "tx",
    };
}
