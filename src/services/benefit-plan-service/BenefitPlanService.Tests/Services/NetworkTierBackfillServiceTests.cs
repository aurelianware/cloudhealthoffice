using BenefitPlanService.Models;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Capability 5.5 — operator-driven NetworkTier.NetworkId backfill.
/// Verifies idempotency, soft-miss outcomes, organization
/// resolution gating, and the head-row patch path through
/// <see cref="InMemoryBenefitPlanRepository.UpdateNetworkTiersAsync"/>.
/// </summary>
public sealed class NetworkTierBackfillServiceTests
{
    private const string Tenant = "tenant-a";
    private const string PlanId = "plan-001";

    [Fact]
    public async Task RunTenantAsync_Patches_A_Tier_When_NetworkId_Is_Null_And_Organization_Resolves()
    {
        var (service, repo, _) = Build(networkResolves: true);
        await SeedPlanAsync(repo, withNetworkIds: false);

        var result = await service.RunTenantAsync(Tenant, new NetworkTierBackfillRequest
        {
            Mappings = new()
            {
                new() { PlanId = PlanId, TierName = "In-Network", NetworkId = "net-1" },
            },
        });

        result.Patched.Should().Be(1);
        result.Skipped.Should().Be(0);
        result.Failed.Should().Be(0);

        var stored = repo.Docs.Single(d => d.PlanId == PlanId);
        stored.NetworkTiers.Single(t => t.TierName == "In-Network").NetworkId.Should().Be("net-1");
    }

    [Fact]
    public async Task RunTenantAsync_Is_Idempotent_For_Tiers_Already_Mapped()
    {
        var (service, repo, _) = Build(networkResolves: true);
        await SeedPlanAsync(repo, withNetworkIds: true);

        var result = await service.RunTenantAsync(Tenant, new NetworkTierBackfillRequest
        {
            Mappings = new()
            {
                new() { PlanId = PlanId, TierName = "In-Network", NetworkId = "net-2" },
            },
        });

        result.Patched.Should().Be(0);
        result.Skipped.Should().Be(1);
        var stored = repo.Docs.Single(d => d.PlanId == PlanId);
        stored.NetworkTiers.Single(t => t.TierName == "In-Network").NetworkId.Should().Be("preexisting-net-id");
    }

