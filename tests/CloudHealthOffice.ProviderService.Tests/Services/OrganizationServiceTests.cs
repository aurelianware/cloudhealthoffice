using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Lifecycle tests for <see cref="OrganizationService"/>. Mirrors the
/// provider-versioning test set: genesis create, amend → supersede,
/// terminate, partOf hierarchy traversal.
/// </summary>
public class OrganizationServiceTests
{
    private const string TenantId = "tenant-a";

    [Fact]
    public async Task CreateAndActivateAsync_persists_active_v1_with_chain_key()
    {
        var (svc, repo) = NewService();

        var created = await svc.CreateAndActivateAsync(new Organization
        {
            TenantId = TenantId,
            Name = "Aetna PPO FL 2025",
            NetworkType = NetworkType.PPO,
            LineOfBusiness = LineOfBusiness.Commercial,
            EffectiveDate = DateTime.UtcNow.Date,
        }, actorId: "alice");

        created.OrganizationId.Should().NotBeEmpty();
        created.OrganizationId.Should().Be(created.Id);
        created.VersionNumber.Should().Be(1);
        created.VersionState.Should().Be(OrganizationVersionState.Active);
        created.ActivatedBy.Should().Be("alice");

        var head = await repo.GetByIdAsync(created.OrganizationId);
        head.Should().NotBeNull();
        head!.VersionState.Should().Be(OrganizationVersionState.Active);
    }

    [Fact]
    public async Task UpdateAsync_creates_v2_and_supersedes_v1()
    {
        var (svc, repo) = NewService();

        var v1 = await svc.CreateAndActivateAsync(new Organization
        {
            TenantId = TenantId,
            Name = "BCBS HMO",
            NetworkType = NetworkType.HMO,
            LineOfBusiness = LineOfBusiness.Medicare,
            EffectiveDate = DateTime.UtcNow.Date,
        }, actorId: "alice");

        var v2 = await svc.UpdateAsync(v1.OrganizationId, new Organization
        {
            Name = "BCBS HMO (renamed)",
            NetworkType = NetworkType.HMO,
            LineOfBusiness = LineOfBusiness.Medicare,
            EffectiveDate = DateTime.UtcNow.Date,
        }, actorId: "bob");

        v2.OrganizationId.Should().Be(v1.OrganizationId);
        v2.VersionNumber.Should().Be(2);
        v2.VersionState.Should().Be(OrganizationVersionState.Active);
        v2.PredecessorVersionId.Should().Be(v1.VersionId);
        v2.Name.Should().Be("BCBS HMO (renamed)");

        // The v1 row is still present but Superseded.
        var v1After = await repo.GetVersionAsync(v1.OrganizationId, v1.VersionId);
        v1After.Should().NotBeNull();
        v1After!.VersionState.Should().Be(OrganizationVersionState.Superseded);
        v1After.SupersededByVersionId.Should().Be(v2.VersionId);

        // GetByIdAsync resolves to the v2 head.
        var head = await repo.GetByIdAsync(v1.OrganizationId);
        head!.VersionId.Should().Be(v2.VersionId);
    }

