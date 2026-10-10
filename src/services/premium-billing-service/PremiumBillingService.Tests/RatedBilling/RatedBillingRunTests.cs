using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiumBillingService.Clients;
using PremiumBillingService.Controllers;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;

namespace PremiumBillingService.Tests.RatedBilling;

/// <summary>The per-tenant switch in the billing run, and what a Draft may not do.</summary>
public class RatedBillingRunTests
{
    private const string RatedTenant = "tenant-rated";
    private const string OtherTenant = "tenant-unrated";

    private readonly Mock<IBillingRunRepository> _runs = new();
    private readonly Mock<IPremiumInvoiceRepository> _invoices = new();
    private readonly Mock<ISponsorServiceClient> _sponsors = new();
    private readonly Mock<ICoverageServiceClient> _coverage = new();
    private readonly Mock<IRatedInvoiceGenerator> _rated = new();
    private readonly Mock<ICurrentActor> _actor = new();

    private PremiumBillingService.Services.PremiumBillingService Service() => new(
        _runs.Object, _invoices.Object, _sponsors.Object, _coverage.Object, _actor.Object,
        NullLogger<PremiumBillingService.Services.PremiumBillingService>.Instance,
        ratedInvoices: _rated.Object,
        ratedOptions: Options.Create(new RatedBillingOptions
        {
            Tenants = { [RatedTenant] = new RatedBillingTenantOptions { Enabled = true } }
        }));

    private BillingRun Run(string tenant)
    {
        var run = new BillingRun { TenantId = tenant, BillingPeriod = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), Status = BillingRunStatus.Pending };
        _runs.Setup(r => r.GetByIdAsync(run.Id)).ReturnsAsync(run);
        _runs.Setup(r => r.UpdateAsync(It.IsAny<BillingRun>())).ReturnsAsync((BillingRun b) => b);
        return run;
    }

    private static SponsorDto Sponsor(string group) => new() { GroupNumber = group, EmployerName = group };

    private static PremiumInvoice Invoice(string group, InvoiceStatus status, decimal total, int exceptions = 0)
    {
        var invoice = new PremiumInvoice
        {
            InvoiceNumber = $"INV-{group}-2026-03", GroupNumber = group, Status = status, PricingSource = PricingSource.RatingEngine,
            LineItems = { new InvoiceLineItem { MemberId = "M", TotalPremium = total } },
            RatingExceptions = Enumerable.Range(0, exceptions)
                .Select(i => new InvoiceRatingException { Code = "RATE_NOT_FOUND", Message = $"No rate table for plan 'P{i}'" }).ToList()
        };
        invoice.RecalculateTotals();
        return invoice;
    }

    [Fact]
    public async Task RatedTenant_GoesThroughTheRatingEngine_DraftsAndUnchangedInvoicesAreNotBilled()
    {
        var run = Run(RatedTenant);
        _sponsors.Setup(s => s.GetActiveSponsorsAsync(RatedTenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SponsorDto> { Sponsor("G-NEW"), Sponsor("G-DRAFT"), Sponsor("G-DONE") });
        var created = Invoice("G-NEW", InvoiceStatus.Generated, 500m);
        var draft = Invoice("G-DRAFT", InvoiceStatus.Draft, 300m, exceptions: 1);
        var done = Invoice("G-DONE", InvoiceStatus.Sent, 700m);
        _rated.Setup(g => g.GenerateAsync(It.Is<SponsorDto>(s => s.GroupNumber == "G-NEW"), RatedTenant, run.BillingPeriod, run.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RatedInvoiceOutcome(RatedInvoiceOutcomeKind.Created, created));
        _rated.Setup(g => g.GenerateAsync(It.Is<SponsorDto>(s => s.GroupNumber == "G-DRAFT"), RatedTenant, run.BillingPeriod, run.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RatedInvoiceOutcome(RatedInvoiceOutcomeKind.Created, draft));
        _rated.Setup(g => g.GenerateAsync(It.Is<SponsorDto>(s => s.GroupNumber == "G-DONE"), RatedTenant, run.BillingPeriod, run.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RatedInvoiceOutcome(RatedInvoiceOutcomeKind.AlreadyIssued, done));

        var result = await Service().ExecuteBillingRunAsync(run.Id);

        result.Status.Should().Be(BillingRunStatus.Completed);
        result.InvoiceIds.Should().Equal(created.Id);
        result.DraftInvoiceIds.Should().Equal(draft.Id);
        result.UnchangedInvoiceIds.Should().Equal(done.Id);
        result.TotalPremiumAmount.Should().Be(500m);
        result.Warnings.Should().Contain(w => w.Contains("Draft") && w.Contains("No rate table"));
        _coverage.VerifyNoOtherCalls();
        _invoices.Verify(i => i.CreateAsync(It.IsAny<PremiumInvoice>()), Times.Never);
    }

    [Fact]
    public async Task TenantWithoutTheFlag_KeepsTheOriginalPricing()
    {
        var run = Run(OtherTenant);
        _sponsors.Setup(s => s.GetActiveSponsorsAsync(OtherTenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SponsorDto> { Sponsor("G1") });
        _coverage.Setup(c => c.GetActiveCoveragesByGroupAsync(OtherTenant, "G1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CoverageDto>
            {
                new() { CoverageId = "cov-1", MemberId = "M1", GroupNumber = "G1", EffectiveDate = new DateTime(2026, 1, 1), MonthlyPremium = 100m, EmployerContribution = 400m }
            });
        _invoices.Setup(i => i.CreateAsync(It.IsAny<PremiumInvoice>())).ReturnsAsync((PremiumInvoice i) => i);

        var result = await Service().ExecuteBillingRunAsync(run.Id);

        result.TotalPremiumAmount.Should().Be(500m);
        _invoices.Verify(i => i.CreateAsync(It.Is<PremiumInvoice>(p => p.PricingSource == PricingSource.CoveragePremium && p.Status == InvoiceStatus.Generated)));
        _rated.VerifyNoOtherCalls();
    }

    [Fact]
    public void FlagDefaultsOff()
    {
        new RatedBillingOptions().For("any-tenant").Enabled.Should().BeFalse();
        new RatedBillingOptions { Tenants = { ["Tenant-A"] = new() { Enabled = true } } }.For("tenant-a").Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task DraftInvoice_CannotBePaid()
    {
        var draft = Invoice("G1", InvoiceStatus.Draft, 300m);
        _invoices.Setup(i => i.GetByIdAsync(draft.Id)).ReturnsAsync(draft);
        _actor.SetupGet(a => a.UserId).Returns("finance-user");

        var pay = () => Service().RecordPaymentAsync(draft.Id, new RecordPaymentRequest { Amount = 300m, PaymentDate = DateTime.UtcNow });

        await pay.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Draft*");
        _invoices.Verify(i => i.UpdateAsync(It.IsAny<PremiumInvoice>()), Times.Never);
    }

    [Fact]
    public void DraftInvoice_IsNotShownToTheMember()
    {
        var issued = Invoice("G1", InvoiceStatus.Sent, 500m);
        issued.BillingPeriodStart = new DateTime(2026, 2, 1);
        var draft = Invoice("G1", InvoiceStatus.Draft, 600m);
        draft.BillingPeriodStart = new DateTime(2026, 3, 1);

        var summary = MembersPremiumController.Build("M", new[] { issued, draft }, DateTime.UtcNow);

        summary.CurrentInvoice!.Id.Should().Be(issued.Id);
        summary.Last12.Should().ContainSingle();
    }
}
