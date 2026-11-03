using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class ShadowGapGuideTests
{
    [Theory]
    [InlineData("NOCONTRACT", ShadowGapGuide.GapAction.LoadData, "/fee-schedules")]
    [InlineData("nocontract", ShadowGapGuide.GapAction.LoadData, "/fee-schedules")]
    [InlineData("MEMBERNOTLOADED", ShadowGapGuide.GapAction.LoadData, "/edi-transactions")]
    [InlineData("PLANUNAVAIL", ShadowGapGuide.GapAction.LoadData, "/benefit-plans/templates")]
    [InlineData("NOAUTH", ShadowGapGuide.GapAction.LoadData, "/edi-transactions")]
    [InlineData("SVCUNAVAIL", ShadowGapGuide.GapAction.RunAgain, null)]
    [InlineData("MEDREVIEW", ShadowGapGuide.GapAction.ExpectedReview, null)]
    public void Each_gap_says_what_to_do_and_where(string code, ShadowGapGuide.GapAction action, string? page)
    {
        var gap = ShadowGapGuide.For(code);

        gap.Action.Should().Be(action);
        gap.Missing.Should().NotBeNullOrWhiteSpace();
        gap.Fix.Should().NotBeNullOrWhiteSpace();
        if (page is not null)
            gap.Links.Select(l => l.Href).Should().Contain(page);
    }

    [Fact]
    public void A_stage_rejection_and_an_unknown_code_still_get_guidance()
    {
        ShadowGapGuide.For("REJECT:Eligibility").Missing.Should().Contain("Eligibility stage");
        ShadowGapGuide.For("SOMETHINGNEW").Fix.Should().NotBeNullOrWhiteSpace();
        ShadowGapGuide.For(null).Code.Should().Be("UNKNOWN");
    }

    [Fact]
    public void Guide_links_point_at_docs_that_exist()
    {
        var root = FindRepoRoot();
        var guides = new[] { "NOCONTRACT", "MEMBERNOTLOADED", "PLANUNAVAIL", "NETWORK", "NOAUTH", "TPL", "CONFIGUNAVAIL", "SCRUB", "PROVIDERSCREEN", "REJECT:X", "UNKNOWN" }
            .SelectMany(c => ShadowGapGuide.For(c).Links)
            .Where(l => l.Href.Contains("/docs/integration/"))
            .Select(l => l.Href[(l.Href.IndexOf("/docs/integration/") + 1)..].Split('#')[0])
            .Distinct();
        foreach (var path in guides)
            File.Exists(Path.Combine(root, path)).Should().BeTrue($"{path} is linked from the gap guide");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "integration")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
