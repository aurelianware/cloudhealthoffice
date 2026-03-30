using PremiumBillingService.Models;

namespace PremiumBillingService.Tests.Models;

public class PremiumInvoiceModelTests
{
    [Fact]
    public void RecalculateTotals_SumsLineItems()
    {
        var invoice = new PremiumInvoice
        {
            LineItems = new List<InvoiceLineItem>
            {
                new() { MemberId = "m1", TotalPremium = 500, SubscriberPremium = 300, EmployerContribution = 200 },
                new() { MemberId = "m2", TotalPremium = 700, SubscriberPremium = 400, EmployerContribution = 300 }
            }
        };

        invoice.RecalculateTotals();

        invoice.SubtotalPremium.Should().Be(1200);
        invoice.MemberCount.Should().Be(2);
    }

    [Fact]
    public void RecalculateTotals_IncludesAdjustments()
    {
        var invoice = new PremiumInvoice
        {
            LineItems = new List<InvoiceLineItem>
            {
                new() { MemberId = "m1", TotalPremium = 1000 }
            },
            Adjustments = new List<InvoiceAdjustment>
            {
                new() { Amount = -50, Type = AdjustmentType.Credit, Description = "Discount" },
                new() { Amount = 25, Type = AdjustmentType.RetroAdd, Description = "Retro add" }
            }
        };

        invoice.RecalculateTotals();

        invoice.TotalAdjustments.Should().Be(-25);
        invoice.TotalAmount.Should().Be(975);
    }

    [Fact]
    public void RecalculateTotals_SubtractsPayments()
    {
        var invoice = new PremiumInvoice
        {
            LineItems = new List<InvoiceLineItem>
            {
                new() { MemberId = "m1", TotalPremium = 1000 }
            },
            Payments = new List<InvoicePayment>
            {
                new() { Amount = 400 },
                new() { Amount = 200 }
            }
        };

        invoice.RecalculateTotals();

        invoice.TotalPaid.Should().Be(600);
        invoice.BalanceDue.Should().Be(400);
    }

    [Fact]
    public void RecalculateTotals_DuplicateMemberIds_CountedOnce()
    {
        var invoice = new PremiumInvoice
        {
            LineItems = new List<InvoiceLineItem>
            {
                new() { MemberId = "m1", TotalPremium = 500 },
                new() { MemberId = "m1", TotalPremium = 300 }, // same member, different coverage
                new() { MemberId = "m2", TotalPremium = 400 }
            }
        };

        invoice.RecalculateTotals();

        invoice.MemberCount.Should().Be(2);
        invoice.SubtotalPremium.Should().Be(1200);
    }

    [Fact]
    public void RecalculateTotals_EmptyInvoice_AllZeros()
    {
        var invoice = new PremiumInvoice();

        invoice.RecalculateTotals();

        invoice.SubtotalPremium.Should().Be(0);
        invoice.TotalAdjustments.Should().Be(0);
        invoice.TotalAmount.Should().Be(0);
        invoice.TotalPaid.Should().Be(0);
        invoice.BalanceDue.Should().Be(0);
        invoice.MemberCount.Should().Be(0);
    }

    [Fact]
    public void RecalculateTotals_OverpaidInvoice_NegativeBalance()
    {
        var invoice = new PremiumInvoice
        {
            LineItems = new List<InvoiceLineItem>
            {
                new() { MemberId = "m1", TotalPremium = 500 }
            },
            Payments = new List<InvoicePayment>
            {
                new() { Amount = 600 }
            }
        };

        invoice.RecalculateTotals();

        invoice.BalanceDue.Should().Be(-100);
    }
}
