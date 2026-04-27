using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Lifecycle coverage for the provider version chain: create draft →
/// activate v1 → amend → activate v2 → predecessor superseded; plus
/// suspend / terminate / reactivate transitions and the error-paths the
/// repository enforces (active is read-only, draft predecessor must
/// match current head).
/// </summary>
public class ProviderVersioningServiceTests
{
    private const string Tenant = "tenant-a";
    private const string Actor = "user-1";

    private static (ProviderVersioningService service,
                    InMemoryProviderRepository repo,
                    InMemoryProviderTransitionRepository transitions,
                    FakeProviderVersionEventPublisher events) Build()
    {
        var repo = new InMemoryProviderRepository { TenantId = Tenant };
        var transitions = new InMemoryProviderTransitionRepository();
        var events = new FakeProviderVersionEventPublisher();
        var service = new ProviderVersioningService(repo, transitions, events,
            NullLogger<ProviderVersioningService>.Instance);
        return (service, repo, transitions, events);
    }

    private static Provider SampleProvider() => new()
    {
        TenantId = Tenant,
        NPI = "1234567890",
        ProviderType = ProviderType.Individual,
        FirstName = "Jane",
        LastName = "Doe",
        PrimarySpecialty = "Internal Medicine",
        TaxonomyCode = "207R00000X"
    };

    [Fact]
    public async Task CreateDraftAsync_assigns_identity_and_marks_draft()
    {
        var (service, repo, _, _) = Build();

        var draft = await service.CreateDraftAsync(SampleProvider(), Actor);

        draft.VersionId.Should().NotBeNullOrEmpty();
        draft.VersionNumber.Should().Be(1);
        draft.VersionState.Should().Be(ProviderVersionState.Draft);
        draft.PredecessorVersionId.Should().BeNull();
        draft.ProviderId.Should().Be(draft.Id);
        draft.Status.Should().Be(ProviderStatus.Pending);
        repo.Docs.Should().ContainSingle(d => d.VersionId == draft.VersionId);
    }

    [Fact]
    public async Task ActivateVersionAsync_activates_genesis_and_emits_event()
    {
        var (service, _, transitions, events) = Build();

        var draft = await service.CreateDraftAsync(SampleProvider(), Actor);
        var active = await service.ActivateVersionAsync(draft.ProviderId, draft.VersionId, Actor);

        active.VersionState.Should().Be(ProviderVersionState.Active);
        active.ActivatedAt.Should().NotBeNull();
        active.ActivatedBy.Should().Be(Actor);
        active.Status.Should().Be(ProviderStatus.Active);

        transitions.Items.Should().ContainSingle(t =>
            t.TransitionType == ProviderTransitionType.Activate && t.ToVersionId == active.VersionId);
        events.Events.Should().ContainSingle(e =>
            e.EventType == ProviderVersionEventType.ProviderVersionActivated && e.VersionId == active.VersionId);
    }

    [Fact]
    public async Task ActivateVersionAsync_throws_when_version_not_draft()
    {
        var (service, _, _, _) = Build();

        var draft = await service.CreateDraftAsync(SampleProvider(), Actor);
        await service.ActivateVersionAsync(draft.ProviderId, draft.VersionId, Actor);

        var act = () => service.ActivateVersionAsync(draft.ProviderId, draft.VersionId, Actor);
        await act.Should().ThrowAsync<ProviderVersionStateException>()
            .Where(ex => ex.CurrentState == ProviderVersionState.Active);
    }

    [Fact]
    public async Task AmendActiveProviderAsync_clones_content_and_links_predecessor()
    {
        var (service, _, transitions, _) = Build();

        var draft = await service.CreateDraftAsync(SampleProvider(), Actor);
        var v1 = await service.ActivateVersionAsync(draft.ProviderId, draft.VersionId, Actor);

        var v2Draft = await service.AmendActiveProviderAsync(v1.ProviderId, Actor);

        v2Draft.VersionId.Should().NotBe(v1.VersionId);
        v2Draft.VersionNumber.Should().Be(2);
        v2Draft.VersionState.Should().Be(ProviderVersionState.Draft);
        v2Draft.PredecessorVersionId.Should().Be(v1.VersionId);
        v2Draft.ProviderId.Should().Be(v1.ProviderId);
        v2Draft.Id.Should().NotBe(v1.Id, "each version is a separate document row");

        transitions.Items.Should().ContainSingle(t =>
            t.TransitionType == ProviderTransitionType.Amend && t.FromVersionId == v1.VersionId);
    }

    [Fact]
    public async Task AmendActiveProviderAsync_throws_when_no_active_version_exists()
    {
        var (service, _, _, _) = Build();

        var act = () => service.AmendActiveProviderAsync("does-not-exist", Actor);
        await act.Should().ThrowAsync<ProviderVersionStateException>();
    }

