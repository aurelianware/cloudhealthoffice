using BenefitPlanService.Models;
using BenefitPlanService.Tests.Fakes;

namespace BenefitPlanService.Tests.Repositories;

/// <summary>
/// Capability 5.5 — projection-metadata bypass writes for
/// <c>BenefitPlan.NetworkTiers</c>. The bypass is exempt from the
/// <c>UpdateAsync</c> "Published is read-only" guard (mirrors
/// <c>UpdateIntegrityProjectionAsync</c> on provider-service). These
/// tests pin the contract on the in-memory fake and document the
/// invariant that real repositories must preserve.
/// </summary>
public sealed class UpdateNetworkTiersAsyncTests
{
    private const string Tenant = "tenant-a";
    private const string PlanId = "plan-001";

    [Fact]
    public async Task UpdateNetworkTiersAsync_Patches_Head_Published_Row_Without_Triggering_Version_Guard()
    {
        var repo = new InMemoryBenefitPlanRepository();
        await SeedPublishedAsync(repo);

        var newTiers = new List<NetworkTier>
        {
            new() { TierName = "In-Network", TierLevel = 1, NetworkId = "net-1" },
            new() { TierName = "Out-of-Network", TierLevel = 2, NetworkId = "net-2" },
        };

        var ok = await repo.UpdateNetworkTiersAsync(Tenant, PlanId, newTiers);

        ok.Should().BeTrue();
        var head = await repo.GetLatestPublishedAsync(PlanId, Tenant, DateTime.UtcNow);
        head.Should().NotBeNull();
        head!.NetworkTiers.Should().HaveCount(2);
        head.NetworkTiers.Single(t => t.TierName == "In-Network").NetworkId.Should().Be("net-1");
        head.NetworkTiers.Single(t => t.TierName == "Out-of-Network").NetworkId.Should().Be("net-2");
    }

    [Fact]
    public async Task UpdateNetworkTiersAsync_Returns_False_When_No_Published_Head()
    {
        var repo = new InMemoryBenefitPlanRepository();

        var ok = await repo.UpdateNetworkTiersAsync(Tenant, "ghost-plan", Array.Empty<NetworkTier>());

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateNetworkTiersAsync_Skips_Draft_Heads()
    {
        var repo = new InMemoryBenefitPlanRepository();
        await SeedDraftAsync(repo);

        var ok = await repo.UpdateNetworkTiersAsync(Tenant, PlanId, new List<NetworkTier>
        {
            new() { TierName = "In-Network", NetworkId = "net-1" },
        });

        // No Published version exists → the bypass returns false rather
        // than upgrading the Draft.
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_Still_Throws_On_Published_Rows_After_Bypass_Patch()
    {
        var repo = new InMemoryBenefitPlanRepository();
        var head = await SeedPublishedAsync(repo);

        await repo.UpdateNetworkTiersAsync(Tenant, PlanId, new List<NetworkTier>
        {
            new() { TierName = "In-Network", NetworkId = "net-1" },
        });

        // Identity-write path is unaffected: a regular UpdateAsync against
        // a Published row still raises PlanVersionStateException.
        head.PlanName = "Renamed Mid-Flight";
        await FluentActions
            .Invoking(() => repo.UpdateAsync(head))
            .Should().ThrowAsync<BenefitPlanService.Repositories.PlanVersionStateException>();
    }

    private static async Task<BenefitPlan> SeedPublishedAsync(InMemoryBenefitPlanRepository repo)
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
                new NetworkTier { TierName = "In-Network", TierLevel = 1 },
                new NetworkTier { TierName = "Out-of-Network", TierLevel = 2 },
            },
        };
        await repo.CreateAsync(plan);
        return plan;
    }

    private static async Task SeedDraftAsync(InMemoryBenefitPlanRepository repo)
    {
        var draft = new BenefitPlan
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = Tenant,
            PlanId = PlanId,
            PlanName = "Sample Draft",
            Payer = "Acme Health",
            EffectiveDate = DateTime.UtcNow.AddMonths(-3),
            VersionId = Guid.NewGuid().ToString(),
            VersionNumber = 1,
            VersionState = PlanVersionState.Draft,
        };
        await repo.CreateDraftAsync(draft);
    }
}
