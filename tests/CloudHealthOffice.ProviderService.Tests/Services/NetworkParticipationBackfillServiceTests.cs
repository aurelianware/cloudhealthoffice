using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Behavior tests for <see cref="NetworkParticipationBackfillService"/>:
/// idempotent eligibility, tenant isolation, etag-conflict counting,
/// best-effort event emission.
/// </summary>
public class NetworkParticipationBackfillServiceTests
{
    private const string Tenant = "tenant-a";

    private static NetworkParticipation Legacy(LineOfBusiness lob = LineOfBusiness.Commercial,
        string planId = "plan-1", string networkId = "net-1") =>
        new()
        {
            PlanId = planId,
            NetworkId = networkId,
            LineOfBusiness = lob,
            NetworkTier = "Tier1",
            EffectiveDate = DateTime.UtcNow.AddYears(-1),
            AcceptingNewPatients = true,
        };

    private static NetworkParticipation AlreadyTouched(LineOfBusiness lob = LineOfBusiness.Commercial) =>
        new()
        {
            PlanId = "plan-touched",
            NetworkId = "net-1",
            LineOfBusiness = lob,
            NetworkTier = "Tier1",
            EffectiveDate = DateTime.UtcNow.AddYears(-1),
            AcceptingNewPatients = true,
            PanelLimit = 100,
        };

    private static Provider ActiveProvider(string id, params NetworkParticipation[] participations) =>
        new()
        {
            Id = id,
            ProviderId = id,
            TenantId = Tenant,
            NPI = "1234500000",
            VersionId = id + ":v1",
            VersionNumber = 1,
            VersionState = ProviderVersionState.Active,
            Status = ProviderStatus.Active,
            ProviderType = ProviderType.Individual,
            FirstName = "Test",
            LastName = "Provider",
            PrimarySpecialty = "Internal Medicine",
            TaxonomyCode = "207R00000X",
            NetworkParticipations = participations.ToList(),
        };

    private static (NetworkParticipationBackfillService Service, InMemoryProviderRepository Repo, FakeNetworkParticipationEventPublisher Events)
        BuildService(NetworkParticipationBackfillOptions? overrideOptions = null)
    {
        var repo = new InMemoryProviderRepository { TenantId = Tenant };
        var events = new FakeNetworkParticipationEventPublisher();
        var opts = overrideOptions ?? new NetworkParticipationBackfillOptions { AdminBackfillEnabled = true, PageSize = 50 };
        var service = new NetworkParticipationBackfillService(
            repo, events, Options.Create(opts),
            NullLogger<NetworkParticipationBackfillService>.Instance);
        return (service, repo, events);
    }

    [Fact]
    public async Task RunTenant_patches_legacy_participations_and_emits_events()
    {
        var (service, repo, events) = BuildService();
        await repo.CreateAsync(ActiveProvider("p1", Legacy(LineOfBusiness.Commercial), Legacy(LineOfBusiness.Medicare)));
        await repo.CreateAsync(ActiveProvider("p2", Legacy(LineOfBusiness.Medicaid)));

        var result = await service.RunTenantAsync(Tenant, new NetworkParticipationBackfillRequest
        {
            ActorId = "test-actor",
            CorrelationId = "corr-1",
        });

        result.ProvidersInspected.Should().Be(2);
        result.ParticipationsBackfilled.Should().Be(3);
        result.ParticipationsSkipped.Should().Be(0);
        result.ParticipationsFailed.Should().Be(0);
        result.EtagConflicts.Should().Be(0);
        result.BackfillRunId.Should().NotBeNullOrEmpty();
        events.Events.Should().HaveCount(3);
        events.Events.Should().OnlyContain(e => e.BackfillRunId == result.BackfillRunId);
        events.Events.Should().OnlyContain(e => e.ActorId == "test-actor" && e.CorrelationId == "corr-1");
    }

    [Fact]
    public async Task RunTenant_is_safe_to_rerun_and_reemits_events_with_distinct_runIds()
    {
        var (service, repo, events) = BuildService();
        await repo.CreateAsync(ActiveProvider("p1", Legacy()));

        var first = await service.RunTenantAsync(Tenant, new NetworkParticipationBackfillRequest());
        first.ParticipationsBackfilled.Should().Be(1);
        events.Events.Should().HaveCount(1);

        // The backfill writes panel-gating fields to their type defaults
        // (PanelGatingFields.LegacyUnconstrained), and eligibility is
        // also defined as "all five fields at type defaults." So a
        // patched row REMAINS eligible on rerun. Documented contract
        // (see docs/architecture/network-participation-backfill.md):
        // reruns are SAFE at the document-state level — they re-apply
        // the same values, never corrupt data — but they are NOT
        // skip-based idempotent at the event stream level. A new
        // backfillRunId per invocation produces a distinct event for
        // each run.
        var second = await service.RunTenantAsync(Tenant, new NetworkParticipationBackfillRequest());
        second.ParticipationsBackfilled.Should().Be(1);
        events.Events.Should().HaveCount(2);
        events.Events[0].BackfillRunId.Should().NotBe(events.Events[1].BackfillRunId);
        // Stored values still match defaults; rerun is value-preserving.
        repo.Docs[0].NetworkParticipations[0].PanelLimit.Should().BeNull();
    }

