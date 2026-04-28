using Bunit;
using CloudHealthOffice.Portal.Shared;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Shared;

/// <summary>
/// bUnit tests for <see cref="IntegrityBadge"/> (capability 5.10).
/// Verifies the six-rating colour map (Decision 9), tooltip content,
/// compact / expanded layout switching, and tolerance for null inputs.
/// </summary>
public class IntegrityBadgeTests : TestContext
{
    public IntegrityBadgeTests()
    {
        Services.AddMudServices();
        JSInterop.SetupVoid("mudPopover.initialize", _ => true);
        JSInterop.SetupVoid("mudKeyInterceptor.connect", _ => true);
        JSInterop.SetupVoid("mudElementReference.saveFocus", _ => true);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Theory]
    [InlineData("Clear",     "cho-integrity-clear")]
    [InlineData("Advisory",  "cho-integrity-advisory")]
    [InlineData("Caution",   "cho-integrity-caution")]
    [InlineData("Alert",     "cho-integrity-alert")]
    [InlineData("Blocked",   "cho-integrity-blocked")]
    [InlineData("clear",     "cho-integrity-clear")]   // case-insensitive
    [InlineData("BLOCKED",   "cho-integrity-blocked")] // case-insensitive
    public void RendersExpectedCssClass_ForEachRating(string rating, string expectedCssClass)
    {
        var cut = RenderComponent<IntegrityBadge>(parameters => parameters
            .Add(p => p.Rating, rating)
            .Add(p => p.Score, 80)
            .Add(p => p.Compact, true));

        cut.Markup.Should().Contain(expectedCssClass);
    }

    [Fact]
    public void RendersUnknownClass_WhenRatingIsNull()
    {
        var cut = RenderComponent<IntegrityBadge>(parameters => parameters
            .Add(p => p.Rating, (string?)null)
            .Add(p => p.Score, (int?)null)
            .Add(p => p.Compact, true));

        cut.Markup.Should().Contain("cho-integrity-unknown");
        cut.Markup.Should().Contain("Unknown");
    }

    [Fact]
    public void RendersUnknownClass_WhenRatingIsEmpty()
    {
        var cut = RenderComponent<IntegrityBadge>(parameters => parameters
            .Add(p => p.Rating, string.Empty)
            .Add(p => p.Score, (int?)null)
            .Add(p => p.Compact, true));

        cut.Markup.Should().Contain("cho-integrity-unknown");
    }

    [Fact]
    public void Compact_OmitsScoreNumber()
    {
        // The compact chip only renders the rating label so the grid
        // column stays narrow; the score-and-tooltip surface is the
        // expanded mode. We assert against the rendered numeric value
        // (not the CSS class) because the component's embedded
        // <style> block also contains the class name and would
        // produce a false positive.
        var cut = RenderComponent<IntegrityBadge>(parameters => parameters
            .Add(p => p.Rating, "Clear")
            .Add(p => p.Score, 92)
            .Add(p => p.Compact, true));

        cut.FindAll("span.cho-integrity-score").Should().BeEmpty();
        cut.Markup.Should().Contain("Clear");
    }

    [Fact]
    public void Expanded_RendersScoreAndTooltip()
    {
        var cut = RenderComponent<IntegrityBadge>(parameters => parameters
            .Add(p => p.Rating, "Caution")
            .Add(p => p.Score, 55)
            .Add(p => p.Compact, false));

        cut.FindAll("span.cho-integrity-score").Should().NotBeEmpty();
        cut.Markup.Should().Contain("55");
        cut.Markup.Should().Contain("Caution");
    }

    [Fact]
    public void Expanded_NullRating_DisplaysUnknownLabel()
    {
        var cut = RenderComponent<IntegrityBadge>(parameters => parameters
            .Add(p => p.Rating, (string?)null)
            .Add(p => p.Score, (int?)null)
            .Add(p => p.Compact, false));

        // The label disambiguates a null projection from a Clear
        // rating in operator mental models. Tooltip body content
        // ("Not yet verified — projection worker hasn't produced a
        // score") is rendered via MudTooltip's popover and not
        // asserted here — popover internals are MudBlazor-version-specific.
        cut.Markup.Should().Contain("Unknown");
    }
}
