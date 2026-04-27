using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProviderService.Controllers;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Controllers;

/// <summary>
/// Unit-level coverage for <see cref="IntegrityProjectionAdminController"/>'s
/// gate behavior — the AdminBackfillEnabled flag must default to false
/// and surface 503 (not 404) so operators know the route exists but
/// is intentionally gated.
/// </summary>
public class IntegrityProjectionAdminControllerTests
{
    private static IntegrityProjectionAdminController BuildController(
        bool adminBackfillEnabled,
        IProviderIntegrityProjectionService projection)
    {
        var opts = new IntegrityProjectionOptions { AdminBackfillEnabled = adminBackfillEnabled };
        var monitor = new TestOptionsMonitor(opts);
        return new IntegrityProjectionAdminController(
            projection, monitor, NullLogger<IntegrityProjectionAdminController>.Instance);
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<IntegrityProjectionOptions>
    {
        private readonly IntegrityProjectionOptions _value;
        public TestOptionsMonitor(IntegrityProjectionOptions value) => _value = value;
        public IntegrityProjectionOptions CurrentValue => _value;
        public IntegrityProjectionOptions Get(string? name) => _value;
        public IDisposable? OnChange(Action<IntegrityProjectionOptions, string?> listener) => null;
    }

    private static ProviderIntegrityProjectionService BuildProjectionService()
    {
        var repo = new InMemoryProviderRepository();
        var client = new FakeProviderVerificationClient();
        var events = new FakeProviderVerificationEventPublisher();
        return new ProviderIntegrityProjectionService(
            repo, client, events,
            Options.Create(new IntegrityProjectionOptions { AdminBackfillEnabled = true }),
            NullLogger<ProviderIntegrityProjectionService>.Instance);
    }

    [Fact]
    public async Task Backfill_returns_503_when_flag_disabled()
    {
        var controller = BuildController(adminBackfillEnabled: false, BuildProjectionService());
        var result = await controller.BackfillIntegrityProjection("tenant-a", null, CancellationToken.None);

        var status = result.Result as ObjectResult;
        status.Should().NotBeNull();
        status!.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task Backfill_returns_400_on_missing_tenantId_when_flag_enabled()
    {
        var controller = BuildController(adminBackfillEnabled: true, BuildProjectionService());
        var result = await controller.BackfillIntegrityProjection("", null, CancellationToken.None);

        var bad = result.Result as BadRequestObjectResult;
        bad.Should().NotBeNull();
    }

    [Fact]
    public async Task Backfill_runs_when_flag_enabled_and_tenant_provided()
    {
        var controller = BuildController(adminBackfillEnabled: true, BuildProjectionService());
        var result = await controller.BackfillIntegrityProjection(
            "tenant-a", maxProviders: 10, CancellationToken.None);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        ok!.Value.Should().BeOfType<IntegrityProjectionTenantSweepResult>();
    }
}
