using CloudHealthOffice.Infrastructure.Tenancy;
using Microsoft.Extensions.Configuration;

namespace CloudHealthOffice.Infrastructure.Tests.Tenancy;

/// <summary>
/// Live core connectors are an optional integration point: off unless a
/// deployment turns them on, and a vendor platform key is never mistaken for CHO.
/// </summary>
public class LiveCoreConnectorsTests
{
    private static IConfiguration Config(string? value) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [LiveCoreConnectors.ConfigKey] = value })
        .Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("no")]
    public void Off_unless_explicitly_enabled(string? value)
        => LiveCoreConnectors.IsEnabled(Config(value)).Should().BeFalse();

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void On_when_enabled(string value)
        => LiveCoreConnectors.IsEnabled(Config(value)).Should().BeTrue();

    [Theory]
    [InlineData("qnxt")]
    [InlineData("QNXT")]
    [InlineData(" facets ")]
    [InlineData("healthedge")]
    public void Vendor_platforms_are_recognized(string platform)
        => LiveCoreConnectors.IsVendorPlatform(platform).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cho")]
    [InlineData("snowflake")]
    public void Cho_and_unknown_keys_are_not_vendor_platforms(string? platform)
        => LiveCoreConnectors.IsVendorPlatform(platform).Should().BeFalse();

    [Fact]
    public void Refusal_is_a_501_and_names_the_platform_and_the_switch()
    {
        var ex = new LiveCoreConnectorNotEnabledException("claims", "qnxt");

        ex.Should().BeAssignableTo<NotImplementedException>();
        ex.Platform.Should().Be("qnxt");
        ex.Domain.Should().Be("claims");
        ex.Message.Should().Contain(LiveCoreConnectors.ConfigKey).And.Contain("file drop");
    }
}
