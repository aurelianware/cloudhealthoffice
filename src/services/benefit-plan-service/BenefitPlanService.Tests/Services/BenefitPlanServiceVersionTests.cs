using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Lifecycle coverage for the version chain: create draft → publish v1 →
/// amend → publish v2 → predecessor superseded; plus error-path coverage
/// for double-publish and updates against Published versions.
/// </summary>
public class BenefitPlanServiceVersionTests
{
    private const string Tenant = "tenant-a";
    private const string Actor = "user-1";

    private static (BenefitPlanServiceImpl service,
                    InMemoryBenefitPlanRepository repo,
                    InMemoryPlanVersionTransitionRepository transitions,
                    FakePlanVersionEventPublisher events) Build()
    {
        var repo = new InMemoryBenefitPlanRepository();
        var transitions = new InMemoryPlanVersionTransitionRepository();
        var events = new FakePlanVersionEventPublisher();
        var service = new BenefitPlanServiceImpl(repo, transitions, events, new NoOpNetworkTierSoftValidator(), new NoOpPlanLimitValidator(), NullLogger<BenefitPlanServiceImpl>.Instance);
        return (service, repo, transitions, events);
    }

    private static BenefitPlan SamplePlan(string planId = "plan-001") => new()
    {
        TenantId = Tenant,
        PlanId = planId,
        PlanName = "Gold HMO 500",
        Payer = "Acme Health",
        EffectiveDate = new DateTime(2026, 1, 1),
        PlanType = PlanType.HMO,
        LineOfBusiness = LineOfBusiness.Commercial,
        CostSharing = new CostSharing { MonthlyPremium = 475m, Coinsurance = 20m },
        Benefits = { new Benefit { ServiceCategory = "Office Visit", CopayAmount = 25m } }
    };

    [Fact]
    public async Task CreateDraftAsync_assigns_identity_and_marks_draft()
    {
        var (service, repo, _, _) = Build();

        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, Actor);

