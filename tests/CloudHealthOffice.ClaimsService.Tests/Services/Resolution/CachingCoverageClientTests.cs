using ClaimsService.Services.Resolution;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Resolution;

/// <summary>
/// Capability 5.8 — caching behaviour for the COB client. Mirrors
/// <see cref="CachingProviderMembershipClientTests"/> shape and 5-minute
/// TTL precedent. Empty lists ARE cached (positive answer; "CHO is the
/// only coverage"); null transport-failure results are NOT cached.
/// </summary>
public class CachingCoverageClientTests
{
    private readonly ICoverageClient _inner = Substitute.For<ICoverageClient>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    private const string TenantId = "tenant-1";
    private const string MemberId = "MEM-1";
    private static readonly DateTime AsOf = new(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<CobEntry> EntryList(params CobEntry[] entries) => entries;

    [Fact]
    public async Task CacheHit_does_not_call_inner_a_second_time()
    {
        var sut = new CachingCoverageClient(_inner, _cache);
        var entries = EntryList(new CobEntry { PayerName = "Aetna", CoverageSequence = "P" });
        _inner.GetCobEntriesAsync(TenantId, MemberId, AsOf, false, Arg.Any<CancellationToken>())
            .Returns(entries);

        var first = await sut.GetCobEntriesAsync(TenantId, MemberId, AsOf);
        var second = await sut.GetCobEntriesAsync(TenantId, MemberId, AsOf);

        Assert.Same(entries, first);
        Assert.Same(entries, second);
        await _inner.Received(1)
            .GetCobEntriesAsync(TenantId, MemberId, AsOf, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptyList_is_cached_as_positive_answer()
    {
        var sut = new CachingCoverageClient(_inner, _cache);
        IReadOnlyList<CobEntry> empty = Array.Empty<CobEntry>();
        _inner.GetCobEntriesAsync(TenantId, MemberId, AsOf, false, Arg.Any<CancellationToken>())
            .Returns(empty);

        var first = await sut.GetCobEntriesAsync(TenantId, MemberId, AsOf);
        var second = await sut.GetCobEntriesAsync(TenantId, MemberId, AsOf);

        Assert.NotNull(first);
        Assert.Empty(first!);
        Assert.NotNull(second);
        Assert.Empty(second!);
        await _inner.Received(1)
            .GetCobEntriesAsync(TenantId, MemberId, AsOf, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NullResult_is_not_cached()
    {
        var sut = new CachingCoverageClient(_inner, _cache);
        _inner.GetCobEntriesAsync(TenantId, MemberId, AsOf, false, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CobEntry>?)null,
                     EntryList(new CobEntry { PayerName = "Aetna" }));

        var first = await sut.GetCobEntriesAsync(TenantId, MemberId, AsOf);
        var second = await sut.GetCobEntriesAsync(TenantId, MemberId, AsOf);

        Assert.Null(first);
        Assert.NotNull(second);
        await _inner.Received(2)
            .GetCobEntriesAsync(TenantId, MemberId, AsOf, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForceRefresh_bypasses_cache()
    {
        var sut = new CachingCoverageClient(_inner, _cache);
        var cachedEntries = EntryList(new CobEntry { PayerName = "Aetna", CoverageSequence = "S" });
        var liveEntries = EntryList(new CobEntry { PayerName = "Aetna", CoverageSequence = "P" });

        _inner.GetCobEntriesAsync(TenantId, MemberId, AsOf, false, Arg.Any<CancellationToken>())
            .Returns(cachedEntries);
        _inner.GetCobEntriesAsync(TenantId, MemberId, AsOf, true, Arg.Any<CancellationToken>())
            .Returns(liveEntries);

        await sut.GetCobEntriesAsync(TenantId, MemberId, AsOf);
        var forced = await sut.GetCobEntriesAsync(TenantId, MemberId, AsOf, forceRefresh: true);

        Assert.Equal("P", forced![0].CoverageSequence);
    }

    [Fact]
    public async Task DifferentTenants_do_not_share_cache_entries()
    {
        var sut = new CachingCoverageClient(_inner, _cache);
        _inner.GetCobEntriesAsync("tenant-A", MemberId, AsOf, false, Arg.Any<CancellationToken>())
            .Returns(EntryList(new CobEntry { PayerName = "Aetna" }));
        _inner.GetCobEntriesAsync("tenant-B", MemberId, AsOf, false, Arg.Any<CancellationToken>())
            .Returns(EntryList(new CobEntry { PayerName = "BCBS" }));

        var a = await sut.GetCobEntriesAsync("tenant-A", MemberId, AsOf);
        var b = await sut.GetCobEntriesAsync("tenant-B", MemberId, AsOf);

        Assert.Equal("Aetna", a![0].PayerName);
        Assert.Equal("BCBS", b![0].PayerName);
    }

    [Fact]
    public async Task DifferentMembers_do_not_share_cache_entries()
    {
        var sut = new CachingCoverageClient(_inner, _cache);
        _inner.GetCobEntriesAsync(TenantId, "MEM-A", AsOf, false, Arg.Any<CancellationToken>())
            .Returns(EntryList(new CobEntry { PayerName = "Aetna" }));
        _inner.GetCobEntriesAsync(TenantId, "MEM-B", AsOf, false, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<CobEntry>());

        var a = await sut.GetCobEntriesAsync(TenantId, "MEM-A", AsOf);
        var b = await sut.GetCobEntriesAsync(TenantId, "MEM-B", AsOf);

        Assert.Single(a!);
        Assert.Empty(b!);
    }

    [Fact]
    public void CacheKey_collapses_to_day_granularity()
    {
        var morning = new DateTime(2025, 5, 1, 8, 0, 0, DateTimeKind.Utc);
        var evening = new DateTime(2025, 5, 1, 23, 30, 0, DateTimeKind.Utc);

        var morningKey = CachingCoverageClient.BuildCacheKey(TenantId, MemberId, morning, false);
        var eveningKey = CachingCoverageClient.BuildCacheKey(TenantId, MemberId, evening, false);

        Assert.Equal(morningKey, eveningKey);
    }

    [Fact]
    public void CacheKey_namespaces_force_refresh_separately_from_default_path()
    {
        var defaultKey = CachingCoverageClient.BuildCacheKey(TenantId, MemberId, AsOf, false);
        var forceKey = CachingCoverageClient.BuildCacheKey(TenantId, MemberId, AsOf, true);

        Assert.NotEqual(defaultKey, forceKey);
    }

    [Fact]
    public async Task TtlZero_disables_caching()
    {
        var sut = new CachingCoverageClient(_inner, _cache, TimeSpan.Zero);
        _inner.GetCobEntriesAsync(TenantId, MemberId, AsOf, false, Arg.Any<CancellationToken>())
            .Returns(EntryList(new CobEntry { PayerName = "Aetna" }));

        await sut.GetCobEntriesAsync(TenantId, MemberId, AsOf);
        await sut.GetCobEntriesAsync(TenantId, MemberId, AsOf);

        await _inner.Received(2)
            .GetCobEntriesAsync(TenantId, MemberId, AsOf, false, Arg.Any<CancellationToken>());
    }
}
