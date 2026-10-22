using CloudHealthOffice.PlanConfiguration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.TenantService.Tests.Configuration;

/// <summary>The client services use: caching, invalidation and the fallback when tenant-service cannot be read.</summary>
public class PlanSettingsClientTests
{
    private sealed class CountingSettings(IMemoryCache cache, FixedClock clock) : CachingPlanSettings(cache, new PlanSettingsOptions { CacheSeconds = 60 }, clock, NullLogger.Instance)
    {
        public int Fetches { get; private set; }
        public string Value { get; set; } = "3";
        public bool Fail { get; set; }

        protected override Task<ResolvedSetting> FetchAsync(string tenantId, SettingDefinition definition, SettingScope scope, DateTime asOf, CancellationToken ct)
        {
            Fetches++;
            if (Fail) throw new HttpRequestException("tenant-service down");
            return Task.FromResult(new ResolvedSetting { Key = definition.Key, Value = Value, SourceLevel = SettingLevel.Sponsor, SourceId = scope.Sponsor });
        }
    }

    private static readonly string Grace = PlanSettings.OrphanGraceDays.Key;
    private static readonly SettingScope Acme = new() { LineOfBusiness = "Commercial", Sponsor = "ACME" };
    private readonly FixedClock _clock = new(new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task Answers_are_cached_per_tenant_scope_and_date_and_invalidated_by_a_change()
    {
        var settings = new CountingSettings(new MemoryCache(new MemoryCacheOptions()), _clock);

        (await settings.ResolveAsync("t1", Grace, Acme)).Value.Should().Be("3");
        await settings.ResolveAsync("t1", Grace, Acme);
        settings.Fetches.Should().Be(1);

        await settings.ResolveAsync("t2", Grace, Acme);
        await settings.ResolveAsync("t1", Grace, Acme with { Sponsor = "OTHER" });
        await settings.ResolveAsync("t1", Grace, Acme, new DateTime(2027, 1, 1));
        settings.Fetches.Should().Be(4);

        settings.Value = "5";
        settings.Invalidate("t1", Grace);
        (await settings.ResolveAsync("t1", Grace, Acme)).Value.Should().Be("5");
        await settings.ResolveAsync("t2", Grace, Acme);
        settings.Fetches.Should().Be(5, "t2's cached answer was not touched");
    }

    [Fact]
    public async Task An_unreachable_tenant_service_gives_the_default_for_the_line_and_is_not_cached()
    {
        var settings = new CountingSettings(new MemoryCache(new MemoryCacheOptions()), _clock) { Fail = true };

        var r = await settings.ResolveAsync("t1", Grace, Acme);
        r.IsFallback.Should().BeTrue();
        r.Value.Should().Be("7");
        r.SourceLevel.Should().Be(SettingLevel.Platform);

        settings.Fail = false;
        (await settings.ResolveAsync("t1", Grace, Acme)).IsFallback.Should().BeFalse("the next call tries again");
    }

    [Fact]
    public async Task An_undefined_key_is_a_programming_error()
    {
        var settings = new CountingSettings(new MemoryCache(new MemoryCacheOptions()), _clock);
        var act = () => settings.ResolveAsync("t1", "no.such.key", Acme);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task The_in_memory_settings_resolve_with_the_same_rules()
    {
        var settings = new InMemoryPlanSettings();
        settings.Set("t1", Grace, SettingLevel.LineOfBusiness, "Commercial", "9");
        (await settings.ResolveAsync("t1", Grace, Acme)).Value.Should().Be("9");
        (await settings.ResolveAsync("t2", Grace, Acme)).Value.Should().Be("7");
        settings.Unavailable = true;
        (await settings.ResolveAsync("t1", Grace, Acme)).IsFallback.Should().BeTrue();
    }
}
