using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;

namespace PremiumBillingService.Tests.Services;

public class PremiumBillingServiceTests
{
    private readonly Mock<IBillingRunRepository> _billingRunRepo;
    private readonly Mock<IPremiumInvoiceRepository> _invoiceRepo;
    private readonly Mock<IHttpClientFactory> _httpClientFactory;
    private readonly PremiumBillingService.Services.PremiumBillingService _service;

    public PremiumBillingServiceTests()
    {
        _billingRunRepo = new Mock<IBillingRunRepository>();
        _invoiceRepo = new Mock<IPremiumInvoiceRepository>();
        _httpClientFactory = new Mock<IHttpClientFactory>();
        var logger = new Mock<ILogger<PremiumBillingService.Services.PremiumBillingService>>();

        _service = new PremiumBillingService.Services.PremiumBillingService(
            _billingRunRepo.Object,
            _invoiceRepo.Object,
            _httpClientFactory.Object,
            logger.Object);
    }

    private static HttpClient CreateMockHttpClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }

    #region CreateBillingRunAsync

    [Fact]
    public async Task CreateBillingRunAsync_NormalizesPeriodToFirstOfMonth()
    {
        var request = new CreateBillingRunRequest
        {
            BillingPeriod = new DateTime(2026, 3, 15),
            CreatedBy = "admin",
            Description = "Test run"
        };

        _billingRunRepo.Setup(r => r.CreateAsync(It.IsAny<BillingRun>()))
            .ReturnsAsync((BillingRun br) => br);

        var result = await _service.CreateBillingRunAsync(request, "admin");

        result.BillingPeriod.Day.Should().Be(1);
        result.BillingPeriod.Month.Should().Be(3);
        result.BillingPeriod.Year.Should().Be(2026);
        result.Status.Should().Be(BillingRunStatus.Pending);
        result.CreatedBy.Should().Be("admin");
        result.Description.Should().Be("Test run");
        result.BillingRunNumber.Should().StartWith("BR-2026-03-");
    }

    [Fact]
    public async Task CreateBillingRunAsync_SetsCriteriaFromRequest()
    {
        var criteria = new BillingRunCriteria
        {
            GroupNumbers = new List<string> { "GRP001", "GRP002" },
            LineOfBusiness = LineOfBusiness.Commercial
        };
        var request = new CreateBillingRunRequest
        {
            BillingPeriod = new DateTime(2026, 3, 1),
            Criteria = criteria
        };

        _billingRunRepo.Setup(r => r.CreateAsync(It.IsAny<BillingRun>()))
            .ReturnsAsync((BillingRun br) => br);

        var result = await _service.CreateBillingRunAsync(request, null);

        result.Criteria.GroupNumbers.Should().Contain("GRP001");
        result.Criteria.LineOfBusiness.Should().Be(LineOfBusiness.Commercial);
    }

    #endregion

    #region ExecuteBillingRunAsync

    [Fact]
    public async Task ExecuteBillingRunAsync_NotFound_ThrowsInvalidOperation()
    {
        _billingRunRepo.Setup(r => r.GetByIdAsync("missing"))
            .ReturnsAsync((BillingRun?)null);

        var act = () => _service.ExecuteBillingRunAsync("missing");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task ExecuteBillingRunAsync_NotPending_ThrowsInvalidOperation()
    {
        var billingRun = new BillingRun { Id = "br-1", Status = BillingRunStatus.Completed };
        _billingRunRepo.Setup(r => r.GetByIdAsync("br-1"))
            .ReturnsAsync(billingRun);

        var act = () => _service.ExecuteBillingRunAsync("br-1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expected Pending*");
    }

    [Fact]
    public async Task ExecuteBillingRunAsync_Success_GeneratesInvoicesAndCompletes()
    {
        var billingRun = new BillingRun
        {
            Id = "br-1",
            BillingRunNumber = "BR-2026-03-ABCD",
            Status = BillingRunStatus.Pending,
            BillingPeriod = new DateTime(2026, 3, 1),
            Criteria = new BillingRunCriteria()
        };

        _billingRunRepo.Setup(r => r.GetByIdAsync("br-1")).ReturnsAsync(billingRun);
        _billingRunRepo.Setup(r => r.UpdateAsync(It.IsAny<BillingRun>()))
            .ReturnsAsync((BillingRun br) => br);

        var sponsors = new List<SponsorDto>
        {
            new() { GroupNumber = "GRP001", EmployerName = "Acme Corp", BillingDay = 15, GracePeriodDays = 30 }
        };

        var coverages = new List<CoverageDto>
        {
            new()
            {
                CoverageId = "cov-1",
                MemberId = "mem-1",
                MemberName = "John Doe",
                GroupNumber = "GRP001",
                EffectiveDate = new DateTime(2025, 1, 1),
                MonthlyPremium = 500m,
                EmployerContribution = 300m
            }
        };

        var sponsorHandler = new BillingMockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(sponsors))
            });
        var coverageHandler = new BillingMockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(coverages))
            });

        _httpClientFactory.Setup(f => f.CreateClient("SponsorService"))
            .Returns(CreateMockHttpClient(sponsorHandler));
        _httpClientFactory.Setup(f => f.CreateClient("CoverageService"))
            .Returns(CreateMockHttpClient(coverageHandler));

        _invoiceRepo.Setup(r => r.CreateAsync(It.IsAny<PremiumInvoice>()))
            .ReturnsAsync((PremiumInvoice inv) =>
            {
                inv.RecalculateTotals();
                return inv;
            });

        var result = await _service.ExecuteBillingRunAsync("br-1");

        result.Status.Should().Be(BillingRunStatus.Completed);
        result.TotalInvoices.Should().Be(1);
        result.InvoiceIds.Should().HaveCount(1);
        result.TotalMembers.Should().BeGreaterThan(0);
        result.ExecutionStartedAt.Should().NotBeNull();
        result.ExecutionCompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteBillingRunAsync_SponsorFetchFails_MarksFailed()
    {
        var billingRun = new BillingRun
        {
            Id = "br-1",
            Status = BillingRunStatus.Pending,
            BillingPeriod = new DateTime(2026, 3, 1),
            Criteria = new BillingRunCriteria()
        };

        _billingRunRepo.Setup(r => r.GetByIdAsync("br-1")).ReturnsAsync(billingRun);
        _billingRunRepo.Setup(r => r.UpdateAsync(It.IsAny<BillingRun>()))
            .ReturnsAsync((BillingRun br) => br);

        var handler = new BillingMockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        _httpClientFactory.Setup(f => f.CreateClient("SponsorService"))
            .Returns(CreateMockHttpClient(handler));

        var result = await _service.ExecuteBillingRunAsync("br-1");

        result.Status.Should().Be(BillingRunStatus.Failed);
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteBillingRunAsync_EmptyCoverageForSponsor_StillGeneratesInvoiceAndContinues()
    {
        var billingRun = new BillingRun
        {
            Id = "br-1",
            Status = BillingRunStatus.Pending,
            BillingPeriod = new DateTime(2026, 3, 1),
            Criteria = new BillingRunCriteria()
        };

        _billingRunRepo.Setup(r => r.GetByIdAsync("br-1")).ReturnsAsync(billingRun);
        _billingRunRepo.Setup(r => r.UpdateAsync(It.IsAny<BillingRun>()))
            .ReturnsAsync((BillingRun br) => br);

        var sponsors = new List<SponsorDto>
        {
            new() { GroupNumber = "GRP001", EmployerName = "Acme Corp", BillingDay = 1 },
            new() { GroupNumber = "GRP002", EmployerName = "Beta Inc", BillingDay = 1 }
        };

        var sponsorHandler = new BillingMockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(sponsors))
            });

        // Coverage fetch fails for GRP001 but succeeds for GRP002
        var callCount = 0;
        var coverageHandler = new BillingMockHttpMessageHandler(request =>
        {
            callCount++;
            if (request.RequestUri!.ToString().Contains("GRP001"))
            {
                // Returns empty list (FetchCoveragesByGroupAsync catches exceptions and returns empty)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new List<CoverageDto>
                {
                    new()
                    {
                        CoverageId = "cov-2",
                        MemberId = "mem-2",
                        MemberName = "Jane Doe",
                        GroupNumber = "GRP002",
                        EffectiveDate = new DateTime(2025, 1, 1),
                        MonthlyPremium = 400m,
                        EmployerContribution = 200m
                    }
                }))
            };
        });

        _httpClientFactory.Setup(f => f.CreateClient("SponsorService"))
            .Returns(CreateMockHttpClient(sponsorHandler));
        _httpClientFactory.Setup(f => f.CreateClient("CoverageService"))
            .Returns(CreateMockHttpClient(coverageHandler));

        _invoiceRepo.Setup(r => r.CreateAsync(It.IsAny<PremiumInvoice>()))
            .ReturnsAsync((PremiumInvoice inv) =>
            {
                inv.RecalculateTotals();
                return inv;
            });

        var result = await _service.ExecuteBillingRunAsync("br-1");

        callCount.Should().Be(2);
        result.Status.Should().Be(BillingRunStatus.Completed);
        result.TotalInvoices.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteBillingRunAsync_WithGroupNumberCriteria_FiltersSponsors()
    {
        var billingRun = new BillingRun
        {
            Id = "br-1",
            Status = BillingRunStatus.Pending,
            BillingPeriod = new DateTime(2026, 3, 1),
            Criteria = new BillingRunCriteria { GroupNumbers = new List<string> { "GRP001" } }
        };

        _billingRunRepo.Setup(r => r.GetByIdAsync("br-1")).ReturnsAsync(billingRun);
        _billingRunRepo.Setup(r => r.UpdateAsync(It.IsAny<BillingRun>()))
            .ReturnsAsync((BillingRun br) => br);

        var allSponsors = new List<SponsorDto>
        {
            new() { GroupNumber = "GRP001", EmployerName = "Acme Corp", BillingDay = 1 },
            new() { GroupNumber = "GRP002", EmployerName = "Beta Inc", BillingDay = 1 }
        };

        var sponsorHandler = new BillingMockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(allSponsors))
            });

        var coverageHandler = new BillingMockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new List<CoverageDto>
                {
                    new()
                    {
                        CoverageId = "cov-1", MemberId = "mem-1", GroupNumber = "GRP001",
                        EffectiveDate = new DateTime(2025, 1, 1), MonthlyPremium = 500m
                    }
                }))
            });

        _httpClientFactory.Setup(f => f.CreateClient("SponsorService"))
            .Returns(CreateMockHttpClient(sponsorHandler));
        _httpClientFactory.Setup(f => f.CreateClient("CoverageService"))
            .Returns(CreateMockHttpClient(coverageHandler));

        _invoiceRepo.Setup(r => r.CreateAsync(It.IsAny<PremiumInvoice>()))
            .ReturnsAsync((PremiumInvoice inv) => { inv.RecalculateTotals(); return inv; });

        var result = await _service.ExecuteBillingRunAsync("br-1");

        // Only GRP001 should be billed (GRP002 filtered out by criteria)
        result.TotalInvoices.Should().Be(1);
    }

    #endregion

    #region GetBillingRunAsync

    [Fact]
    public async Task GetBillingRunAsync_Found_ReturnsBillingRun()
    {
        var billingRun = new BillingRun { Id = "br-1" };
        _billingRunRepo.Setup(r => r.GetByIdAsync("br-1")).ReturnsAsync(billingRun);

        var result = await _service.GetBillingRunAsync("br-1");

        result.Id.Should().Be("br-1");
    }

    [Fact]
    public async Task GetBillingRunAsync_NotFound_ThrowsInvalidOperation()
    {
        _billingRunRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((BillingRun?)null);

        var act = () => _service.GetBillingRunAsync("missing");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region GetBillingRunsAsync

    [Fact]
    public async Task GetBillingRunsAsync_DelegatesToRepository()
    {
        var from = new DateTime(2026, 1, 1);
        var to = new DateTime(2026, 3, 31);
        var runs = new List<BillingRun> { new() { Id = "br-1" } };
        _billingRunRepo.Setup(r => r.SearchAsync(from, to, null)).ReturnsAsync(runs);

        var result = await _service.GetBillingRunsAsync(from, to);

        result.Should().HaveCount(1);
    }

    #endregion

    #region CancelBillingRunAsync

    [Fact]
    public async Task CancelBillingRunAsync_PendingRun_CancelsSuccessfully()
    {
        var billingRun = new BillingRun { Id = "br-1", Status = BillingRunStatus.Pending };
        _billingRunRepo.Setup(r => r.GetByIdAsync("br-1")).ReturnsAsync(billingRun);
        _billingRunRepo.Setup(r => r.UpdateAsync(It.IsAny<BillingRun>()))
            .ReturnsAsync((BillingRun br) => br);

        await _service.CancelBillingRunAsync("br-1");

        _billingRunRepo.Verify(r => r.UpdateAsync(It.Is<BillingRun>(br =>
            br.Status == BillingRunStatus.Cancelled)), Times.Once);
    }

    [Fact]
    public async Task CancelBillingRunAsync_NotPending_ThrowsInvalidOperation()
    {
        var billingRun = new BillingRun { Id = "br-1", Status = BillingRunStatus.Running };
        _billingRunRepo.Setup(r => r.GetByIdAsync("br-1")).ReturnsAsync(billingRun);

        var act = () => _service.CancelBillingRunAsync("br-1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Pending*");
    }

    [Fact]
    public async Task CancelBillingRunAsync_NotFound_ThrowsInvalidOperation()
    {
        _billingRunRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((BillingRun?)null);

        var act = () => _service.CancelBillingRunAsync("missing");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region RecordPaymentAsync

    [Fact]
    public async Task RecordPaymentAsync_FullPayment_StatusSetToPaid()
    {
        var invoice = new PremiumInvoice
        {
            Id = "inv-1",
            InvoiceNumber = "INV-001",
            Status = InvoiceStatus.Sent,
            LineItems = new List<InvoiceLineItem>
            {
                new() { MemberId = "m1", TotalPremium = 1000 }
            }
        };
        invoice.RecalculateTotals();

        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<PremiumInvoice>()))
            .ReturnsAsync((PremiumInvoice inv) => inv);

        var request = new RecordPaymentRequest
        {
            Amount = 1000,
            PaymentDate = DateTime.UtcNow,
            PaymentMethod = "ACH",
            ReferenceNumber = "REF-001"
        };

        var result = await _service.RecordPaymentAsync("inv-1", request);

        result.Status.Should().Be(InvoiceStatus.Paid);
        result.Payments.Should().HaveCount(1);
        result.BalanceDue.Should().BeLessThanOrEqualTo(0);
    }

    [Fact]
    public async Task RecordPaymentAsync_PartialPayment_StatusSetToPartiallyPaid()
    {
        var invoice = new PremiumInvoice
        {
            Id = "inv-1",
            InvoiceNumber = "INV-001",
            Status = InvoiceStatus.Sent,
            LineItems = new List<InvoiceLineItem>
            {
                new() { MemberId = "m1", TotalPremium = 1000 }
            }
        };
        invoice.RecalculateTotals();

        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<PremiumInvoice>()))
            .ReturnsAsync((PremiumInvoice inv) => inv);

        var request = new RecordPaymentRequest
        {
            Amount = 500,
            PaymentDate = DateTime.UtcNow
        };

        var result = await _service.RecordPaymentAsync("inv-1", request);

        result.Status.Should().Be(InvoiceStatus.PartiallyPaid);
        result.BalanceDue.Should().Be(500);
    }

    [Fact]
    public async Task RecordPaymentAsync_VoidedInvoice_ThrowsInvalidOperation()
    {
        var invoice = new PremiumInvoice { Id = "inv-1", Status = InvoiceStatus.Voided };
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);

        var request = new RecordPaymentRequest { Amount = 100, PaymentDate = DateTime.UtcNow };

        var act = () => _service.RecordPaymentAsync("inv-1", request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot record payment*");
    }

    [Fact]
    public async Task RecordPaymentAsync_WriteOffInvoice_ThrowsInvalidOperation()
    {
        var invoice = new PremiumInvoice { Id = "inv-1", Status = InvoiceStatus.WriteOff };
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);

        var request = new RecordPaymentRequest { Amount = 100, PaymentDate = DateTime.UtcNow };

        var act = () => _service.RecordPaymentAsync("inv-1", request);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RecordPaymentAsync_NotFound_ThrowsInvalidOperation()
    {
        _invoiceRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((PremiumInvoice?)null);

        var request = new RecordPaymentRequest { Amount = 100, PaymentDate = DateTime.UtcNow };

        var act = () => _service.RecordPaymentAsync("missing", request);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region VoidInvoiceAsync

    [Fact]
    public async Task VoidInvoiceAsync_GeneratedInvoice_VoidsSuccessfully()
    {
        var invoice = new PremiumInvoice
        {
            Id = "inv-1",
            InvoiceNumber = "INV-001",
            Status = InvoiceStatus.Generated
        };
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<PremiumInvoice>()))
            .ReturnsAsync((PremiumInvoice inv) => inv);

        var result = await _service.VoidInvoiceAsync("inv-1", "Duplicate invoice");

        result.Status.Should().Be(InvoiceStatus.Voided);
        result.Adjustments.Should().HaveCount(1);
        result.Adjustments[0].Description.Should().Contain("Duplicate invoice");
    }

    [Fact]
    public async Task VoidInvoiceAsync_PaidInvoice_ThrowsInvalidOperation()
    {
        var invoice = new PremiumInvoice { Id = "inv-1", Status = InvoiceStatus.Paid };
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);

        var act = () => _service.VoidInvoiceAsync("inv-1", "test");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot void a fully paid invoice*");
    }

    [Fact]
    public async Task VoidInvoiceAsync_NotFound_ThrowsInvalidOperation()
    {
        _invoiceRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((PremiumInvoice?)null);

        var act = () => _service.VoidInvoiceAsync("missing", "test");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region MarkInvoiceSentAsync

    [Fact]
    public async Task MarkInvoiceSentAsync_GeneratedInvoice_MarksAsSent()
    {
        var invoice = new PremiumInvoice { Id = "inv-1", Status = InvoiceStatus.Generated };
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<PremiumInvoice>()))
            .ReturnsAsync((PremiumInvoice inv) => inv);

        var result = await _service.MarkInvoiceSentAsync("inv-1");

        result.Status.Should().Be(InvoiceStatus.Sent);
    }

    [Fact]
    public async Task MarkInvoiceSentAsync_NotGenerated_ThrowsInvalidOperation()
    {
        var invoice = new PremiumInvoice { Id = "inv-1", Status = InvoiceStatus.Paid };
        _invoiceRepo.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);

        var act = () => _service.MarkInvoiceSentAsync("inv-1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Generated*");
    }

    [Fact]
    public async Task MarkInvoiceSentAsync_NotFound_ThrowsInvalidOperation()
    {
        _invoiceRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((PremiumInvoice?)null);

        var act = () => _service.MarkInvoiceSentAsync("missing");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region GetOverdueInvoicesAsync

    [Fact]
    public async Task GetOverdueInvoicesAsync_DelegatesToRepository()
    {
        var invoices = new List<PremiumInvoice> { new() { Id = "inv-1" } };
        _invoiceRepo.Setup(r => r.GetOverdueAsync()).ReturnsAsync(invoices);

        var result = await _service.GetOverdueInvoicesAsync();

        result.Should().HaveCount(1);
    }

    #endregion

    #region GetAgingReportAsync

    [Fact]
    public async Task GetAgingReportAsync_CategorizesByAgeBuckets()
    {
        var now = DateTime.UtcNow;
        var invoices = new List<PremiumInvoice>
        {
            new() { DueDate = now.AddDays(-10), BalanceDue = 100 },   // Current (<=30)
            new() { DueDate = now.AddDays(-40), BalanceDue = 200 },   // 30-day
            new() { DueDate = now.AddDays(-70), BalanceDue = 300 },   // 60-day
            new() { DueDate = now.AddDays(-100), BalanceDue = 400 }   // 90+ day
        };
        _invoiceRepo.Setup(r => r.GetOverdueAsync()).ReturnsAsync(invoices);

        var result = await _service.GetAgingReportAsync();

        result.CurrentCount.Should().Be(1);
        result.CurrentAmount.Should().Be(100);
        result.ThirtyDayCount.Should().Be(1);
        result.ThirtyDayAmount.Should().Be(200);
        result.SixtyDayCount.Should().Be(1);
        result.SixtyDayAmount.Should().Be(300);
        result.NinetyPlusDayCount.Should().Be(1);
        result.NinetyPlusDayAmount.Should().Be(400);
        result.TotalOutstanding.Should().Be(1000);
        result.TotalCount.Should().Be(4);
    }

    [Fact]
    public async Task GetAgingReportAsync_NoOverdueInvoices_ReturnsEmptyReport()
    {
        _invoiceRepo.Setup(r => r.GetOverdueAsync()).ReturnsAsync(new List<PremiumInvoice>());

        var result = await _service.GetAgingReportAsync();

        result.TotalCount.Should().Be(0);
        result.TotalOutstanding.Should().Be(0);
    }

    #endregion

    #region ProcessDelinquenciesAsync

    [Fact]
    public async Task ProcessDelinquenciesAsync_GracePeriodExpired_MarksDelinquent()
    {
        var now = DateTime.UtcNow;
        var invoice = new PremiumInvoice
        {
            Id = "inv-1",
            InvoiceNumber = "INV-001",
            GroupNumber = "GRP001",
            Status = InvoiceStatus.Sent,
            DueDate = now.AddDays(-60),
            BalanceDue = 500,
            GracePeriodExpires = now.AddDays(-10) // Grace period already expired
        };

        _invoiceRepo.Setup(r => r.GetOverdueAsync()).ReturnsAsync(new List<PremiumInvoice> { invoice });
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<PremiumInvoice>()))
            .ReturnsAsync((PremiumInvoice inv) => inv);

        // Mock sponsor service for suspension call
        var sponsorHandler = new BillingMockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK));
        _httpClientFactory.Setup(f => f.CreateClient("SponsorService"))
            .Returns(CreateMockHttpClient(sponsorHandler));

        var result = await _service.ProcessDelinquenciesAsync();

        result.Should().Be(1);
        _invoiceRepo.Verify(r => r.UpdateAsync(It.Is<PremiumInvoice>(inv =>
            inv.Status == InvoiceStatus.Delinquent)), Times.Once);
    }

    [Fact]
    public async Task ProcessDelinquenciesAsync_WithinGracePeriod_MarksOverdue()
    {
        var now = DateTime.UtcNow;
        var invoice = new PremiumInvoice
        {
            Id = "inv-1",
            InvoiceNumber = "INV-001",
            GroupNumber = "GRP001",
            Status = InvoiceStatus.Sent,
            DueDate = now.AddDays(-10),
            BalanceDue = 500,
            GracePeriodExpires = now.AddDays(20) // Grace period still active
        };

        _invoiceRepo.Setup(r => r.GetOverdueAsync()).ReturnsAsync(new List<PremiumInvoice> { invoice });
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<PremiumInvoice>()))
            .ReturnsAsync((PremiumInvoice inv) => inv);

        var result = await _service.ProcessDelinquenciesAsync();

        result.Should().Be(0);
        _invoiceRepo.Verify(r => r.UpdateAsync(It.Is<PremiumInvoice>(inv =>
            inv.Status == InvoiceStatus.Overdue)), Times.Once);
    }

    [Fact]
    public async Task ProcessDelinquenciesAsync_AlreadyDelinquent_Skipped()
    {
        var now = DateTime.UtcNow;
        var invoice = new PremiumInvoice
        {
            Id = "inv-1",
            Status = InvoiceStatus.Delinquent,
            DueDate = now.AddDays(-100),
            BalanceDue = 500,
            GracePeriodExpires = now.AddDays(-70)
        };

        _invoiceRepo.Setup(r => r.GetOverdueAsync()).ReturnsAsync(new List<PremiumInvoice> { invoice });

        var result = await _service.ProcessDelinquenciesAsync();

        result.Should().Be(0);
        _invoiceRepo.Verify(r => r.UpdateAsync(It.IsAny<PremiumInvoice>()), Times.Never);
    }

    [Fact]
    public async Task ProcessDelinquenciesAsync_NoOverdue_ReturnsZero()
    {
        _invoiceRepo.Setup(r => r.GetOverdueAsync()).ReturnsAsync(new List<PremiumInvoice>());

        var result = await _service.ProcessDelinquenciesAsync();

        result.Should().Be(0);
    }

    [Fact]
    public async Task ProcessDelinquenciesAsync_SponsorSuspensionFails_StillCountsDelinquent()
    {
        var now = DateTime.UtcNow;
        var invoice = new PremiumInvoice
        {
            Id = "inv-1",
            InvoiceNumber = "INV-001",
            GroupNumber = "GRP001",
            Status = InvoiceStatus.Sent,
            DueDate = now.AddDays(-60),
            BalanceDue = 500,
            GracePeriodExpires = now.AddDays(-10)
        };

        _invoiceRepo.Setup(r => r.GetOverdueAsync()).ReturnsAsync(new List<PremiumInvoice> { invoice });
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<PremiumInvoice>()))
            .ReturnsAsync((PremiumInvoice inv) => inv);

        // Sponsor service call fails
        var handler = new BillingMockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        _httpClientFactory.Setup(f => f.CreateClient("SponsorService"))
            .Returns(CreateMockHttpClient(handler));

        var result = await _service.ProcessDelinquenciesAsync();

        result.Should().Be(1); // Still counted even though suspension failed
    }

    #endregion
}

/// <summary>
/// Simple mock HTTP message handler for testing HttpClient calls in PremiumBillingService tests
/// </summary>
internal class BillingMockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public BillingMockHttpMessageHandler(HttpResponseMessage response)
    {
        _handler = _ => response;
    }

    public BillingMockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = _handler(request);
        response.Content ??= new StringContent(string.Empty);
        if (response.Content.Headers.ContentType == null)
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return Task.FromResult(response);
    }
}