    [Fact]
    public async Task UpdateAsync_throws_when_organization_missing()
    {
        var (svc, _) = NewService();

        var act = () => svc.UpdateAsync("missing", new Organization
        {
            Name = "x",
            NetworkType = NetworkType.PPO,
            LineOfBusiness = LineOfBusiness.Commercial,
            EffectiveDate = DateTime.UtcNow.Date,
        }, "actor");

        var ex = await act.Should().ThrowAsync<OrganizationVersionStateException>();
        ex.Which.IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task TerminateAsync_marks_head_as_terminated()
    {
        var (svc, repo) = NewService();

        var created = await svc.CreateAndActivateAsync(new Organization
        {
            TenantId = TenantId,
            Name = "Gone Fishing PPO",
            NetworkType = NetworkType.PPO,
            LineOfBusiness = LineOfBusiness.Commercial,
            EffectiveDate = DateTime.UtcNow.Date,
        }, actorId: "alice");

        var terminated = await svc.TerminateAsync(created.OrganizationId, "wind down", "alice");

        terminated.VersionState.Should().Be(OrganizationVersionState.Terminated);
        terminated.Status.Should().Be(OrganizationStatus.Terminated);
        terminated.TerminationDate.Should().NotBeNull();

        // GetByIdAsync still returns the terminated row (it's the only non-Draft version).
        var head = await repo.GetByIdAsync(created.OrganizationId);
        head!.VersionState.Should().Be(OrganizationVersionState.Terminated);
    }

    [Fact]
    public async Task UpdateAsync_rejects_terminated_chain()
    {
        var (svc, _) = NewService();

        var created = await svc.CreateAndActivateAsync(new Organization
        {
            TenantId = TenantId,
            Name = "Net A",
            NetworkType = NetworkType.PPO,
            LineOfBusiness = LineOfBusiness.Commercial,
            EffectiveDate = DateTime.UtcNow.Date,
        }, actorId: "alice");

        await svc.TerminateAsync(created.OrganizationId, "done", "alice");

        var act = () => svc.UpdateAsync(created.OrganizationId, new Organization
        {
            Name = "Net A v2",
            NetworkType = NetworkType.PPO,
            LineOfBusiness = LineOfBusiness.Commercial,
            EffectiveDate = DateTime.UtcNow.Date,
        }, "alice");

        await act.Should().ThrowAsync<OrganizationVersionStateException>()
            .Where(e => e.CurrentState == OrganizationVersionState.Terminated);
    }

    [Fact]
    public async Task ListAsync_returns_only_one_row_per_chain()
    {
        var (svc, _) = NewService();

        var v1 = await svc.CreateAndActivateAsync(new Organization
        {
            TenantId = TenantId,
            Name = "Tier 1 PPO",
            NetworkType = NetworkType.PPO,
            LineOfBusiness = LineOfBusiness.Commercial,
            EffectiveDate = DateTime.UtcNow.Date,
        }, actorId: "alice");
        await svc.UpdateAsync(v1.OrganizationId, new Organization
        {
            Name = "Tier 1 PPO renamed",
            NetworkType = NetworkType.PPO,
            LineOfBusiness = LineOfBusiness.Commercial,
            EffectiveDate = DateTime.UtcNow.Date,
        }, "bob");
        await svc.CreateAndActivateAsync(new Organization
        {
            TenantId = TenantId,
            Name = "HMO",
            NetworkType = NetworkType.HMO,
            LineOfBusiness = LineOfBusiness.Commercial,
            EffectiveDate = DateTime.UtcNow.Date,
        }, "alice");

        var (items, total) = await svc.ListAsync(null, null, null, 1, 50);

        // Two distinct chains, with the head of each returned.
        items.Should().HaveCount(2);
        total.Should().Be(2);
        items.Select(o => o.VersionState).Should().AllBeEquivalentTo(OrganizationVersionState.Active);
    }

    [Fact]
    public async Task GetByParentAsync_returns_only_children_under_parent()
    {
        var (svc, _) = NewService();

        var parent = await svc.CreateAndActivateAsync(new Organization
        {
            TenantId = TenantId,
            Name = "Parent",
            NetworkType = NetworkType.PPO,
            LineOfBusiness = LineOfBusiness.Commercial,
            EffectiveDate = DateTime.UtcNow.Date,
        }, "alice");

        await svc.CreateAndActivateAsync(new Organization
        {
            TenantId = TenantId,
            Name = "Sub A",
            NetworkType = NetworkType.PPO,
            LineOfBusiness = LineOfBusiness.Commercial,
            EffectiveDate = DateTime.UtcNow.Date,
            ParentOrganizationId = parent.OrganizationId,
        }, "alice");
        await svc.CreateAndActivateAsync(new Organization
        {
            TenantId = TenantId,
            Name = "Sub B",
            NetworkType = NetworkType.PPO,
            LineOfBusiness = LineOfBusiness.Commercial,
            EffectiveDate = DateTime.UtcNow.Date,
            ParentOrganizationId = parent.OrganizationId,
        }, "alice");
        await svc.CreateAndActivateAsync(new Organization
        {
            TenantId = TenantId,
            Name = "Unrelated",
            NetworkType = NetworkType.HMO,
            LineOfBusiness = LineOfBusiness.Commercial,
            EffectiveDate = DateTime.UtcNow.Date,
        }, "alice");

        var children = await svc.GetByParentAsync(parent.OrganizationId);

        children.Should().HaveCount(2);
        children.All(o => o.ParentOrganizationId == parent.OrganizationId).Should().BeTrue();
    }

    private static (IOrganizationService Service, InMemoryOrganizationRepository Repo) NewService()
    {
        var repo = new InMemoryOrganizationRepository { TenantId = TenantId };
        var svc = new OrganizationService(repo, NullLogger<OrganizationService>.Instance);
        return (svc, repo);
    }
}
