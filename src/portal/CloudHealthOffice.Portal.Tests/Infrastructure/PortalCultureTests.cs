using System.Globalization;
using CloudHealthOffice.Portal.Infrastructure;

namespace CloudHealthOffice.Portal.Tests.Infrastructure;

public class PortalCultureTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-culture-xx")]
    public void Resolve_FallsBackToEnUs(string? configured)
    {
        var culture = PortalCulture.Resolve(configured);
        culture.Name.Should().Be("en-US");
        1234.5m.ToString("C", culture).Should().Be("$1,234.50");
    }

    [Fact]
    public void Resolve_HonorsConfiguredCulture()
    {
        PortalCulture.Resolve("en-GB").Name.Should().Be("en-GB");
    }

    [Fact]
    public void Resolve_NeverReturnsInvariant()
    {
        PortalCulture.Resolve(CultureInfo.InvariantCulture.Name).Should().NotBe(CultureInfo.InvariantCulture);
    }
}
