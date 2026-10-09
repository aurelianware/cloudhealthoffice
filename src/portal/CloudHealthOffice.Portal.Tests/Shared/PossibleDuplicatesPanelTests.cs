using Bunit;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Shared;

/// <summary>
/// bUnit tests for <see cref="PossibleDuplicatesPanel"/>: the claim-detail
/// panel listing prior claims a DUPLICATE-pended claim matched.
/// </summary>
public class PossibleDuplicatesPanelTests : TestContext
{
    public PossibleDuplicatesPanelTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static ClaimDuplicateMatch Exact() => new()
    {
        MatchedClaimId = "prior-exact",
        MatchedClaimNumber = "CLM-PRIOR-A",
        MatchType = "Exact",
        MatchedClaimFound = true,
        ServiceDateFrom = new DateTime(2026, 5, 1),
        ServiceDateTo = new DateTime(2026, 5, 1),
        BilledAmount = 100m,
        Status = "Paid",
        MatchedFields = new() { "Member", "Billing provider", "Service dates", "Procedure code" },
        Lines = new() { new ClaimDuplicateLineMatch { LineNumber = 1, MatchedLineNumber = 2, MatchType = "Exact", RuleId = "DUP001" } },
    };

    private static ClaimDuplicateMatch SuspectNotFound() => new()
    {
        MatchedClaimId = "prior-gone",
        MatchedClaimNumber = null,
        MatchType = "Suspect",
        MatchedClaimFound = false,
        MatchedFields = new() { "Member", "Service dates" },
    };

    [Fact]
    public void Renders_nothing_when_matches_are_null()
    {
        var cut = RenderComponent<PossibleDuplicatesPanel>(p => p.Add(x => x.Matches, null));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Renders_nothing_when_matches_are_empty()
    {
        var cut = RenderComponent<PossibleDuplicatesPanel>(p => p.Add(x => x.Matches, new List<ClaimDuplicateMatch>()));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Lists_each_match_with_link_dates_billed_status_type_and_fields()
    {
        var cut = RenderComponent<PossibleDuplicatesPanel>(p => p.Add(x => x.Matches,
            new List<ClaimDuplicateMatch> { Exact(), SuspectNotFound() }));

        cut.Find("[data-testid=possible-duplicates]").TextContent.Should().Contain("Possible duplicates");
        cut.FindAll("tbody tr").Should().HaveCount(2);

        var row = cut.Find("[data-testid=duplicate-row-prior-exact]");
        var link = row.QuerySelector("a")!;
        link.GetAttribute("href").Should().Be("/claims/prior-exact");
        link.TextContent.Trim().Should().Be("CLM-PRIOR-A");
        row.TextContent.Should().Contain("05/01/2026");
        row.TextContent.Should().Contain(100m.ToString("C"));
        row.TextContent.Should().Contain("Paid");
        row.TextContent.Should().Contain("Exact");
        row.TextContent.Should().Contain("Billing provider");
        row.TextContent.Should().Contain("Procedure code");
        row.TextContent.Should().Contain("Line 1 ↔ 2");
    }

    [Fact]
    public void Unreadable_match_falls_back_to_claim_id_and_placeholders()
    {
        var cut = RenderComponent<PossibleDuplicatesPanel>(p => p.Add(x => x.Matches,
            new List<ClaimDuplicateMatch> { SuspectNotFound() }));

        var row = cut.Find("[data-testid=duplicate-row-prior-gone]");
        row.QuerySelector("a")!.TextContent.Trim().Should().Be("prior-gone");
        row.TextContent.Should().Contain("Not available");
        row.TextContent.Should().Contain("Suspect");
        row.TextContent.Should().Contain("—");
        cut.Find("[data-testid=possible-duplicates]").TextContent.Should().Contain("1 prior claim.");
    }

    [Fact]
    public void Service_date_range_renders_both_ends()
    {
        var match = Exact();
        match.ServiceDateTo = new DateTime(2026, 5, 3);

        var cut = RenderComponent<PossibleDuplicatesPanel>(p => p.Add(x => x.Matches,
            new List<ClaimDuplicateMatch> { match }));

        cut.Find("[data-testid=duplicate-row-prior-exact]").TextContent
            .Should().Contain("05/01/2026 – 05/03/2026");
    }
}
