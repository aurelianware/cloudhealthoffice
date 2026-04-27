using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSubstitute;
using PaymentService.Controllers;
using PaymentService.Models;
using PaymentService.Repositories;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

public class PaymentsControllerTests : IClassFixture<PaymentApiFactory>
{
    // Match the server's wire format (string enums via JsonStringEnumConverter
    // registered by AddCloudHealthOfficeJsonOptions).
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly PaymentApiFactory _factory;
    private readonly HttpClient _client;
    private readonly IPaymentRepository _repo;
    private readonly IEraGeneratorService _eraGenerator;

    public PaymentsControllerTests(PaymentApiFactory factory)
    {
        _factory = factory;
        _repo = factory.PaymentRepository;
        _eraGenerator = factory.EraGeneratorService;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static Payment CreateValidPayment(string? checkNumber = null)
    {
        return new Payment
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = "test-tenant",
            CheckNumber = checkNumber ?? "CHK-0001000001",
            PaymentMethod = "ACH",
            TotalPaymentAmount = 1250.00m,
            PaymentDate = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            PayerName = "Blue Cross Blue Shield",
            PayerId = "BCBS001",
            PayeeName = "Springfield Medical Group",
            PayeeNPI = "1234567890",
            Status = PaymentStatus.Received,
            ClaimPayments = new List<ClaimPayment>
            {
                new()
                {
                    ClaimId = "claim-001",
                    PatientControlNumber = "CLM-20260115-001",
                    ClaimStatusCode = "1",
                    ChargeAmount = 1500.00m,
                    PaymentAmount = 1250.00m,
                    PatientResponsibilityAmount = 250.00m,
                    PayerClaimControlNumber = "ICN-98765",
                    MemberId = "MEM-001",
                    RenderingProviderNPI = "9876543210",
                    ServiceLines = new List<ServiceLinePayment>
                    {
                        new()
                        {
                            LineNumber = 1,
                            ProcedureCode = "99213",
                            ChargeAmount = 150.00m,
                            PaymentAmount = 125.00m,
                            Units = 1,
                            ServiceDateFrom = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)
                        }
                    }
                }
            }
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 1: CREATE PAYMENT (PROCESS 835)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessPayment_ValidPayment_Returns201WithPaymentId()
    {
        var payment = CreateValidPayment("CHK-NEW-001");

        _repo.GetByCheckNumberAsync("CHK-NEW-001").Returns((Payment?)null);
        _repo.CreateAsync(Arg.Any<Payment>()).Returns(ci => ci.Arg<Payment>());

        var response = await _client.PostAsJsonAsync("/api/payments", payment, Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<Payment>(Json);
        Assert.NotNull(created);
        Assert.NotNull(created.Id);
        Assert.NotEmpty(created.Id);
        Assert.Equal("CHK-NEW-001", created.CheckNumber);
    }

    [Fact]
    public async Task ProcessPayment_MissingCheckNumber_Returns400()
    {
        var payment = CreateValidPayment();
        payment.CheckNumber = "";

        var response = await _client.PostAsJsonAsync("/api/payments", payment, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProcessPayment_DuplicateCheckNumber_Returns409Conflict()
    {
        var existing = CreateValidPayment("CHK-DUPE-001");

        _repo.GetByCheckNumberAsync("CHK-DUPE-001").Returns(existing);

        var payment = CreateValidPayment("CHK-DUPE-001");
        var response = await _client.PostAsJsonAsync("/api/payments", payment, Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 2: GENERATE 835 ERA
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetEra835_ExistingPayment_ReturnsValidX12Structure()
    {
        var payment = CreateValidPayment();
        payment.Id = "pay-835-test";

        // Build a realistic 835 EDI string with required segments
        var era835 =
            "ISA*00*          *00*          *ZZ*SENDER          *ZZ*RECEIVER        *260315*1200*^*00501*000000001*0*P*:~" +
            "GS*HP*SENDER*RECEIVER*20260315*1200*1*X*005010X221A1~" +
            "ST*835*0001*005010X221A1~" +
            "BPR*C*1250.00*C*ACH*CCP*01*021000021*DA*123456789*20260315*01*021000021*DA*987654321*20260315~" +
            "TRN*1*CHK-0001000001*BCBS001~" +
            "DTM*405*20260315~" +
            "N1*PR*Blue Cross Blue Shield*XV*BCBS001~" +
            "N1*PE*Springfield Medical Group*XX*1234567890~" +
            "CLP*CLM-20260115-001*1*1500.00*1250.00*250.00*HM*ICN-98765~" +
            "SVC*HC:99213*150.00*125.00**1~" +
            "SE*10*0001~" +
            "GE*1*1~" +
            "IEA*1*000000001~";

        _repo.GetByIdAsync("pay-835-test").Returns(payment);
        _eraGenerator.Generate835(Arg.Any<Payment>(), Arg.Any<TradingPartnerInfo>())
            .Returns(era835);

        var response = await _client.GetAsync("/api/payments/pay-835-test/835");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();

        // Verify X12 835 structure: required segments present
        Assert.Contains("ISA*", content);
        Assert.Contains("GS*HP*", content);
        Assert.Contains("ST*835*", content);
        Assert.Contains("BPR*", content);
        Assert.Contains("TRN*1*", content);
        Assert.Contains("SE*", content);
        Assert.Contains("GE*", content);
        Assert.Contains("IEA*", content);
        // Verify claim loop present
        Assert.Contains("CLP*", content);
        // Verify segment terminator
        Assert.Contains("~", content);
    }

    [Fact]
    public async Task GetEra835_ReturnsTextPlainContentType()
    {
        var payment = CreateValidPayment();
        payment.Id = "pay-ct-test";

        _repo.GetByIdAsync("pay-ct-test").Returns(payment);
        _eraGenerator.Generate835(Arg.Any<Payment>(), Arg.Any<TradingPartnerInfo>())
            .Returns("ST*835*0001~SE*2*0001~");

        var response = await _client.GetAsync("/api/payments/pay-ct-test/835");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetEra835_SetsContentDispositionWithCheckNumber()
    {
        var payment = CreateValidPayment("CHK-DISP-001");
        payment.Id = "pay-disp-test";

        _repo.GetByIdAsync("pay-disp-test").Returns(payment);
        _eraGenerator.Generate835(Arg.Any<Payment>(), Arg.Any<TradingPartnerInfo>())
            .Returns("ST*835*0001~SE*2*0001~");

        var response = await _client.GetAsync("/api/payments/pay-disp-test/835");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var contentDisposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(contentDisposition);
        var filename = contentDisposition.FileNameStar ?? contentDisposition.FileName?.Trim('"');
        Assert.NotNull(filename);
        Assert.Contains("835_CHK-DISP-001.edi", filename);
    }

    [Fact]
    public async Task GetEra835_NonexistentPayment_Returns404()
    {
        _repo.GetByIdAsync("nonexistent").Returns((Payment?)null);

        var response = await _client.GetAsync("/api/payments/nonexistent/835");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 3: PAYMENT STATUS TRACKING (LIFECYCLE)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetPaymentById_ReturnsReceivedStatus_WhenNewlyCreated()
    {
        var payment = CreateValidPayment();
        payment.Id = "pay-received";
        payment.Status = PaymentStatus.Received;

        _repo.GetByIdAsync("pay-received").Returns(payment);

        var response = await _client.GetAsync("/api/payments/pay-received");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returned = await response.Content.ReadFromJsonAsync<Payment>(Json);
        Assert.NotNull(returned);
        Assert.Equal(PaymentStatus.Received, returned.Status);
    }

    [Fact]
    public async Task PostPayment_TransitionsToPostedStatus()
    {
        var payment = CreateValidPayment();
        payment.Id = "pay-to-post";
        payment.Status = PaymentStatus.Received;

        _repo.GetByIdAsync("pay-to-post").Returns(payment);
        _repo.UpdateAsync(Arg.Any<Payment>()).Returns(ci => ci.Arg<Payment>());

        var request = new PostPaymentRequest
        {
            PostedBy = "admin@test.com",
            Notes = "Posting for reconciliation"
        };

        var response = await _client.PostAsJsonAsync("/api/payments/pay-to-post/post", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<Payment>(Json);
        Assert.NotNull(updated);
        Assert.Equal(PaymentStatus.Posted, updated.Status);
        Assert.NotNull(updated.PostedAt);
        Assert.Equal("admin@test.com", updated.PostedBy);
    }

    [Fact]
    public async Task ReconcilePayment_TransitionsToReconciledStatus()
    {
        var payment = CreateValidPayment();
        payment.Id = "pay-to-reconcile";
        payment.Status = PaymentStatus.Posted;
        payment.PostedAt = DateTime.UtcNow;

        _repo.GetByIdAsync("pay-to-reconcile").Returns(payment);
        _repo.UpdateAsync(Arg.Any<Payment>()).Returns(ci => ci.Arg<Payment>());

        var request = new ReconcilePaymentRequest
        {
            Notes = "Reconciled with bank deposit"
        };

        var response = await _client.PostAsJsonAsync("/api/payments/pay-to-reconcile/reconcile", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<Payment>(Json);
        Assert.NotNull(updated);
        Assert.Equal(PaymentStatus.Reconciled, updated.Status);
        Assert.NotNull(updated.ReconciledAt);
    }

    [Fact]
    public async Task PostPayment_NonexistentPayment_Returns404()
    {
        _repo.GetByIdAsync("nonexistent").Returns((Payment?)null);

        var request = new PostPaymentRequest { PostedBy = "admin@test.com" };
        var response = await _client.PostAsJsonAsync("/api/payments/nonexistent/post", request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReconcilePayment_NonexistentPayment_Returns404()
    {
        _repo.GetByIdAsync("nonexistent").Returns((Payment?)null);

        var request = new ReconcilePaymentRequest { Notes = "test" };
        var response = await _client.PostAsJsonAsync("/api/payments/nonexistent/reconcile", request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 4: DOWNLOAD 835
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Download835_ReturnsEdiContentWithCorrectContentType()
    {
        var payment = CreateValidPayment("CHK-DOWNLOAD-001");
        payment.Id = "pay-download";

        var ediContent =
            "ISA*00*          *00*          *ZZ*SENDER          *ZZ*RECEIVER        *260315*1200*^*00501*000000001*0*P*:~" +
            "GS*HP*SENDER*RECEIVER*20260315*1200*1*X*005010X221A1~" +
            "ST*835*0001*005010X221A1~" +
            "BPR*C*1250.00*C*ACH****20260315~" +
            "TRN*1*CHK-DOWNLOAD-001*BCBS001~" +
            "SE*5*0001~" +
            "GE*1*1~" +
            "IEA*1*000000001~";

        _repo.GetByIdAsync("pay-download").Returns(payment);
        _eraGenerator.Generate835(Arg.Any<Payment>(), Arg.Any<TradingPartnerInfo>())
            .Returns(ediContent);

        var response = await _client.GetAsync("/api/payments/pay-download/835");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(ediContent, body);

        // Verify attachment filename
        var contentDisposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(contentDisposition);
        var filename = contentDisposition.FileNameStar ?? contentDisposition.FileName?.Trim('"');
        Assert.NotNull(filename);
        Assert.Contains("835_CHK-DOWNLOAD-001.edi", filename);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 5: GET PAYMENT BY ID / CHECK NUMBER / CLAIM
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetPaymentById_ExistingPayment_Returns200()
    {
        var payment = CreateValidPayment();
        payment.Id = "pay-get-001";

        _repo.GetByIdAsync("pay-get-001").Returns(payment);

        var response = await _client.GetAsync("/api/payments/pay-get-001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returned = await response.Content.ReadFromJsonAsync<Payment>(Json);
        Assert.NotNull(returned);
        Assert.Equal("pay-get-001", returned.Id);
    }

    [Fact]
    public async Task GetPaymentById_NonexistentPayment_Returns404()
    {
        _repo.GetByIdAsync("nonexistent").Returns((Payment?)null);

        var response = await _client.GetAsync("/api/payments/nonexistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPaymentByCheckNumber_ExistingPayment_Returns200()
    {
        var payment = CreateValidPayment("CHK-LOOKUP-001");

        _repo.GetByCheckNumberAsync("CHK-LOOKUP-001").Returns(payment);

        var response = await _client.GetAsync("/api/payments/check/CHK-LOOKUP-001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returned = await response.Content.ReadFromJsonAsync<Payment>(Json);
        Assert.NotNull(returned);
        Assert.Equal("CHK-LOOKUP-001", returned.CheckNumber);
    }

    [Fact]
    public async Task GetPaymentsByClaimId_ReturnsMatchingPayments()
    {
        var p1 = CreateValidPayment("CHK-CLM-001");
        var p2 = CreateValidPayment("CHK-CLM-002");

        _repo.GetByClaimIdAsync("claim-001").Returns(new List<Payment> { p1, p2 });

        var response = await _client.GetAsync("/api/payments/claim/claim-001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<Payment>>(Json);
        Assert.NotNull(results);
        Assert.Equal(2, results.Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 6: SEARCH PAYMENTS
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SearchPayments_ByStatus_ReturnsFilteredResults()
    {
        var payment = CreateValidPayment();
        payment.Status = PaymentStatus.Posted;

        _repo.SearchAsync(null, null, null, PaymentStatus.Posted, 1, 50)
            .Returns(new List<Payment> { payment });

        var response = await _client.GetAsync("/api/payments?status=Posted");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<Payment>>(Json);
        Assert.NotNull(results);
        Assert.Single(results);
    }
}