    [Fact]
    public async Task RunTenant_skips_already_touched_participations()
    {
        var (service, repo, _) = BuildService();
        await repo.CreateAsync(ActiveProvider("p1",
            Legacy(LineOfBusiness.Commercial),
            AlreadyTouched(LineOfBusiness.Medicare)));

        var result = await service.RunTenantAsync(Tenant, new NetworkParticipationBackfillRequest());

        result.ParticipationsBackfilled.Should().Be(1); // only the legacy one
        result.ParticipationsSkipped.Should().Be(1);    // the touched one
    }

    [Fact]
    public async Task RunTenant_isolates_tenants()
    {
        var (service, repo, events) = BuildService();
        var inTenant = ActiveProvider("p1", Legacy());
        var otherTenant = ActiveProvider("p2", Legacy());
        otherTenant.TenantId = "tenant-b";
        await repo.CreateAsync(inTenant);
        await repo.CreateAsync(otherTenant);

        var result = await service.RunTenantAsync(Tenant, new NetworkParticipationBackfillRequest());

        result.ProvidersInspected.Should().Be(1);
        result.ParticipationsBackfilled.Should().Be(1);
        events.Events.Should().HaveCount(1);
        events.Events[0].TenantId.Should().Be(Tenant);
    }

    [Fact]
    public async Task RunTenant_counts_etag_conflicts_separately_from_failures()
    {
        var (service, repo, events) = BuildService();
        await repo.CreateAsync(ActiveProvider("p1", Legacy()));
        repo.FailNextPanelGatingPatchAsConflict = true;

        var result = await service.RunTenantAsync(Tenant, new NetworkParticipationBackfillRequest());

        result.EtagConflicts.Should().Be(1);
        result.ParticipationsBackfilled.Should().Be(0);
        result.ParticipationsFailed.Should().Be(0);
        events.Events.Should().BeEmpty(); // no event when patch did not land
    }

    [Fact]
    public async Task RunTenant_does_not_emit_events_for_skipped_participations()
    {
        // Mix of legacy + touched on the same provider so the storage
        // filter passes (provider has at least one eligible
        // participation) and the service-layer skip path is exercised.
        // A provider with ONLY touched participations is excluded at
        // the storage layer — never reaches the service.
        var (service, repo, events) = BuildService();
        await repo.CreateAsync(ActiveProvider("p1",
            Legacy(LineOfBusiness.Commercial),
            AlreadyTouched(LineOfBusiness.Medicare)));

        var result = await service.RunTenantAsync(Tenant, new NetworkParticipationBackfillRequest());

        result.ParticipationsBackfilled.Should().Be(1);
        result.ParticipationsSkipped.Should().Be(1);
        // Event emitted only for the patched legacy participation; the
        // skipped touched one produces no event.
        events.Events.Should().HaveCount(1);
        events.Events[0].LineOfBusiness.Should().Be(LineOfBusiness.Commercial);
    }

    [Fact]
    public async Task RunTenant_caps_iteration_at_max_providers()
    {
        var (service, repo, _) = BuildService();
        for (var i = 0; i < 5; i++)
        {
            await repo.CreateAsync(ActiveProvider($"p{i}", Legacy()));
        }

        var result = await service.RunTenantAsync(Tenant, new NetworkParticipationBackfillRequest
        {
            MaxProviders = 3,
        });

        result.ProvidersInspected.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task RunTenant_continues_when_event_publication_fails()
    {
        var repo = new InMemoryProviderRepository { TenantId = Tenant };
        await repo.CreateAsync(ActiveProvider("p1", Legacy()));
        var events = new FakeNetworkParticipationEventPublisher { ThrowOnPublish = true };
        var service = new NetworkParticipationBackfillService(
            repo, events,
            Options.Create(new NetworkParticipationBackfillOptions { AdminBackfillEnabled = true, PageSize = 50 }),
            NullLogger<NetworkParticipationBackfillService>.Instance);

        var result = await service.RunTenantAsync(Tenant, new NetworkParticipationBackfillRequest());

        // Patch landed even though event publication failed.
        result.ParticipationsBackfilled.Should().Be(1);
        events.Events.Should().BeEmpty();
        repo.Docs[0].NetworkParticipations[0].AcceptedLobs.Should().NotBeNull();
    }
}
