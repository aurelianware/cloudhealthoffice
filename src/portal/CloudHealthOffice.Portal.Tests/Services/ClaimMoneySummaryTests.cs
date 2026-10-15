using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class ClaimMoneySummaryTests
{
    private static ClaimDetails Claim(string? tier, decimal billed, decimal allowed, decimal paid, params ClaimServiceLine[] lines)
    {
        var claim = new ClaimDetails
        {
            ClaimId = "C1",
            Status = "Paid",
            NetworkTier = tier,
            TotalChargeAmount = billed,
            AllowedAmount = allowed,
            PaidAmount = paid,
        };
        claim.ServiceLines = lines.ToList();
        return claim;
    }

    private static ClaimServiceLine Line(params (string Group, string Carc, decimal Amount)[] cas)
    {
        var line = new ClaimServiceLine { LineNumber = 1 };
        line.Adjustments = cas.Select(c => new ClaimLineAdjustment { GroupCode = c.Group, ReasonCode = c.Carc, Amount = c.Amount }).ToList();
        return line;
    }

    [Fact]
    public void In_network_gap_is_the_providers_contractual_write_off()
    {
        var gap = ClaimMoneySummary.AboveAllowed(Claim("InNetwork", 12963m, 5285m, 5165m));

        gap.Amount.Should().Be(7678m);
        gap.Label.Should().Be("Contractual adjustment");
        gap.NetworkLabel.Should().Be("In-network");
        gap.Explanation.Should().Contain("can't be billed");
        gap.FromRemittance.Should().BeFalse();
    }

    [Fact]
    public void The_lines_CO_adjustments_win_over_billed_minus_allowed()
    {
        // Line 2's second unit denied (CO-151) on top of the CO-45 write-off;
        // the member's copay (PR-3) is not the provider's write-off.
        var gap = ClaimMoneySummary.AboveAllowed(Claim("InNetwork", 1000m, 600m, 580m,
            Line(("CO", "45", 300m), ("PR", "3", 20m)),
            Line(("CO", "151", 100m))));

        gap.Amount.Should().Be(400m);
        gap.FromRemittance.Should().BeTrue();
    }

    [Theory]
    [InlineData("OutOfNetwork", "Out-of-network")]
    [InlineData("OutOfArea", "Out-of-area")]
    public void Out_of_network_gap_may_be_the_members(string tier, string label)
    {
        var gap = ClaimMoneySummary.AboveAllowed(Claim(tier, 500m, 300m, 300m));

        gap.Label.Should().Be("Above allowed amount");
        gap.NetworkLabel.Should().Be(label);
        gap.Explanation.Should().Contain("No Surprises Act");
    }

    [Fact]
    public void Unknown_network_says_so()
    {
        var gap = ClaimMoneySummary.AboveAllowed(Claim(null, 500m, 300m, 300m));

        gap.Network.Should().Be(ClaimNetwork.Unknown);
        gap.NetworkLabel.Should().Be("Network not recorded");
    }

    [Fact]
    public void Nothing_is_shown_before_the_claim_is_priced()
    {
        ClaimMoneySummary.AboveAllowed(Claim("InNetwork", 500m, 0m, 0m)).Amount.Should().BeNull();
    }

    [Theory]
    [InlineData("InNetwork", ClaimNetwork.InNetwork)]
    [InlineData("in-network", ClaimNetwork.InNetwork)]
    [InlineData("Out of Network", ClaimNetwork.OutOfNetwork)]
    [InlineData("", ClaimNetwork.Unknown)]
    public void Network_names(string tier, ClaimNetwork expected) =>
        ClaimMoneySummary.Network(tier).Should().Be(expected);
}
