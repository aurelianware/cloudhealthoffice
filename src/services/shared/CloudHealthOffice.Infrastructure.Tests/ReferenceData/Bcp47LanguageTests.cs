using CloudHealthOffice.Infrastructure.ReferenceData;
using FluentAssertions;

namespace CloudHealthOffice.Infrastructure.Tests.ReferenceData;

public class Bcp47LanguageTests
{
    [Theory]
    [InlineData("en", "English")]
    [InlineData("ES", "Spanish")]
    [InlineData(" vi ", "Vietnamese")]
    public void A_known_tag_gets_its_registry_name(string tag, string display)
        => Bcp47Language.DisplayName(tag).Should().Be(display);

    [Theory]
    [InlineData("xx")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_tag_gets_no_display(string? tag)
        => Bcp47Language.DisplayName(tag).Should().BeNull();
}
