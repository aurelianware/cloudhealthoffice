using WorkManagementService.Domain;
using WorkManagementService.Models;

namespace WorkManagementService.Tests;

public class RoutingTests
{
    private static List<WorkQueue> Defaults() =>
        DefaultClaimQueues.All.Select(d => DefaultClaimQueues.Create(d, "t", DateTime.UtcNow)).ToList();

    private static WorkItemAttributes Claim(string? primary, params string[] others) => new()
    {
        PrimaryPendCode = primary,
        PendCodes = (primary is null ? others : others.Prepend(primary)).ToList(),
        Amount = 500m,
        LineOfBusiness = "Commercial",
        ClaimType = "Professional",
    };

    /// <summary>The default queues reproduce claims-service's work-queue categories (MapPendReason).</summary>
    [Theory]
    [InlineData("NCCI", "claim.ncci")]
    [InlineData("MUE", "claim.ncci")]
    [InlineData("AUTH", "claim.auth")]
    [InlineData("NOAUTH", "claim.auth")]
    [InlineData("PAUNAVAIL", "claim.auth")]
    [InlineData("OON", "claim.oon")]
    [InlineData("NOCONTRACT", "claim.oon")]
    [InlineData("COB", "claim.cob")]
    [InlineData("MEDREVIEW", "claim.medreview")]
    [InlineData("CLINICAL", "claim.medreview")]
    [InlineData("SVCUNAVAIL", "claim.svcunavail")]
    [InlineData("PLANUNAVAIL", "claim.svcunavail")]
    [InlineData("DUPLICATE", "claim.duplicate")]
    [InlineData("PLANEDIT", "claim.planedit")]
    [InlineData("PRICING", "claim.pricing")]
    [InlineData("RETROELIG", "claim.other")]
    [InlineData("SUBRO", "claim.other")]
    [InlineData(null, "claim.other")]
    public void DefaultQueues_RouteByThePrimaryPendCode(string? code, string expectedKey)
    {
        var queue = WorkRouter.Route(WorkType.Claim, Claim(code), Defaults());

        queue!.SystemKey.Should().Be(expectedKey);
    }

    [Fact]
    public void AnAdditionalPendCode_DoesNotRoute_UnlessTheQueueMatchesAnyCode()
    {
        var queues = Defaults();
        var attributes = Claim("NCCI", "COB");

        WorkRouter.Route(WorkType.Claim, attributes, queues)!.SystemKey.Should().Be("claim.ncci");

        var cob = queues.Single(q => q.SystemKey == "claim.cob");
        cob.Order = 1;
        cob.Criteria.MatchAnyPendCode = true;
        WorkRouter.Route(WorkType.Claim, attributes, queues)!.SystemKey.Should().Be("claim.cob");
    }

    [Fact]
    public void FirstMatchingActiveQueueByOrder_Wins_InactiveQueuesAreSkipped()
    {
        var queues = Defaults();
        var highDollar = new WorkQueue
        {
            Id = "high", Name = "High dollar", WorkType = WorkType.Claim, Order = 5,
            Criteria = new ClaimQueueCriteria { MinAmount = 10_000m },
        };
        queues.Add(highDollar);

        WorkRouter.Route(WorkType.Claim, Claim("NCCI").WithAmount(25_000m), queues)!.Id.Should().Be("high");
        WorkRouter.Route(WorkType.Claim, Claim("NCCI").WithAmount(9_999m), queues)!.SystemKey.Should().Be("claim.ncci");

        highDollar.IsActive = false;
        WorkRouter.Route(WorkType.Claim, Claim("NCCI").WithAmount(25_000m), queues)!.SystemKey.Should().Be("claim.ncci");
    }

