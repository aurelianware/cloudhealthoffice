using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Fakes;

/// <summary>
/// No-op fake of <see cref="IIntegrityProjectionStalenessReporter"/> for
/// use in <see cref="CloudHealthOffice.ProviderService.Tests.HostedServices.IntegrityProjectionWorkerTests"/>. Returns 0 (zero
/// stale providers) so tests that only care about refresh behaviour are
/// not affected by the telemetry side-path introduced in capability 5.10.
/// </summary>
public sealed class FakeIntegrityProjectionStalenessReporter : IIntegrityProjectionStalenessReporter
{
    public Task<long> ReportTenantAsync(string tenantId, CancellationToken ct = default)
        => Task.FromResult(0L);
}