    [Fact]
    public async Task RunTenantAsync_Records_Unresolved_When_Organization_Lookup_Returns_Null()
    {
        var (service, repo, _) = Build(networkResolves: false);
        await SeedPlanAsync(repo, withNetworkIds: false);

        var result = await service.RunTenantAsync(Tenant, new NetworkTierBackfillRequest
        {
            Mappings = new()
            {
                new() { PlanId = PlanId, TierName = "In-Network", NetworkId = "net-bogus" },
            },
        });

        result.Patched.Should().Be(0);
        result.Unresolved.Should().Be(1);
        var stored = repo.Docs.Single(d => d.PlanId == PlanId);
        stored.NetworkTiers.Single(t => t.TierName == "In-Network").NetworkId.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task RunTenantAsync_Reports_NotFound_When_The_Plan_Has_No_Published_Version()
    {
        var (service, _, _) = Build(networkResolves: true);

        var result = await service.RunTenantAsync(Tenant, new NetworkTierBackfillRequest
        {
            Mappings = new()
            {
                new() { PlanId = "ghost-plan", TierName = "In-Network", NetworkId = "net-1" },
            },
        });

        result.NotFound.Should().Be(1);
        result.Patched.Should().Be(0);
    }

    [Fact]
    public async Task RunTenantAsync_Reports_Failed_When_Tier_Name_Not_On_Plan()
    {
        var (service, repo, _) = Build(networkResolves: true);
        await SeedPlanAsync(repo, withNetworkIds: false);

        var result = await service.RunTenantAsync(Tenant, new NetworkTierBackfillRequest
        {
            Mappings = new()
            {
                new() { PlanId = PlanId, TierName = "Phantom-Tier", NetworkId = "net-1" },
            },
        });

        result.Failed.Should().Be(1);
        result.Patched.Should().Be(0);
        result.Issues.Should().ContainSingle(i =>
            i.PlanId == PlanId && i.TierName == "Phantom-Tier" && i.Outcome == "failed");
    }

    [Fact]
    public async Task RunTenantAsync_Empty_Mapping_Set_Is_A_Noop()
    {
        var (service, _, _) = Build(networkResolves: true);

        var result = await service.RunTenantAsync(Tenant, new NetworkTierBackfillRequest());

        result.MappingsSubmitted.Should().Be(0);
        result.Patched.Should().Be(0);
        result.Skipped.Should().Be(0);
    }

    [Fact]
    public async Task RunTenantAsync_Multiple_Tiers_On_Same_Plan_Patch_In_One_Pass()
    {
        var (service, repo, _) = Build(networkResolves: true);
        await SeedPlanAsync(repo, withNetworkIds: false);

        var result = await service.RunTenantAsync(Tenant, new NetworkTierBackfillRequest
        {
            Mappings = new()
            {
                new() { PlanId = PlanId, TierName = "In-Network",     NetworkId = "net-1" },
                new() { PlanId = PlanId, TierName = "Out-of-Network", NetworkId = "net-2" },
            },
        });

        result.Patched.Should().Be(2);
        var stored = repo.Docs.Single(d => d.PlanId == PlanId);
        stored.NetworkTiers.Single(t => t.TierName == "In-Network").NetworkId.Should().Be("net-1");
        stored.NetworkTiers.Single(t => t.TierName == "Out-of-Network").NetworkId.Should().Be("net-2");
    }

    private static (NetworkTierBackfillService service,
                    InMemoryBenefitPlanRepository repo,
                    StubOrganizationLookup lookup) Build(bool networkResolves)
    {
        var repo = new InMemoryBenefitPlanRepository();
        var lookup = new StubOrganizationLookup(networkResolves);
        var options = Options.Create(new NetworkTierBackfillOptions { AdminBackfillEnabled = true });
        var monitor = new SingleValueOptionsMonitor<NetworkTierBackfillOptions>(options.Value);
        var service = new NetworkTierBackfillService(
            repo, lookup, monitor, NullLogger<NetworkTierBackfillService>.Instance);
        return (service, repo, lookup);
    }

    private static Task SeedPlanAsync(InMemoryBenefitPlanRepository repo, bool withNetworkIds)
    {
        var plan = new BenefitPlan
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = Tenant,
            PlanId = PlanId,
            PlanName = "Sample",
            Payer = "Acme Health",
            EffectiveDate = DateTime.UtcNow.AddMonths(-3),
            VersionId = Guid.NewGuid().ToString(),
            VersionNumber = 1,
            VersionState = PlanVersionState.Published,
            NetworkTiers = new()
            {
                new NetworkTier
                {
                    TierName = "In-Network",
                    TierLevel = 1,
                    NetworkId = withNetworkIds ? "preexisting-net-id" : null,
                },
                new NetworkTier
                {
                    TierName = "Out-of-Network",
                    TierLevel = 2,
                    NetworkId = null,
                },
            },
        };
        return repo.CreateAsync(plan);
    }

    private sealed class StubOrganizationLookup : IOrganizationLookupClient
    {
        private readonly bool _resolves;
        public StubOrganizationLookup(bool resolves) { _resolves = resolves; }
        public Task<OrganizationLookupResult?> GetOrganizationAsync(string networkId, CancellationToken ct = default)
        {
            if (!_resolves) return Task.FromResult<OrganizationLookupResult?>(null);
            return Task.FromResult<OrganizationLookupResult?>(new OrganizationLookupResult
            {
                OrganizationId = networkId,
                Name = "Resolved",
                EffectiveDate = DateTime.UtcNow.AddYears(-1),
            });
        }
    }

    private sealed class SingleValueOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public SingleValueOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
