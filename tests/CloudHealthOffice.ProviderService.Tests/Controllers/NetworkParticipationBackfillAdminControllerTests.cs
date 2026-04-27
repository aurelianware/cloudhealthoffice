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
        InMemoryProviderRepository? repo = null)
    {
        var opts = new NetworkParticipationBackfillOptions { AdminBackfillEnabled = adminBackfillEnabled };
        var monitor = new TestOptionsMonitor(opts);
        repo ??= new InMemoryProviderRepository();
        var service = new NetworkParticipationBackfillService(
            repo,
            new FakeNetworkParticipationEventPublisher(),
            Options.Create(opts),
            NullLogger<NetworkParticipationBackfillService>.Instance);
        var controller = new NetworkParticipationBackfillAdminController(
            service, monitor, NullLogger<NetworkParticipationBackfillAdminController>.Instance);
        // ControllerBase requires HttpContext for ResolveActorId / TraceIdentifier.
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { TraceIdentifier = "test-trace" },
        };
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
    public async Task Backfill_returns_400_on_missing_tenantId_when_flag_enabled()
    {
        var controller = BuildController(adminBackfillEnabled: true);
        var result = await controller.BackfillNetworkParticipations(
            "", maxProviders: null, pageSize: null, CancellationToken.None);

        var bad = result.Result as BadRequestObjectResult;
        bad.Should().NotBeNull();
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
