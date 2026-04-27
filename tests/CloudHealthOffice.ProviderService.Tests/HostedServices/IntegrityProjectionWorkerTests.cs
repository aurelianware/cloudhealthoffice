using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProviderService.HostedServices;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.HostedServices;

/// <summary>
/// Coverage for <see cref="IntegrityProjectionWorker"/>'s sweep loop:
/// per-tenant iteration via <see cref="IServiceScopeFactory"/>, due-row
/// filtering, and graceful shutdown.
///
/// We avoid spinning a real <see cref="BackgroundService"/> host and
/// instead exercise <c>StartAsync</c> + a short <c>StopAsync</c>; the
/// sweep runs once and the disabled flag (or empty tenant set) keeps
/// the loop idle so the test completes deterministically.
/// </summary>
public class IntegrityProjectionWorkerTests
{
    private static (IntegrityProjectionWorker worker,
                    InMemoryProviderRepository repo,
                    FakeProviderVerificationClient client,
                    FakeProviderVerificationEventPublisher events,
                    IServiceProvider sp)
        BuildWorker(IntegrityProjectionOptions? options = null)
    {
        var repo = new InMemoryProviderRepository();
        var client = new FakeProviderVerificationClient();
        var events = new FakeProviderVerificationEventPublisher();
        var opts = options ?? new IntegrityProjectionOptions
        {
            // Sweep interval doesn't matter for these tests — we cancel
            // after the first sweep completes.
            SweepInterval = TimeSpan.FromSeconds(60),
        };

        var services = new ServiceCollection();
        services.AddSingleton<IProviderRepository>(repo);
        services.AddSingleton<IProviderVerificationClient>(client);
        services.AddSingleton<IProviderVerificationEventPublisher>(events);
        services.AddSingleton(Options.Create(opts));
        services.AddSingleton<IProviderIntegrityProjectionService, ProviderIntegrityProjectionService>();
        services.AddLogging();
        var sp = services.BuildServiceProvider();

        var worker = new IntegrityProjectionWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor(opts),
            NullLogger<IntegrityProjectionWorker>.Instance);

        return (worker, repo, client, events, sp);
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<IntegrityProjectionOptions>
    {
        private readonly IntegrityProjectionOptions _value;
        public TestOptionsMonitor(IntegrityProjectionOptions value) => _value = value;
        public IntegrityProjectionOptions CurrentValue => _value;
        public IntegrityProjectionOptions Get(string? name) => _value;
        public IDisposable? OnChange(Action<IntegrityProjectionOptions, string?> listener) => null;
    }

    private static void Seed(InMemoryProviderRepository repo, string tenant, string providerId, string npi)
    {
        var p = new Provider
        {
            Id = providerId,
            ProviderId = providerId,
            TenantId = tenant,
            NPI = npi,
            ProviderType = ProviderType.Individual,
            FirstName = "Worker",
            LastName = "Target",
            PrimarySpecialty = "Internal Medicine",
            TaxonomyCode = "207R00000X",
            VersionId = providerId,
            VersionNumber = 1,
            VersionState = ProviderVersionState.Active,
        };
        repo.CreateAsync(p).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Worker_iterates_each_tenant_and_patches_due_rows()
    {
        var (worker, repo, client, _, _) = BuildWorker();
        Seed(repo, "tenant-a", "p-a", "1111111111");
        Seed(repo, "tenant-b", "p-b", "2222222222");
        client.Seed("1111111111", 80, "Clear");
        client.Seed("2222222222", 60, "Advisory");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(cts.Token);
        // Give the first sweep time to drain.
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        repo.Docs.First(d => d.Id == "p-a").IntegrityScore.Should().Be(80);
        repo.Docs.First(d => d.Id == "p-b").IntegrityScore.Should().Be(60);
    }

    [Fact]
    public async Task Worker_skips_when_disabled()
    {
        var disabled = new IntegrityProjectionOptions { Enabled = false };
        var (worker, repo, client, _, _) = BuildWorker(disabled);
        Seed(repo, "tenant-a", "p-a", "1111111111");
        client.Seed("1111111111", 80, "Clear");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        client.Calls.Should().BeEmpty();
        repo.Docs.First(d => d.Id == "p-a").IntegrityScore.Should().BeNull();
    }

    [Fact]
    public async Task Worker_emits_one_event_per_refresh()
    {
        var (worker, repo, client, events, _) = BuildWorker();
        Seed(repo, "tenant-a", "p-a", "1111111111");
        client.Seed("1111111111", 80, "Clear");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        events.Events.Should().ContainSingle(e =>
            e.ProviderId == "p-a"
            && e.EventType == ProviderVerificationEventType.ProviderVerificationRefreshed);
    }
}
