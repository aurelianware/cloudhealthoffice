using Microsoft.Extensions.Logging;
using ArService.Controllers;
using ArService.Models;
using ArService.Repositories;

namespace ArService.Tests.Controllers;

/// <summary>
/// Exercises the member AR summary aggregation across posting entries.
/// The controller extracts a static <see cref="MembersArController.BuildSummary"/>
/// so bucket math and charge/payment filtering can be verified without Mongo.
/// </summary>
public class MembersArControllerTests
{
    private readonly Mock<IArBalanceRepository> _repo = new();
    private readonly MembersArController _controller;

    public MembersArControllerTests()
    {
        _controller = new MembersArController(
            _repo.Object,
            new Mock<ILogger<MembersArController>>().Object);
    }

    [Fact]
    public void BuildSummary_AggregatesOnlyMemberPostings_AndBucketsByAge()
    {
        var now = new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc);
        var memberId = "MEM-7";
        var otherMember = "MEM-8";

        var balances = new List<ArBalance>
        {
            new()
            {
                TenantId = "t", GlAccountId = "a1", Period = now,
                PostingEntries = new List<ArPostingEntry>
                {
                    // Member entries → should count
                    new() { MemberId = memberId, DebitAmount = 100m, PostedAt = now.AddDays(-10) },
                    new() { MemberId = memberId, DebitAmount = 200m, PostedAt = now.AddDays(-45) },
                    new() { MemberId = memberId, CreditAmount = 50m, PostedAt = now.AddDays(-80) },
                    new() { MemberId = memberId, DebitAmount = 30m, PostedAt = now.AddDays(-120) },
                    // Another member — must be excluded
                    new() { MemberId = otherMember, DebitAmount = 999m, PostedAt = now.AddDays(-10) }
                }
            }
        };

        var summary = MembersArController.BuildSummary(memberId, balances, now);

        summary.MemberId.Should().Be(memberId);
        summary.CurrentBalance.Should().Be(100m + 200m - 50m + 30m);
        summary.Aged.Bucket0_30.Should().Be(100m);
        summary.Aged.Bucket31_60.Should().Be(200m);
        summary.Aged.Bucket61_90.Should().Be(-50m);
        summary.Aged.Bucket91Plus.Should().Be(30m);
        summary.RecentCharges.Should().HaveCount(3); // three debits
        summary.RecentPayments.Should().HaveCount(1);
    }

    [Fact]
    public void BuildSummary_OrdersRecentChargesByPostedAtDescending()
    {
        var now = new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc);
        var memberId = "MEM-7";
        var balances = new List<ArBalance>
        {
            new()
            {
                TenantId = "t", GlAccountId = "a1", Period = now,
                PostingEntries = new List<ArPostingEntry>
                {
                    new() { MemberId = memberId, DebitAmount = 10m, PostedAt = now.AddDays(-5) },
                    new() { MemberId = memberId, DebitAmount = 20m, PostedAt = now.AddDays(-1) },
                    new() { MemberId = memberId, DebitAmount = 30m, PostedAt = now.AddDays(-10) }
                }
            }
        };

        var summary = MembersArController.BuildSummary(memberId, balances, now);
        summary.RecentCharges.Select(c => c.Amount).Should().ContainInOrder(20m, 10m, 30m);
    }
}
