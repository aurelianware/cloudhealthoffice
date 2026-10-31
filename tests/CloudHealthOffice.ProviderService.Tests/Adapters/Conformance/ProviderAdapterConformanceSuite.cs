using System.Text.RegularExpressions;
using ProviderService.Adapters;
using ProviderService.Models;

namespace CloudHealthOffice.ProviderService.Tests.Adapters.Conformance;

/// <summary>
/// Records a connector's test environment must contain for the provider
/// conformance suite. A vendor connector points these at known records in the
/// payer's sandbox. The reference run seeds them in memory.
/// </summary>
public sealed record ProviderAdapterConformanceFixture(
    IProviderAdapter Adapter,
    string TenantId,
    string ExpectedPlatform,
    string KnownProviderId,
    string KnownNpi,
    string MissingProviderId,
    string MissingNpi,
    Dictionary<string, string>? PlatformSettings = null);

/// <summary>
/// Behavior every <see cref="IProviderAdapter"/> must show, whatever the
/// backend. Derive from this class, return a fixture from
/// <see cref="ArrangeAsync"/>, and every fact below runs against your
/// connector. See docs/integration/CORE-ADAPTER-IMPLEMENTER-GUIDE.md.
/// </summary>
/// <remarks>
/// <see cref="IProviderAdapter.GetNetworkAsync"/> is not covered: the CHO
/// reference adapter does not implement it yet either.
/// </remarks>
public abstract class ProviderAdapterConformanceSuite
{
    private static readonly Regex PlatformKey = new("^[a-z0-9-]+$", RegexOptions.CultureInvariant);

    protected abstract Task<ProviderAdapterConformanceFixture> ArrangeAsync();

    private static ProviderAdapterRequest Request(ProviderAdapterConformanceFixture f) => new()
    {
        TenantId = f.TenantId,
        PlatformSettings = new Dictionary<string, string>(f.PlatformSettings ?? new()),
    };

    [Fact]
    public async Task Platform_is_the_lowercase_key_tenants_configure()
    {
        var f = await ArrangeAsync();

        f.Adapter.Platform.Should().Be(f.ExpectedPlatform);
        PlatformKey.IsMatch(f.Adapter.Platform).Should().BeTrue(
            "the factory matches configuration.providerPlatform.platform against this key");
    }

    [Fact]
    public async Task GetProviderAsync_returns_the_known_provider_in_the_callers_tenant()
    {
        var f = await ArrangeAsync();
        var request = Request(f);
        request.ProviderId = f.KnownProviderId;

        var response = await f.Adapter.GetProviderAsync(request);

        response.Platform.Should().Be(f.Adapter.Platform);
        response.Provider.Should().NotBeNull();
        response.Provider!.ProviderId.Should().Be(f.KnownProviderId);
        response.Provider.Npi.Should().Be(f.KnownNpi);
        response.Provider.TenantId.Should().Be(f.TenantId);
    }

    [Fact]
    public async Task GetProviderAsync_returns_no_provider_rather_than_throwing_when_not_found()
    {
        var f = await ArrangeAsync();
        var request = Request(f);
        request.ProviderId = f.MissingProviderId;

        var response = await f.Adapter.GetProviderAsync(request);

        response.Provider.Should().BeNull();
    }

    [Fact]
    public async Task GetProviderAsync_rejects_a_request_without_a_provider_id()
    {
        var f = await ArrangeAsync();

        var act = () => f.Adapter.GetProviderAsync(Request(f));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetProviderByNpiAsync_finds_the_known_provider_and_misses_cleanly()
    {
        var f = await ArrangeAsync();
        var known = Request(f);
        known.Npi = f.KnownNpi;
        var missing = Request(f);
        missing.Npi = f.MissingNpi;

        var found = await f.Adapter.GetProviderByNpiAsync(known);
        var notFound = await f.Adapter.GetProviderByNpiAsync(missing);

        found.Provider.Should().NotBeNull();
        found.Provider!.Npi.Should().Be(f.KnownNpi);
        found.Provider.TenantId.Should().Be(f.TenantId);
        notFound.Provider.Should().BeNull();
    }

    [Fact]
    public async Task GetProviderByNpiAsync_rejects_a_request_without_an_npi()
    {
        var f = await ArrangeAsync();

        var act = () => f.Adapter.GetProviderByNpiAsync(Request(f));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SearchProvidersAsync_honors_the_page_size_and_stays_in_the_tenant()
    {
        var f = await ArrangeAsync();
        var request = Request(f);
        request.Page = 1;
        request.PageSize = 1;

        var response = await f.Adapter.SearchProvidersAsync(request);

        response.Platform.Should().Be(f.Adapter.Platform);
        response.Providers.Should().NotBeEmpty();
        response.Providers.Count.Should().BeLessThanOrEqualTo(1);
        response.Providers.Should().OnlyContain(p => p.TenantId == f.TenantId);
        if (response.TotalCount is { } total)
            total.Should().BeGreaterThanOrEqualTo(response.Providers.Count);
    }

    [Fact]
    public async Task GetNetworkRosterAsync_stays_in_the_tenant()
    {
        var f = await ArrangeAsync();

        var response = await f.Adapter.GetNetworkRosterAsync(Request(f));

        response.Platform.Should().Be(f.Adapter.Platform);
        response.Providers.Should().OnlyContain(p => p.TenantId == f.TenantId);
    }
}