    [Fact]
    public async Task FullLifecycle_createDraft_activate_amend_activate_supersedes_predecessor()
    {
        var (service, repo, transitions, events) = Build();

        // create + activate v1
        var draft1 = await service.CreateDraftAsync(SampleProvider(), Actor);
        var v1 = await service.ActivateVersionAsync(draft1.ProviderId, draft1.VersionId, Actor);

        // amend to draft v2
        var draft2 = await service.AmendActiveProviderAsync(v1.ProviderId, Actor);
        draft2.PrimarySpecialty = "Family Medicine"; // tweak so the two versions diverge
        await repo.UpdateDraftAsync(draft2);

        // activate v2 → v1 must be Superseded with successor pointer
        var v2 = await service.ActivateVersionAsync(draft2.ProviderId, draft2.VersionId, Actor);

        var v1Reloaded = await service.GetVersionAsync(v1.ProviderId, v1.VersionId);
        v1Reloaded!.VersionState.Should().Be(ProviderVersionState.Superseded);
        v1Reloaded.SupersededAt.Should().NotBeNull();
        v1Reloaded.SupersededByVersionId.Should().Be(v2.VersionId);
        v1Reloaded.Status.Should().Be(ProviderStatus.Inactive);

        v2.VersionNumber.Should().Be(2);
        v2.PredecessorVersionId.Should().Be(v1.VersionId);
        v2.Status.Should().Be(ProviderStatus.Active);

        // both versions retrievable
        (await service.GetVersionAsync(v1.ProviderId, v1.VersionId)).Should().NotBeNull();
        (await service.GetVersionAsync(v2.ProviderId, v2.VersionId)).Should().NotBeNull();

        // listing returns newest-first
        var (items, _) = await service.ListVersionsAsync(v1.ProviderId, 10, null);
        items.Should().HaveCount(2);
        items[0].VersionId.Should().Be(v2.VersionId);

        // latest-active-as-of-today resolves to v2
        var current = await repo.GetLatestActiveAsync(v1.ProviderId, DateTime.UtcNow);
        current!.VersionId.Should().Be(v2.VersionId);

        // events: 2× Activated, 1× Superseded
        events.Events.Count(e => e.EventType == ProviderVersionEventType.ProviderVersionActivated).Should().Be(2);
        events.Events.Count(e => e.EventType == ProviderVersionEventType.ProviderVersionSuperseded).Should().Be(1);

        // transitions log: Activate, Amend, Supersede (Activate of v1, Amend, Supersede on v2 activate)
        transitions.Items.Select(t => t.TransitionType).Should().BeEquivalentTo(new[]
        {
            ProviderTransitionType.Activate,
            ProviderTransitionType.Amend,
            ProviderTransitionType.Supersede
        }, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public async Task UpdateAsync_against_active_throws_state_exception()
    {
        var (service, repo, _, _) = Build();

        var draft = await service.CreateDraftAsync(SampleProvider(), Actor);
        var v1 = await service.ActivateVersionAsync(draft.ProviderId, draft.VersionId, Actor);

        v1.PrimarySpecialty = "Tampered";
        var act = () => repo.UpdateAsync(v1);
        await act.Should().ThrowAsync<ProviderVersionStateException>()
            .Where(ex => ex.CurrentState == ProviderVersionState.Active);
    }

    [Fact]
    public async Task SuspendVersionAsync_marks_active_as_suspended_and_emits_event()
    {
        var (service, _, transitions, events) = Build();

        var draft = await service.CreateDraftAsync(SampleProvider(), Actor);
        var v1 = await service.ActivateVersionAsync(draft.ProviderId, draft.VersionId, Actor);

        var suspended = await service.SuspendVersionAsync(v1.ProviderId, v1.VersionId, "compliance review", Actor);

        suspended.VersionState.Should().Be(ProviderVersionState.Suspended);
        suspended.SuspendedAt.Should().NotBeNull();
        suspended.SuspensionReason.Should().Be("compliance review");
        suspended.Status.Should().Be(ProviderStatus.Inactive);

        transitions.Items.Should().Contain(t =>
            t.TransitionType == ProviderTransitionType.Suspend && t.FromVersionId == v1.VersionId);
        events.Events.Should().Contain(e =>
            e.EventType == ProviderVersionEventType.ProviderVersionSuspended && e.VersionId == v1.VersionId);
    }

    [Fact]
    public async Task TerminateVersionAsync_terminates_and_emits_event()
    {
        var (service, _, transitions, events) = Build();

        var draft = await service.CreateDraftAsync(SampleProvider(), Actor);
        var v1 = await service.ActivateVersionAsync(draft.ProviderId, draft.VersionId, Actor);

        var terminated = await service.TerminateVersionAsync(v1.ProviderId, v1.VersionId, "left network", Actor);

        terminated.VersionState.Should().Be(ProviderVersionState.Terminated);
        terminated.TerminationDate.Should().NotBeNull();
        terminated.TerminationReason.Should().Be("left network");
        terminated.Status.Should().Be(ProviderStatus.Terminated);

        transitions.Items.Should().Contain(t =>
            t.TransitionType == ProviderTransitionType.Terminate && t.FromVersionId == v1.VersionId);
        events.Events.Should().Contain(e =>
            e.EventType == ProviderVersionEventType.ProviderVersionTerminated && e.VersionId == v1.VersionId);
    }

    [Fact]
    public async Task ReactivateProviderAsync_creates_new_active_version_and_emits_reactivated()
    {
        var (service, _, transitions, events) = Build();

        var draft = await service.CreateDraftAsync(SampleProvider(), Actor);
        var v1 = await service.ActivateVersionAsync(draft.ProviderId, draft.VersionId, Actor);
        await service.SuspendVersionAsync(v1.ProviderId, v1.VersionId, "investigation", Actor);

        var v2 = await service.ReactivateProviderAsync(v1.ProviderId, Actor);

        v2.VersionId.Should().NotBe(v1.VersionId);
        v2.VersionNumber.Should().Be(2);
        v2.VersionState.Should().Be(ProviderVersionState.Active);
        v2.PredecessorVersionId.Should().Be(v1.VersionId);

        var v1Reloaded = await service.GetVersionAsync(v1.ProviderId, v1.VersionId);
        v1Reloaded!.VersionState.Should().Be(ProviderVersionState.Superseded);
        v1Reloaded.SupersededByVersionId.Should().Be(v2.VersionId);

        events.Events.Should().Contain(e =>
            e.EventType == ProviderVersionEventType.ProviderVersionReactivated && e.VersionId == v2.VersionId);
        transitions.Items.Should().Contain(t => t.TransitionType == ProviderTransitionType.Reactivate);
    }

    [Fact]
    public async Task ReactivateProviderAsync_throws_when_no_suspended_or_terminated_head()
    {
        var (service, _, _, _) = Build();

        var draft = await service.CreateDraftAsync(SampleProvider(), Actor);
        await service.ActivateVersionAsync(draft.ProviderId, draft.VersionId, Actor);

        var act = () => service.ReactivateProviderAsync(draft.ProviderId, Actor);
        await act.Should().ThrowAsync<ProviderVersionStateException>()
            .Where(ex => ex.IsNotFound);
    }

    [Fact]
    public async Task LegacyHydration_active_when_versionState_missing()
    {
        var (_, repo, _, _) = Build();

        // Insert a legacy doc directly: no VersionId, no VersionState set.
        await repo.CreateAsync(new Provider
        {
            Id = "legacy-1",
            TenantId = Tenant,
            NPI = "9999999990",
            ProviderType = ProviderType.Individual,
            FirstName = "Legacy",
            LastName = "Doc",
            PrimarySpecialty = "Cardiology",
            TaxonomyCode = "207RC0000X",
            Status = ProviderStatus.Active
        });

        var hydrated = await repo.GetByIdAsync("legacy-1");
        hydrated.Should().NotBeNull();
        hydrated!.VersionState.Should().Be(ProviderVersionState.Active);
        hydrated.VersionId.Should().Be("legacy-1");
        hydrated.VersionNumber.Should().Be(1);
        hydrated.ProviderId.Should().Be("legacy-1");
    }

    [Fact]
    public async Task ActivateVersionAsync_rejects_when_predecessor_no_longer_current()
    {
        // Two parallel amend chains race: the first amendment to activate
        // wins; the second draft (created from the same v1 baseline) is
        // now stale because the chain head moved, and activate must 409.
        var (service, _, _, _) = Build();

        var d1 = await service.CreateDraftAsync(SampleProvider(), Actor);
        var v1 = await service.ActivateVersionAsync(d1.ProviderId, d1.VersionId, Actor);

        var amendA = await service.AmendActiveProviderAsync(v1.ProviderId, Actor);
        var amendB = await service.AmendActiveProviderAsync(v1.ProviderId, Actor);

        // amendA wins the race.
        await service.ActivateVersionAsync(amendA.ProviderId, amendA.VersionId, Actor);

        // amendB still points at v1; its activate must now fail.
        var act = () => service.ActivateVersionAsync(amendB.ProviderId, amendB.VersionId, Actor);
        await act.Should().ThrowAsync<ProviderVersionStateException>()
            .Where(ex => ex.CurrentState == ProviderVersionState.Draft && !ex.IsNotFound);
    }

    [Fact]
    public async Task ActivateVersionAsync_unknown_version_throws_with_isNotFound_set()
    {
        var (service, _, _, _) = Build();

        var act = () => service.ActivateVersionAsync("provider", "no-such-version", Actor);
        await act.Should().ThrowAsync<ProviderVersionStateException>()
            .Where(ex => ex.IsNotFound);
    }
}