    [Fact]
    public void EveryCriterion_MustHold()
    {
        var c = new ClaimQueueCriteria
        {
            PendCodes = ["COB"],
            LinesOfBusiness = ["Medicaid"],
            ClaimTypes = ["Institutional"],
            MinAmount = 100m,
            MaxAmount = 1000m,
            ProviderNpis = ["1111111111"],
            NetworkTiers = ["OutOfNetwork"],
            Expedited = true,
        };
        var match = new WorkItemAttributes
        {
            PrimaryPendCode = "cob", PendCodes = ["COB"], LineOfBusiness = "medicaid", ClaimType = "Institutional",
            Amount = 500m, ProviderNpi = "1111111111", NetworkTier = "OutOfNetwork", Expedited = true,
        };

        WorkRouter.MatchesClaim(c, match).Should().BeTrue();
        WorkRouter.MatchesClaim(c, Copy(match, a => a.LineOfBusiness = "Commercial")).Should().BeFalse();
        WorkRouter.MatchesClaim(c, Copy(match, a => a.ClaimType = "Professional")).Should().BeFalse();
        WorkRouter.MatchesClaim(c, Copy(match, a => a.Amount = 1000.01m)).Should().BeFalse();
        WorkRouter.MatchesClaim(c, Copy(match, a => a.Amount = 99m)).Should().BeFalse();
        WorkRouter.MatchesClaim(c, Copy(match, a => a.ProviderNpi = null)).Should().BeFalse();
        WorkRouter.MatchesClaim(c, Copy(match, a => a.NetworkTier = "InNetwork")).Should().BeFalse();
        WorkRouter.MatchesClaim(c, Copy(match, a => a.Expedited = false)).Should().BeFalse();
        WorkRouter.MatchesClaim(c, Copy(match, a => a.ProviderTin = "12-3456789")).Should().BeTrue("an unset criterion does not constrain");
    }

    [Fact]
    public void NoFallback_AndNoMatch_IsUnrouted()
    {
        var queues = Defaults().Where(q => !q.IsFallback).ToList();

        WorkRouter.Route(WorkType.Claim, Claim("SUBRO"), queues).Should().BeNull();
    }

    [Fact]
    public void OtherWorkTypes_NeverLandInClaimQueues()
    {
        WorkRouter.Route(WorkType.Appeal, Claim("NCCI"), Defaults()).Should().BeNull();
    }

    private static WorkItemAttributes Copy(WorkItemAttributes a, Action<WorkItemAttributes> change)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<WorkItemAttributes>(System.Text.Json.JsonSerializer.Serialize(a))!;
        change(copy);
        return copy;
    }
}

internal static class AttributeExtensions
{
    public static WorkItemAttributes WithAmount(this WorkItemAttributes a, decimal amount)
    {
        a.Amount = amount;
        return a;
    }
}

public class PriorityScorerTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly WorkQueue Queue = new() { SlaDays = 30, Weights = new PriorityWeights() };

    private static double Score(int receivedDaysAgo, decimal amount = 0m, bool expedited = false, DateTime? deadline = null) =>
        PriorityScorer.Score(new WorkItemAttributes
        {
            ReceivedDate = Now.AddDays(-receivedDaysAgo), Amount = amount, Expedited = expedited, Deadline = deadline,
        }, Queue, Now.AddDays(-receivedDaysAgo), Now).Score;

    [Fact]
    public void CloserToTheDeadline_ScoresHigher()
    {
        Score(25).Should().BeGreaterThan(Score(10));
        Score(10).Should().BeGreaterThan(Score(1));
    }

    [Fact]
    public void Overdue_OutranksAnythingStillInTime()
    {
        Score(31).Should().BeGreaterThan(Score(29, amount: 1_000_000m));
        Score(40).Should().BeGreaterThan(Score(31));
    }

    [Fact]
    public void AmountAndExpedited_RaiseTheScore()
    {
        Score(5, amount: 50_000m).Should().BeGreaterThan(Score(5, amount: 50m));
        Score(5, expedited: true).Should().BeGreaterThan(Score(5));
    }

    [Fact]
    public void TheFormula_IsTheDocumentedOne()
    {
        // 15 of 30 days left: deadline 50; age 15d: 30; $999: 20 × log10(1000) = 60.
        var result = PriorityScorer.Score(new WorkItemAttributes { ReceivedDate = Now.AddDays(-15), Amount = 999m },
            Queue, Now.AddDays(-15), Now);

        result.Score.Should().Be(Math.Round(1.0 * 50 + 0.5 * 30 + 0.25 * 60, 2));
        result.DueAt.Should().Be(Now.AddDays(15));
        result.Reason.Should().Contain("due in 15d").And.Contain("$999");
    }

    [Fact]
    public void AnExplicitDeadline_OverridesTheQueueSla()
    {
        var due = Now.AddDays(1);
        PriorityScorer.DueAt(new WorkItemAttributes { Deadline = due, ReceivedDate = Now.AddDays(-2) }, Queue, Now).Should().Be(due);
    }

    [Fact]
    public void Weights_ArePerQueue()
    {
        var amountOnly = new WorkQueue { SlaDays = 30, Weights = new PriorityWeights { Deadline = 0, Age = 0, Amount = 1, Expedited = 0 } };
        var big = PriorityScorer.Score(new WorkItemAttributes { Amount = 99_999m, ReceivedDate = Now }, amountOnly, Now, Now);

        big.Score.Should().Be(100);
    }
}
