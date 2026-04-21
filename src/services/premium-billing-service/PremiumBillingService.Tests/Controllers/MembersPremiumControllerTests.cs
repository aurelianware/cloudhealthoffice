using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using PremiumBillingService.Controllers;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.Tests.Controllers;

/// <summary>
/// Verifies the member premium rollup — in particular the APTC-aware grace
/// period math, which is the spec's acceptance criterion.
/// </summary>
public class MembersPremiumControllerTests
{
    private readonly Mock<IPremiumInvoiceRepository> _repo = new();
    private readonly MembersPremiumController _controller;

    public MembersPremiumControllerTests()
    {
        _controller = new MembersPremiumController(
            _repo.Object,
            new Mock<ILogger<MembersPremiumController>>().Object);
    }

    [Fact]
    public async Task GetPremiumSummary_NoInvoices_ReturnsEmptySummary()
    {
        _repo.Setup(r => r.ListByMemberAsync("MEM-1", 12))
            .ReturnsAsync(new List<PremiumInvoice>());

        var result = await _controller.GetPremiumSummary("MEM-1");
        var summary = (result.Result as OkObjectResult)!.Value as MemberPremiumSummary;
        summary.Should().NotBeNull();
        summary!.CurrentInvoice.Should().BeNull();
        summary.Grace.IsInGrace.Should().BeFalse();
    }

    [Fact]
    public void ComputeGrace_AptcInvoiceInThirdMonth_SetsAptcGraceWithDayCounter()
    {
        var now = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var invoice = new PremiumInvoice
        {
            DueDate = now.AddDays(-45),                 // billed 45 days ago
            GracePeriodExpires = now.AddDays(45),       // APTC window still open
            GracePeriodDays = 30,
            GraceType = GraceType.AptcThreeMonth,
            IsAptcSubsidized = true,
            AptcMonthlyAmount = 320.00m,
            Status = InvoiceStatus.Overdue,
            BalanceDue = 500m
        };

        var state = MembersPremiumController.ComputeGrace(invoice, now);

        state.IsInGrace.Should().BeTrue();
        state.GraceType.Should().Be(GraceType.AptcThreeMonth);
        state.DaysRemaining.Should().Be(45);
        state.ExpiresOn.Should().Be(invoice.GracePeriodExpires);
    }

    [Fact]
    public void ComputeGrace_StandardInvoicePastGraceExpiry_NoGrace()
    {
        var now = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var invoice = new PremiumInvoice
        {
            DueDate = now.AddDays(-40),
            GracePeriodExpires = now.AddDays(-5),      // already past
            GracePeriodDays = 30,
            GraceType = GraceType.Standard,
            Status = InvoiceStatus.Delinquent,
            BalanceDue = 500m
        };

        var state = MembersPremiumController.ComputeGrace(invoice, now);
        state.IsInGrace.Should().BeFalse();
    }

    [Fact]
    public void ComputeGrace_PaidInvoice_IsNeverInGrace()
    {
        var now = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var invoice = new PremiumInvoice
        {
            DueDate = now.AddDays(-10),
            GracePeriodExpires = now.AddDays(20),
            Status = InvoiceStatus.Paid,
            BalanceDue = 0m
        };

        MembersPremiumController.ComputeGrace(invoice, now).IsInGrace.Should().BeFalse();
    }

    [Fact]
    public void Build_PicksMostRecentInvoiceAsCurrent()
    {
        var memberId = "MEM-42";
        var newest = MakeInvoice(memberId, 2026, 3);
        var oldest = MakeInvoice(memberId, 2025, 12);

        var summary = MembersPremiumController.Build(memberId,
            new[] { oldest, newest },
            new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc));

        summary.CurrentInvoice!.InvoiceNumber.Should().Be(newest.InvoiceNumber);
        summary.Last12.Should().HaveCount(2);
        summary.NextBillDate.Should().Be(newest.BillingPeriodEnd.AddDays(1));
    }

    private static PremiumInvoice MakeInvoice(string memberId, int year, int month)
    {
        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1).AddDays(-1);
        return new PremiumInvoice
        {
            Id = Guid.NewGuid().ToString(),
            InvoiceNumber = $"INV-{year}-{month:D2}",
            GroupNumber = "GRP-1",
            BillingPeriodStart = start,
            BillingPeriodEnd = end,
            DueDate = end,
            Status = InvoiceStatus.Sent,
            LineItems = new List<InvoiceLineItem>
            {
                new() { MemberId = memberId, TotalPremium = 100m }
            }
        };
    }
}
