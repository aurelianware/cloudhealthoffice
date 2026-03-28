using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using PremiumBillingService.Controllers;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;

namespace PremiumBillingService.Tests.Controllers;

public class PremiumInvoicesControllerTests
{
    private readonly Mock<IPremiumBillingService> _billingService;
    private readonly Mock<IPremiumInvoiceRepository> _invoiceRepository;
    private readonly PremiumInvoicesController _controller;

    public PremiumInvoicesControllerTests()
    {
        _billingService = new Mock<IPremiumBillingService>();
        _invoiceRepository = new Mock<IPremiumInvoiceRepository>();
        var logger = new Mock<ILogger<PremiumInvoicesController>>();
        _controller = new PremiumInvoicesController(
            _billingService.Object, _invoiceRepository.Object, logger.Object);
    }

    #region SearchInvoices

    [Fact]
    public async Task SearchInvoices_WithFilters_Returns200()
    {
        var invoices = new List<PremiumInvoice>
        {
            new() { Id = "inv-1", GroupNumber = "GRP001" },
            new() { Id = "inv-2", GroupNumber = "GRP001" }
        };
        _invoiceRepository.Setup(r => r.SearchAsync("GRP001", null, null, null, 1, 50))
            .ReturnsAsync(invoices);

        var result = await _controller.SearchInvoices("GRP001", null, null, null, 1, 50);

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeAssignableTo<IEnumerable<PremiumInvoice>>().Subject;
        returned.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchInvoices_NoFilters_Returns200()
    {
        _invoiceRepository.Setup(r => r.SearchAsync(null, null, null, null, 1, 50))
            .ReturnsAsync(new List<PremiumInvoice>());

        var result = await _controller.SearchInvoices(null, null, null, null, 1, 50);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    #endregion

    #region GetInvoiceById

    [Fact]
    public async Task GetInvoiceById_Found_Returns200()
    {
        var invoice = new PremiumInvoice { Id = "inv-1", InvoiceNumber = "INV-GRP001-2026-03" };
        _invoiceRepository.Setup(r => r.GetByIdAsync("inv-1"))
            .ReturnsAsync(invoice);

        var result = await _controller.GetInvoiceById("inv-1");

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<PremiumInvoice>().Subject;
        returned.InvoiceNumber.Should().Be("INV-GRP001-2026-03");
    }

    [Fact]
    public async Task GetInvoiceById_NotFound_Returns404()
    {
        _invoiceRepository.Setup(r => r.GetByIdAsync("missing"))
            .ReturnsAsync((PremiumInvoice?)null);

        var result = await _controller.GetInvoiceById("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region GetInvoicesBySponsor

    [Fact]
    public async Task GetInvoicesBySponsor_Returns200()
    {
        var invoices = new List<PremiumInvoice>
        {
            new() { Id = "inv-1", GroupNumber = "GRP001" }
        };
        _invoiceRepository.Setup(r => r.GetByGroupNumberAsync("GRP001"))
            .ReturnsAsync(invoices);

        var result = await _controller.GetInvoicesBySponsor("GRP001");

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeAssignableTo<IEnumerable<PremiumInvoice>>().Subject;
        returned.Should().HaveCount(1);
    }

    #endregion

    #region RecordPayment

    [Fact]
    public async Task RecordPayment_Success_Returns200()
    {
        var request = new RecordPaymentRequest { Amount = 500, PaymentDate = DateTime.UtcNow };
        var invoice = new PremiumInvoice { Id = "inv-1", Status = InvoiceStatus.Paid };
        _billingService.Setup(s => s.RecordPaymentAsync("inv-1", request))
            .ReturnsAsync(invoice);

        var result = await _controller.RecordPayment("inv-1", request);

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<PremiumInvoice>().Subject;
        returned.Status.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public async Task RecordPayment_InvalidOperation_Returns400()
    {
        var request = new RecordPaymentRequest { Amount = 500, PaymentDate = DateTime.UtcNow };
        _billingService.Setup(s => s.RecordPaymentAsync("inv-1", request))
            .ThrowsAsync(new InvalidOperationException("Cannot record payment on Voided invoice"));

        var result = await _controller.RecordPayment("inv-1", request);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region VoidInvoice

    [Fact]
    public async Task VoidInvoice_Success_Returns200()
    {
        var invoice = new PremiumInvoice { Id = "inv-1", Status = InvoiceStatus.Voided };
        _billingService.Setup(s => s.VoidInvoiceAsync("inv-1", "Duplicate"))
            .ReturnsAsync(invoice);

        var result = await _controller.VoidInvoice("inv-1", new VoidInvoiceRequest { Reason = "Duplicate" });

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<PremiumInvoice>().Subject;
        returned.Status.Should().Be(InvoiceStatus.Voided);
    }

    [Fact]
    public async Task VoidInvoice_PaidInvoice_Returns400()
    {
        _billingService.Setup(s => s.VoidInvoiceAsync("inv-1", "Mistake"))
            .ThrowsAsync(new InvalidOperationException("Cannot void a fully paid invoice"));

        var result = await _controller.VoidInvoice("inv-1", new VoidInvoiceRequest { Reason = "Mistake" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region MarkInvoiceSent

    [Fact]
    public async Task MarkInvoiceSent_Success_Returns200()
    {
        var invoice = new PremiumInvoice { Id = "inv-1", Status = InvoiceStatus.Sent };
        _billingService.Setup(s => s.MarkInvoiceSentAsync("inv-1"))
            .ReturnsAsync(invoice);

        var result = await _controller.MarkInvoiceSent("inv-1");

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<PremiumInvoice>().Subject;
        returned.Status.Should().Be(InvoiceStatus.Sent);
    }

    [Fact]
    public async Task MarkInvoiceSent_NotGenerated_Returns400()
    {
        _billingService.Setup(s => s.MarkInvoiceSentAsync("inv-1"))
            .ThrowsAsync(new InvalidOperationException("Can only mark Generated invoices as Sent"));

        var result = await _controller.MarkInvoiceSent("inv-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region GetOverdueInvoices

    [Fact]
    public async Task GetOverdueInvoices_Returns200()
    {
        var invoices = new List<PremiumInvoice>
        {
            new() { Id = "inv-1", Status = InvoiceStatus.Overdue }
        };
        _billingService.Setup(s => s.GetOverdueInvoicesAsync())
            .ReturnsAsync(invoices);

        var result = await _controller.GetOverdueInvoices();

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeAssignableTo<IEnumerable<PremiumInvoice>>().Subject;
        returned.Should().HaveCount(1);
    }

    #endregion

    #region GetAgingReport

    [Fact]
    public async Task GetAgingReport_Returns200()
    {
        var report = new AgingReport
        {
            CurrentAmount = 1000,
            CurrentCount = 2,
            TotalOutstanding = 1000,
            TotalCount = 2
        };
        _billingService.Setup(s => s.GetAgingReportAsync())
            .ReturnsAsync(report);

        var result = await _controller.GetAgingReport();

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<AgingReport>().Subject;
        returned.TotalOutstanding.Should().Be(1000);
    }

    #endregion

    #region ProcessDelinquencies

    [Fact]
    public async Task ProcessDelinquencies_Returns200WithCount()
    {
        _billingService.Setup(s => s.ProcessDelinquenciesAsync())
            .ReturnsAsync(3);

        var result = await _controller.ProcessDelinquencies();

        result.Should().BeOfType<OkObjectResult>();
    }

    #endregion
}
