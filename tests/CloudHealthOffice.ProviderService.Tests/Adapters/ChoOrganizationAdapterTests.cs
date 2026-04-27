using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderService.Adapters;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Adapters;

/// <summary>
/// Verifies the CHO pass-through organization adapter against the
/// in-memory repository. Mirrors the ChoProviderAdapter regression set.
/// </summary>
public class ChoOrganizationAdapterTests
{
    private const string TenantId = "tenant-a";

    [Fact]
    public void Platform_identifier_is_cho()
    {
        var adapter = NewAdapter(out _);
        adapter.Platform.Should().Be("cho");
    }

    [Fact]
    public async Task GetOrganizationAsync_returns_org_when_found()
    {
        var adapter = NewAdapter(out var repo);
        var seeded = await SeedActive(repo, name: "Aetna PPO FL", networkType: NetworkType.PPO);

        var response = await adapter.GetOrganizationAsync(new OrganizationAdapterRequest
        {
            TenantId = TenantId,
            OrganizationId = seeded.OrganizationId,
        });

        response.Platform.Should().Be("cho");
        response.Organization.Should().NotBeNull();
        response.Organization!.OrganizationId.Should().Be(seeded.OrganizationId);
        response.Organization.NetworkType.Should().Be(NetworkType.PPO);
    }

    [Fact]
    public async Task GetOrganizationAsync_returns_null_when_not_found()
    {
        var adapter = NewAdapter(out _);

        var response = await adapter.GetOrganizationAsync(new OrganizationAdapterRequest
        {
            TenantId = TenantId,
            OrganizationId = "missing",
        });

        response.Organization.Should().BeNull();
    }

    [Fact]
    public async Task GetOrganizationAsync_throws_when_id_missing()
    {
        var adapter = NewAdapter(out _);

        var act = () => adapter.GetOrganizationAsync(new OrganizationAdapterRequest { TenantId = TenantId });

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("OrganizationId is required*");
    }

    [Fact]
    public async Task ListAsync_filters_by_network_type()
    {
        var adapter = NewAdapter(out var repo);
        await SeedActive(repo, name: "Aetna PPO", networkType: NetworkType.PPO);
        await SeedActive(repo, name: "BCBS HMO", networkType: NetworkType.HMO);
        await SeedActive(repo, name: "Cigna PPO", networkType: NetworkType.PPO);

        var response = await adapter.ListAsync(new OrganizationAdapterRequest
        {
            TenantId = TenantId,
            NetworkType = NetworkType.PPO,
            Page = 1,
            PageSize = 50,
        });

        response.Platform.Should().Be("cho");
        response.Organizations.Should().HaveCount(2);
        response.Organizations.All(o => o.NetworkType == NetworkType.PPO).Should().BeTrue();
    }

    [Fact]
    public async Task ListAsync_filters_by_lob()
    {
        var adapter = NewAdapter(out var repo);
        await SeedActive(repo, name: "Medicare Net", lob: LineOfBusiness.Medicare);
        await SeedActive(repo, name: "Commercial Net", lob: LineOfBusiness.Commercial);

        var response = await adapter.ListAsync(new OrganizationAdapterRequest
        {
            TenantId = TenantId,
            LineOfBusiness = LineOfBusiness.Medicare,
            Page = 1,
            PageSize = 50,
        });

        response.Organizations.Should().HaveCount(1);
        response.Organizations.Single().LineOfBusiness.Should().Be(LineOfBusiness.Medicare);
    }

    [Fact]
    public async Task GetByParentAsync_returns_children()
    {
        var adapter = NewAdapter(out var repo);
        var parent = await SeedActive(repo, name: "Parent Net");
        await SeedActive(repo, name: "Child A", parent: parent.OrganizationId);
        await SeedActive(repo, name: "Child B", parent: parent.OrganizationId);
        await SeedActive(repo, name: "Unrelated");

        var response = await adapter.GetByParentAsync(new OrganizationAdapterRequest
        {
            TenantId = TenantId,
            ParentOrganizationId = parent.OrganizationId,
        });

        response.Organizations.Should().HaveCount(2);
        response.Organizations.All(o => o.ParentOrganizationId == parent.OrganizationId).Should().BeTrue();
    }

    [Fact]
    public async Task GetByParentAsync_throws_when_parent_missing()
    {
        var adapter = NewAdapter(out _);

        var act = () => adapter.GetByParentAsync(new OrganizationAdapterRequest { TenantId = TenantId });

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("ParentOrganizationId is required*");
    }

    private static ChoOrganizationAdapter NewAdapter(out InMemoryOrganizationRepository repo)
    {
        repo = new InMemoryOrganizationRepository { TenantId = TenantId };
        return new ChoOrganizationAdapter(repo, NullLogger<ChoOrganizationAdapter>.Instance);
    }

    private static async Task<Organization> SeedActive(
        InMemoryOrganizationRepository repo,
        string name,
        NetworkType networkType = NetworkType.PPO,
        LineOfBusiness lob = LineOfBusiness.Commercial,
        string? parent = null)
    {
        var service = new OrganizationService(repo, NullLogger<OrganizationService>.Instance);
        return await service.CreateAndActivateAsync(new Organization
        {
            TenantId = TenantId,
            Name = name,
            NetworkType = networkType,
            LineOfBusiness = lob,
            EffectiveDate = DateTime.UtcNow.Date,
            ParentOrganizationId = parent,
        }, actorId: "test-actor");
    }
}
