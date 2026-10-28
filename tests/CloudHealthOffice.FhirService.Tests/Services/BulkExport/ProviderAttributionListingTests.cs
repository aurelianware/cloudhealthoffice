using FhirService.Services.ProviderAccess;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.Services.BulkExport;

public class ProviderAttributionListingTests
{
    private static ConfiguredProviderAttributionSource Configured() => new(Options.Create(new ProviderAttributionOptions
    {
        PanelsByTenant = new()
        {
            ["t1"] =
            [
                new ConfiguredProviderPanel { ProviderId = "p1", MemberIds = ["m1", "m2", "", "m1"] },
                new ConfiguredProviderPanel { ProviderId = "p1", MemberIds = ["m3"] },
                new ConfiguredProviderPanel { ProviderId = "p2", MemberIds = ["m9"] },
            ],
        },
    }));

    [Fact]
    public async Task Configured_ListsTheProvidersPanels_InItsTenantOnly()
    {
        var source = Configured();
        (await source.ListAttributedAsync("t1", "p1")).Should().BeEquivalentTo("m1", "m2", "m3");
        (await source.ListAttributedAsync("t2", "p1")).Should().BeEmpty();
        (await source.ListAttributedAsync("t1", "unknown")).Should().BeEmpty();
        (await source.ListAttributedAsync("t1", " ")).Should().BeEmpty();
    }

    [Fact]
    public async Task SourceThatCannotList_ListsNoOne()
    {
        IProviderAttributionSource source = new Mock<IProviderAttributionSource> { CallBase = true }.Object;
        (await source.ListAttributedAsync("t1", "p1")).Should().BeEmpty();
    }

    [Fact]
    public async Task Composite_ListsEveryoneAnySourceLists()
    {
        var other = new Mock<IProviderAttributionSource>();
        other.Setup(s => s.ListAttributedAsync("t1", "p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["m3", "m4"]);
        var composite = new CompositeProviderAttributionSource([Configured(), other.Object]);

        (await composite.ListAttributedAsync("t1", "p1")).Should().BeEquivalentTo("m1", "m2", "m3", "m4");
    }

    [Fact]
    public async Task Composite_ASourceThatFails_StopsTheListing()
    {
        var failing = new Mock<IProviderAttributionSource>();
        failing.Setup(s => s.ListAttributedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("down"));
        var composite = new CompositeProviderAttributionSource([Configured(), failing.Object]);

        await composite.Invoking(c => c.ListAttributedAsync("t1", "p1")).Should().ThrowAsync<HttpRequestException>();
    }
}
