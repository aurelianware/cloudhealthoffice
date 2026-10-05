using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProviderService.Controllers;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Controllers;

/// <summary>
/// Unit-level coverage for
/// <see cref="NetworkParticipationBackfillAdminController"/>'s gate
/// behavior — the AdminBackfillEnabled flag must default to false and
/// surface 503 (not 404) so operators know the route exists but is
/// intentionally gated.
/// </summary>
public class NetworkParticipationBackfillAdminControllerTests
{
    private static NetworkParticipationBackfillAdminController BuildController(
        bool adminBackfillEnabled,
        InMemoryProviderRepository? repo = null,
        FakeNetworkParticipationEventPublisher? events = null)
    {
        var opts = new NetworkParticipationBackfillOptions { AdminBackfillEnabled = adminBackfillEnabled };
        var monitor = new TestOptionsMonitor(opts);
        repo ??= new InMemoryProviderRepository();
        var service = new NetworkParticipationBackfillService(
            repo,
            events ?? new FakeNetworkParticipationEventPublisher(),
            Options.Create(opts),
            NullLogger<NetworkParticipationBackfillService>.Instance);
        var controller = new NetworkParticipationBackfillAdminController(
            service, monitor, NullLogger<NetworkParticipationBackfillAdminController>.Instance);
        // As UseChoAuthentication() leaves it: the token's tenant is tenant-a
        // and its subject is the actor.
        var ctx = TestHelpers.TokenHttpContext.For("tenant-a", "admin-user-1");
        ctx.TraceIdentifier = "test-trace";
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return controller;
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<NetworkParticipationBackfillOptions>
    {
        private readonly NetworkParticipationBackfillOptions _value;
        public TestOptionsMonitor(NetworkParticipationBackfillOptions value) => _value = value;
        public NetworkParticipationBackfillOptions CurrentValue => _value;
        public NetworkParticipationBackfillOptions Get(string? name) => _value;
        public IDisposable? OnChange(Action<NetworkParticipationBackfillOptions, string?> listener) => null;
    }

    [Fact]
    public async Task Backfill_returns_503_when_flag_disabled()
    {
        var controller = BuildController(adminBackfillEnabled: false);
        var result = await controller.BackfillNetworkParticipations(
            "tenant-a", maxProviders: null, pageSize: null, CancellationToken.None);

        var status = result.Result as ObjectResult;
        status.Should().NotBeNull();
        status!.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task Backfill_without_query_tenant_runs_in_token_tenant()
    {
        var repo = new InMemoryProviderRepository { TenantId = "tenant-a" };
        var controller = BuildController(adminBackfillEnabled: true, repo: repo);
        var result = await controller.BackfillNetworkParticipations(
            null, maxProviders: null, pageSize: null, CancellationToken.None);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        ((NetworkParticipationBackfillResult)ok!.Value!).TenantId.Should().Be("tenant-a");
    }

    [Fact]
    public async Task Backfill_for_a_tenant_other_than_the_token_tenant_is_forbidden_and_touches_nothing()
    {
        var repo = new InMemoryProviderRepository { TenantId = "tenant-a" };
        await repo.CreateAsync(BuildProvider("p2", "tenant-b"));
        var events = new FakeNetworkParticipationEventPublisher();
        var controller = BuildController(adminBackfillEnabled: true, repo: repo, events: events);

        var result = await controller.BackfillNetworkParticipations(
            "tenant-b", maxProviders: null, pageSize: 50, CancellationToken.None);

        var status = result.Result as ObjectResult;
        status.Should().NotBeNull();
        status!.StatusCode.Should().Be(403);
        events.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Backfill_records_the_token_subject_not_an_X_User_Id_header()
    {
        var repo = new InMemoryProviderRepository { TenantId = "tenant-a" };
        await repo.CreateAsync(BuildProvider("p1", "tenant-a"));
        var events = new FakeNetworkParticipationEventPublisher();
        var controller = BuildController(adminBackfillEnabled: true, repo: repo, events: events);
        controller.HttpContext.Request.Headers["X-User-Id"] = "someone-else";

        await controller.BackfillNetworkParticipations(
            null, maxProviders: null, pageSize: 50, CancellationToken.None);

        events.Events.Should().NotBeEmpty();
        events.Events.Should().OnlyContain(e => e.ActorId == "admin-user-1");
    }

    [Fact]
    public async Task Backfill_runs_when_flag_enabled_and_tenant_provided()
    {
        var repo = new InMemoryProviderRepository { TenantId = "tenant-a" };
        var controller = BuildController(adminBackfillEnabled: true, repo: repo);
        var result = await controller.BackfillNetworkParticipations(
            "tenant-a", maxProviders: 10, pageSize: 50, CancellationToken.None);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        ok!.Value.Should().BeOfType<NetworkParticipationBackfillResult>();
        var summary = (NetworkParticipationBackfillResult)ok.Value!;
        summary.TenantId.Should().Be("tenant-a");
        summary.BackfillRunId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Backfill_only_touches_named_tenant()
    {
        var repo = new InMemoryProviderRepository { TenantId = "tenant-a" };
        // Seed one provider in each tenant; only the named one should be inspected.
        await repo.CreateAsync(BuildProvider("p1", "tenant-a"));
        await repo.CreateAsync(BuildProvider("p2", "tenant-b"));

        var controller = BuildController(adminBackfillEnabled: true, repo: repo);
        var result = await controller.BackfillNetworkParticipations(
            "tenant-a", maxProviders: null, pageSize: 50, CancellationToken.None);
        var ok = (OkObjectResult)result.Result!;
        var summary = (NetworkParticipationBackfillResult)ok.Value!;
        summary.ProvidersInspected.Should().Be(1);
        summary.ParticipationsBackfilled.Should().Be(1);
    }

    private static Provider BuildProvider(string id, string tenantId) => new()
    {
        Id = id,
        ProviderId = id,
        TenantId = tenantId,
        NPI = "1234599999",
        VersionId = id + ":v1",
        VersionNumber = 1,
        VersionState = ProviderVersionState.Active,
        Status = ProviderStatus.Active,
        ProviderType = ProviderType.Individual,
        FirstName = "Test",
        LastName = "Provider",
        PrimarySpecialty = "Internal Medicine",
        TaxonomyCode = "207R00000X",
        NetworkParticipations = new List<NetworkParticipation>
        {
            new()
            {
                PlanId = "plan-1",
                NetworkId = "net-1",
                LineOfBusiness = LineOfBusiness.Commercial,
                NetworkTier = "Tier1",
                EffectiveDate = DateTime.UtcNow.AddYears(-1),
                AcceptingNewPatients = true,
            },
        },
    };
}