        draft.VersionId.Should().NotBeNullOrEmpty();
        draft.VersionNumber.Should().Be(1);
        draft.VersionState.Should().Be(PlanVersionState.Draft);
        draft.PredecessorVersionId.Should().BeNull();
        draft.IsActive.Should().BeFalse();
        repo.Docs.Should().ContainSingle(d => d.VersionId == draft.VersionId);
    }

    [Fact]
    public async Task PublishVersionAsync_publishes_genesis_and_emits_event()
    {
        var (service, _, transitions, events) = Build();

        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, Actor);
        var published = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, Actor);

        published.VersionState.Should().Be(PlanVersionState.Published);
        published.PublishedAt.Should().NotBeNull();
        published.PublishedBy.Should().Be(Actor);
        published.IsActive.Should().BeTrue();

        transitions.Items.Should().ContainSingle(t =>
            t.TransitionType == PlanVersionTransitionType.Publish && t.ToVersionId == published.VersionId);
        events.Events.Should().ContainSingle(e =>
            e.EventType == PlanVersionEventType.PlanVersionPublished && e.VersionId == published.VersionId);
    }

    [Fact]
    public async Task PublishVersionAsync_throws_when_version_not_draft()
    {
        var (service, _, _, _) = Build();

        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, Actor);
        await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, Actor);

        var act = () => service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, Actor);
        await act.Should().ThrowAsync<PlanVersionStateException>()
            .Where(ex => ex.CurrentState == PlanVersionState.Published);
    }

    [Fact]
    public async Task AmendPublishedPlanAsync_clones_content_and_links_predecessor()
    {
        var (service, _, transitions, _) = Build();

        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, Actor);
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, Actor);

        var v2Draft = await service.AmendPublishedPlanAsync(v1.PlanId, Tenant, Actor);

        v2Draft.VersionId.Should().NotBe(v1.VersionId);
        v2Draft.VersionNumber.Should().Be(2);
        v2Draft.VersionState.Should().Be(PlanVersionState.Draft);
        v2Draft.PredecessorVersionId.Should().Be(v1.VersionId);
        v2Draft.Benefits.Should().HaveSameCount(v1.Benefits);
        v2Draft.CostSharing.MonthlyPremium.Should().Be(v1.CostSharing.MonthlyPremium);
        v2Draft.CostSharing.Coinsurance.Should().Be(v1.CostSharing.Coinsurance);
        transitions.Items.Should().ContainSingle(t =>
            t.TransitionType == PlanVersionTransitionType.Amend && t.FromVersionId == v1.VersionId);
    }

    [Fact]
    public async Task AmendPublishedPlanAsync_throws_when_no_published_version_exists()
    {
        var (service, _, _, _) = Build();

        var act = () => service.AmendPublishedPlanAsync("does-not-exist", Tenant, Actor);
        await act.Should().ThrowAsync<PlanVersionStateException>();
    }

    [Fact]
    public async Task FullLifecycle_createDraft_publish_amend_publish_supersedes_predecessor()
    {
        var (service, repo, transitions, events) = Build();

        // create + publish v1
        var draft1 = await service.CreateDraftAsync(SamplePlan(), Tenant, Actor);
        var v1 = await service.PublishVersionAsync(draft1.PlanId, draft1.VersionId, Tenant, Actor);

        // amend to draft v2
        var draft2 = await service.AmendPublishedPlanAsync(v1.PlanId, Tenant, Actor);
        draft2.Benefits[0].CopayAmount = 35m; // tweak so the two versions diverge
        await repo.UpdateDraftAsync(draft2);

        // publish v2 → v1 must be Superseded with successor pointer
        var v2 = await service.PublishVersionAsync(draft2.PlanId, draft2.VersionId, Tenant, Actor);

        var v1Reloaded = await service.GetVersionAsync(v1.PlanId, v1.VersionId, Tenant);
        v1Reloaded!.VersionState.Should().Be(PlanVersionState.Superseded);
        v1Reloaded.SupersededAt.Should().NotBeNull();
        v1Reloaded.SupersededByVersionId.Should().Be(v2.VersionId);
        v1Reloaded.IsActive.Should().BeFalse();

        v2.VersionNumber.Should().Be(2);
        v2.PredecessorVersionId.Should().Be(v1.VersionId);
        v2.IsActive.Should().BeTrue();

        // both versions retrievable
        (await service.GetVersionAsync(v1.PlanId, v1.VersionId, Tenant)).Should().NotBeNull();
        (await service.GetVersionAsync(v2.PlanId, v2.VersionId, Tenant)).Should().NotBeNull();

        // listing returns newest-first
        var (items, _) = await service.ListVersionsAsync(v1.PlanId, Tenant, 10, null);
        items.Should().HaveCount(2);
        items[0].VersionId.Should().Be(v2.VersionId);

        // latest-published-as-of-today resolves to v2
        var current = await repo.GetLatestPublishedAsync(v1.PlanId, Tenant, DateTime.UtcNow);
        current!.VersionId.Should().Be(v2.VersionId);

        // events: 1 Publish, 1 Publish + 1 Supersede
        events.Events.Count(e => e.EventType == PlanVersionEventType.PlanVersionPublished).Should().Be(2);
        events.Events.Count(e => e.EventType == PlanVersionEventType.PlanVersionSuperseded).Should().Be(1);

        // transitions log: Publish + Amend + Supersede
        transitions.Items.Select(t => t.TransitionType).Should().BeEquivalentTo(new[]
        {
            PlanVersionTransitionType.Publish,
            PlanVersionTransitionType.Amend,
            PlanVersionTransitionType.Supersede
        });
    }

    [Fact]
    public async Task UpdatePlanAsync_against_published_throws_state_exception()
    {
        var (service, _, _, _) = Build();

        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, Actor);
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, Actor);

        v1.PlanName = "Tampered";
        var act = () => service.UpdatePlanAsync(v1, Tenant);
        await act.Should().ThrowAsync<PlanVersionStateException>()
            .Where(ex => ex.CurrentState == PlanVersionState.Published);
    }

    [Fact]
    public async Task SupersedeVersionAsync_terminates_published_version_with_no_successor()
    {
        var (service, _, transitions, events) = Build();

        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, Actor);
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, Actor);

        var effectiveDate = DateTime.UtcNow;
        var terminated = await service.SupersedeVersionAsync(v1.PlanId, v1.VersionId, Tenant, Actor, "test reason", effectiveDate);

        terminated.VersionState.Should().Be(PlanVersionState.Superseded);
        terminated.SupersededAt.Should().NotBeNull();
        terminated.SupersededByVersionId.Should().BeNull();
        terminated.IsActive.Should().BeFalse();
        terminated.TerminationDate.Should().Be(effectiveDate);

        // No longer resolvable as the current Published version.
        (await service.GetPlanAsync(v1.PlanId, Tenant)).Should().BeNull();

        transitions.Items.Should().ContainSingle(t =>
            t.TransitionType == PlanVersionTransitionType.Terminate
            && t.FromVersionId == v1.VersionId
            && t.ToVersionId == null
            && t.Reason == "test reason");
        events.Events.Should().ContainSingle(e =>
            e.EventType == PlanVersionEventType.PlanVersionTerminated && e.VersionId == v1.VersionId);
    }

    [Fact]
    public async Task SupersedeVersionAsync_throws_when_version_not_published()
    {
        var (service, _, _, _) = Build();

        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, Actor);

        var act = () => service.SupersedeVersionAsync(draft.PlanId, draft.VersionId, Tenant, Actor, "test", DateTime.UtcNow);
        await act.Should().ThrowAsync<PlanVersionStateException>()
            .Where(ex => ex.CurrentState == PlanVersionState.Draft);
    }

    [Fact]
    public async Task SupersedeVersionAsync_unknown_version_throws_with_isNotFound_set()
    {
        var (service, _, _, _) = Build();

        var act = () => service.SupersedeVersionAsync("plan", "no-such-version", Tenant, Actor, "test", DateTime.UtcNow);
        await act.Should().ThrowAsync<PlanVersionStateException>()
            .Where(ex => ex.IsNotFound);
    }

    [Fact]
    public async Task DeletePlanAsync_terminates_current_published_version()
    {
        var (service, _, transitions, _) = Build();

        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, Actor);
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, Actor);

        var deleted = await service.DeletePlanAsync(v1.PlanId, Tenant, Actor);

        deleted.Should().BeTrue();
        (await service.GetPlanAsync(v1.PlanId, Tenant)).Should().BeNull();
        transitions.Items.Should().ContainSingle(t => t.TransitionType == PlanVersionTransitionType.Terminate);
    }

    [Fact]
    public async Task DeletePlanAsync_returns_false_for_unknown_plan()
    {
        var (service, _, _, _) = Build();

        var deleted = await service.DeletePlanAsync("does-not-exist", Tenant, Actor);

        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task AddBenefitAsync_amends_and_publishes_new_version_with_benefit()
    {
        var (service, _, transitions, _) = Build();

        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, Actor);
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, Actor);

        var newBenefit = new Benefit { ServiceCategory = "Urgent Care", CopayAmount = 50m };
        var added = await service.AddBenefitAsync(v1.PlanId, Tenant, Actor, newBenefit);

        added.Should().Be(newBenefit);

        var current = await service.GetPlanAsync(v1.PlanId, Tenant);
        current.Should().NotBeNull();
        current!.VersionNumber.Should().Be(2);
        current.PredecessorVersionId.Should().Be(v1.VersionId);
        current.Benefits.Should().Contain(b => b.ServiceCategory == "Urgent Care" && b.CopayAmount == 50m);
        current.Benefits.Should().HaveCount(v1.Benefits.Count + 1);

        var v1Reloaded = await service.GetVersionAsync(v1.PlanId, v1.VersionId, Tenant);
        v1Reloaded!.VersionState.Should().Be(PlanVersionState.Superseded);
        v1Reloaded.SupersededByVersionId.Should().Be(current.VersionId);

        transitions.Items.Select(t => t.TransitionType).Should().BeEquivalentTo(new[]
        {
            PlanVersionTransitionType.Publish,
            PlanVersionTransitionType.Amend,
            PlanVersionTransitionType.Supersede
        });
    }

    [Fact]
    public async Task AddBenefitAsync_returns_null_for_unknown_plan()
    {
        var (service, _, _, _) = Build();

        var added = await service.AddBenefitAsync("does-not-exist", Tenant, Actor, new Benefit { ServiceCategory = "X" });

        added.Should().BeNull();
    }

    [Fact]
    public async Task LegacyCreatePlanAsync_marks_v1_published_for_backcompat()
    {
        var (service, _, _, _) = Build();

        var legacy = await service.CreatePlanAsync(SamplePlan(), Tenant);

        legacy.VersionState.Should().Be(PlanVersionState.Published);
        legacy.VersionNumber.Should().Be(1);
        legacy.VersionId.Should().NotBeNullOrEmpty();
        legacy.PublishedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task PublishVersionAsync_rejects_when_predecessor_no_longer_current()
    {
        // Two parallel amend chains race: the first amendment to publish
        // wins; the second draft (created from the same v1 baseline) is
        // now stale because the chain head moved, and publish must 409.
        var (service, _, _, _) = Build();

        var d1 = await service.CreateDraftAsync(SamplePlan(), Tenant, Actor);
        var v1 = await service.PublishVersionAsync(d1.PlanId, d1.VersionId, Tenant, Actor);

        var amendA = await service.AmendPublishedPlanAsync(v1.PlanId, Tenant, Actor);
        var amendB = await service.AmendPublishedPlanAsync(v1.PlanId, Tenant, Actor);

        // amendA wins the race.
        await service.PublishVersionAsync(amendA.PlanId, amendA.VersionId, Tenant, Actor);

        // amendB still points at v1; its publish must now fail.
        var act = () => service.PublishVersionAsync(amendB.PlanId, amendB.VersionId, Tenant, Actor);
        await act.Should().ThrowAsync<PlanVersionStateException>()
            .Where(ex => ex.CurrentState == PlanVersionState.Draft && !ex.IsNotFound);
    }

    [Fact]
    public async Task PublishVersionAsync_unknown_version_throws_with_isNotFound_set()
    {
        var (service, _, _, _) = Build();

        var act = () => service.PublishVersionAsync("plan", "no-such-version", Tenant, Actor);
        await act.Should().ThrowAsync<PlanVersionStateException>()
            .Where(ex => ex.IsNotFound);
    }
}
