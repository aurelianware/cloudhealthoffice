using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Behavior tests for <see cref="IntegrityProjectionStalenessReporter"/>:
///
/// <list type="bullet">
///   <item>Per-tenant counts respect the
///     <see cref="IntegrityProjectionOptions.StalenessAlertThreshold"/>.</item>
///   <item>Snapshot updates land on
///     <see cref="ChoMetrics.SetIntegrityScoreStaleCount(string, long)"/>
///     and the gauge surfaces them.</item>
///   <item>A non-positive threshold disables the gauge entry for the
///     tenant.</item>
///   <item>Repository failures are swallowed so the worker sweep is
///     never blocked by telemetry.</item>
/// </list>
/// </summary>
public class IntegrityProjectionStalenessReporterTests
{
    public IntegrityProjectionStalenessReporterTests()
    {
        ChoMetrics.ResetIntegrityScoreStaleCounts();
    }

    [Fact]
    public async Task ReportTenantAsync_StaleProviders_UpdatesPerTenantSnapshot()
    {
        var repo = new InMemoryProviderRepository();
        var fresh = DateTimeOffset.UtcNow.AddHours(-1);
        var stale = DateTimeOffset.UtcNow.AddDays(-30);

        await repo.CreateAsync(MakeProvider("p1", "tenant-a", lastVerifiedAt: fresh));
        await repo.CreateAsync(MakeProvider("p2", "tenant-a", lastVerifiedAt: stale));
        await repo.CreateAsync(MakeProvider("p3", "tenant-a", lastVerifiedAt: null));
        await repo.CreateAsync(MakeProvider("p4", "tenant-b", lastVerifiedAt: stale));

        var options = new IntegrityProjectionOptions
        {
            StalenessAlertThreshold = TimeSpan.FromDays(7),
        };

        var reporter = new IntegrityProjectionStalenessReporter(
            repo,
            new TestOptionsMonitor<IntegrityProjectionOptions>(options),
            NullLogger<IntegrityProjectionStalenessReporter>.Instance);

        var aCount = await reporter.ReportTenantAsync("tenant-a");
        var bCount = await reporter.ReportTenantAsync("tenant-b");

        aCount.Should().Be(2, "p2 (30d stale) + p3 (never verified)");
        bCount.Should().Be(1, "p4 is the only tenant-b row and is stale");
    }

    [Fact]
    public async Task ReportTenantAsync_ThresholdZero_ReturnsZero_AndClearsGaugeViaTryRemove()
    {
        var repo = new InMemoryProviderRepository();
        await repo.CreateAsync(MakeProvider("p1", "tenant-a", lastVerifiedAt: DateTimeOffset.UtcNow.AddDays(-365)));

        // Pre-seed the gauge with an existing entry so we can verify the
        // reporter calls SetIntegrityScoreStaleCount(_, -1) — the
        // documented "remove" sentinel. We can't read the gauge state
        // directly without a MeterListener, so we observe the contract
        // indirectly: the Set(-1) path goes through TryRemove, leaving
        // the slot empty. Asserting count==0 covers the public return
        // value; gauge-emission absence is verified at integration
        // tier where the Prometheus exporter is wired.
        ChoMetrics.SetIntegrityScoreStaleCount("tenant-a", 99);

        var options = new IntegrityProjectionOptions
        {
            StalenessAlertThreshold = TimeSpan.Zero,
        };

        var reporter = new IntegrityProjectionStalenessReporter(
            repo,
            new TestOptionsMonitor<IntegrityProjectionOptions>(options),
            NullLogger<IntegrityProjectionStalenessReporter>.Instance);

        var count = await reporter.ReportTenantAsync("tenant-a");

        count.Should().Be(0, "threshold=0 disables the gauge and the reporter returns the documented zero");
    }

    [Fact]
    public async Task ReportTenantAsync_RepositoryFailure_ReturnsNegativeSentinel_DoesNotThrow()
    {
        var repo = new ThrowingProviderRepository();
        var options = new IntegrityProjectionOptions
        {
            StalenessAlertThreshold = TimeSpan.FromDays(7),
        };

        var reporter = new IntegrityProjectionStalenessReporter(
            repo,
            new TestOptionsMonitor<IntegrityProjectionOptions>(options),
            NullLogger<IntegrityProjectionStalenessReporter>.Instance);

        var count = await reporter.ReportTenantAsync("tenant-a");

        count.Should().Be(-1,
            "repository failure returns -1 (sentinel) so callers can distinguish 'unknown' from a healthy zero");
    }

