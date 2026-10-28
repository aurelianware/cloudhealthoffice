using System.Text.Json.Nodes;
using FhirService.Services.PublicDirectory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.FhirService.Tests.Services.PublicDirectory;

/// <summary>
/// G-36: how a published directory's snapshot is built, reused, refreshed and
/// kept through an upstream failure.
/// </summary>
public class PlanNetDirectoryStoreTests
{
    private readonly MutableTime _time = new(DateTimeOffset.Parse("2026-10-28T12:00:00Z"));
    private readonly FakeSource _source = new();
    private readonly PlanNetDirectoryStore _store;

    public PlanNetDirectoryStoreTests()
    {
        var options = new StaticOptions(new PublicProviderDirectoryOptions { RefreshMinutes = 60 });
        _store = new PlanNetDirectoryStore(_source, options, _time, NullLogger<PlanNetDirectoryStore>.Instance);
    }

    [Fact]
    public async Task The_first_request_builds_and_later_ones_reuse_the_snapshot()
    {
        var first = await _store.GetAsync("acme-health", "tenant-acme", CancellationToken.None);
        var second = await _store.GetAsync("acme-health", "tenant-acme", CancellationToken.None);

        second.Should().BeSameAs(first);
        _source.Builds.Should().Be(1);
        _source.LastTenant.Should().Be("tenant-acme");
    }

    [Fact]
    public async Task A_stale_snapshot_is_served_while_a_new_one_builds()
    {
        var first = await _store.GetAsync("acme-health", "tenant-acme", CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(61));

        var served = await _store.GetAsync("acme-health", "tenant-acme", CancellationToken.None);
        served.Should().BeSameAs(first, "the rebuild runs in the background");

        await WaitUntil(() => _source.Builds == 2);
        var refreshed = await WaitForAsync(async () =>
        {
            var current = await _store.GetAsync("acme-health", "tenant-acme", CancellationToken.None);
            return ReferenceEquals(current, first) ? null : current;
        });
        refreshed.Should().NotBeSameAs(first);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_previous_snapshot()
    {
        var first = await _store.GetAsync("acme-health", "tenant-acme", CancellationToken.None);
        _source.Fail = true;
        _time.Advance(TimeSpan.FromMinutes(61));

        await _store.GetAsync("acme-health", "tenant-acme", CancellationToken.None);
        await WaitUntil(() => _source.Builds == 2);

        (await _store.GetAsync("acme-health", "tenant-acme", CancellationToken.None)).Should().BeSameAs(first);
    }

    [Fact]
    public async Task A_failed_first_build_is_not_retried_until_the_backoff_passes()
    {
        _source.Fail = true;
        var act = () => _store.GetAsync("acme-health", "tenant-acme", CancellationToken.None);

        await act.Should().ThrowAsync<PlanNetDirectoryUnavailableException>();
        await act.Should().ThrowAsync<PlanNetDirectoryUnavailableException>();
        _source.Builds.Should().Be(1);

        _source.Fail = false;
        _time.Advance(PlanNetDirectoryStore.RetryAfterFailure + TimeSpan.FromSeconds(1));
        (await _store.GetAsync("acme-health", "tenant-acme", CancellationToken.None)).Should().NotBeNull();
        _source.Builds.Should().Be(2);
    }

    [Fact]
    public async Task Directories_are_built_separately()
    {
        await _store.GetAsync("acme-health", "tenant-acme", CancellationToken.None);
        await _store.GetAsync("other-plan", "tenant-other", CancellationToken.None);

        _source.Builds.Should().Be(2);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        condition().Should().BeTrue();
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe) where T : class
    {
        for (var i = 0; i < 200; i++)
        {
            if (await probe() is { } value) return value;
            await Task.Delay(10);
        }
        throw new TimeoutException();
    }

    private sealed class FakeSource : IPlanNetDirectorySource
    {
        private int _builds;
        public int Builds => Volatile.Read(ref _builds);
        public volatile bool Fail;
        public string? LastTenant;

        public Task<PlanNetDirectorySnapshot> BuildAsync(string directoryId, string tenantId, CancellationToken ct)
        {
            Interlocked.Increment(ref _builds);
            LastTenant = tenantId;
            if (Fail) throw new PlanNetDirectoryUnavailableException("provider-service answered 503.");
            var resource = JsonNode.Parse($$"""{"resourceType":"Organization","id":"payer","name":"{{directoryId}}"}""")!.AsObject();
            return Task.FromResult(PlanNetDirectorySnapshot.Create(directoryId, [resource], DateTimeOffset.UtcNow));
        }
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class StaticOptions(PublicProviderDirectoryOptions value) : IOptionsMonitor<PublicProviderDirectoryOptions>
    {
        public PublicProviderDirectoryOptions CurrentValue => value;
        public PublicProviderDirectoryOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PublicProviderDirectoryOptions, string?> listener) => null;
    }
}