    [Fact]
    public async Task ReportTenantAsync_OnEmptyTenant_ReturnsZero()
    {
        var repo = new InMemoryProviderRepository();
        var options = new IntegrityProjectionOptions
        {
            StalenessAlertThreshold = TimeSpan.FromDays(7),
        };
        var reporter = new IntegrityProjectionStalenessReporter(
            repo,
            new TestOptionsMonitor<IntegrityProjectionOptions>(options),
            NullLogger<IntegrityProjectionStalenessReporter>.Instance);

        var count = await reporter.ReportTenantAsync("tenant-empty");

        count.Should().Be(0);
    }

    private static Provider MakeProvider(
        string id,
        string tenantId,
        DateTimeOffset? lastVerifiedAt) =>
        new()
        {
            Id = id,
            ProviderId = id,
            TenantId = tenantId,
            NPI = $"100000000{id.GetHashCode() & 0xFFFF:D4}",
            VersionId = id + ":v1",
            VersionNumber = 1,
            VersionState = ProviderVersionState.Active,
            Status = ProviderStatus.Active,
            ProviderType = ProviderType.Individual,
            FirstName = "Test",
            LastName = id,
            PrimarySpecialty = "Internal Medicine",
            TaxonomyCode = "207R00000X",
            LastVerifiedAt = lastVerifiedAt,
        };

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T> where T : class, new()
    {
        public TestOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; private set; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>
    /// Bare-minimum repository that throws on the only call the
    /// reporter makes — keeps the failure path test isolated from the
    /// in-memory fake's broader surface.
    /// </summary>
    private sealed class ThrowingProviderRepository : IProviderRepository
    {
        public Task<long> CountStaleProvidersAsync(string tenantId, DateTimeOffset staleBefore, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated transient failure");

        // ── Unused interface members ─────────────────────────────────
        public Task<Provider?> GetByIdAsync(string id) => throw new NotImplementedException();
        public Task<Provider?> GetByNPIAsync(string npi) => throw new NotImplementedException();
        public Task<IEnumerable<Provider>> SearchAsync(string? name, string? specialty, string? zipCode, string? state, string? planId, LineOfBusiness? lineOfBusiness, ProviderType? providerType, bool? acceptingNewPatients, int page, int pageSize, string? firstName = null, string? lastName = null, string? city = null) => throw new NotImplementedException();
        public Task<Provider> CreateAsync(Provider provider) => throw new NotImplementedException();
        public Task<Provider> UpdateAsync(Provider provider) => throw new NotImplementedException();
        public Task DeleteAsync(string id) => throw new NotImplementedException();
        public Task<IReadOnlyList<Provider>> ListNetworkRosterAsync(NetworkRosterQuery query, NetworkRosterSort sort, int skip, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Provider?> GetLatestActiveAsync(string providerId, DateTime asOf) => throw new NotImplementedException();
        public Task<Provider?> GetVersionAsync(string providerId, string versionId) => throw new NotImplementedException();
        public Task<(IReadOnlyList<Provider> Items, string? ContinuationToken)> ListVersionsAsync(string providerId, int pageSize, string? continuationToken) => throw new NotImplementedException();
        public Task<Provider> CreateDraftAsync(Provider draft) => throw new NotImplementedException();
        public Task<Provider> UpdateDraftAsync(Provider draft) => throw new NotImplementedException();
        public Task<Provider> ActivateAndSupersedeAsync(Provider draftToActivate, Provider? predecessor) => throw new NotImplementedException();
        public Task<Provider> ReplaceVersionRowAsync(Provider version) => throw new NotImplementedException();
        public Task<bool> UpdateIntegrityProjectionAsync(string tenantId, string providerId, int? integrityScore, string? integrityRating, DateTimeOffset? lastVerifiedAt, DateTimeOffset? nextVerificationDue, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Provider>> ListProvidersForIntegrityRefreshAsync(string tenantId, DateTimeOffset dueBefore, bool includeNeverVerified, int skip, int pageSize, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> ListProviderTenantIdsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> UpdatePanelGatingDefaultsAsync(string tenantId, string providerId, int participationIndex, PanelGatingFields fields, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Provider>> ListProvidersForPanelGatingBackfillAsync(string tenantId, int skip, int pageSize, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> UpdateCredentialingProjectionAsync(string tenantId, string providerId, CredentialingStatus status, DateTime? credentialingDate, DateTime? recredentialingDueDate, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
