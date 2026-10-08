using CloudHealthOffice.PaymentService.Tests.Security;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PaymentService.Models;
using PaymentService.Repositories;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// Unit tests for the 5.10 batched flow inside <see cref="PaymentRunService"/>.
/// Exercises the orchestration: claims-service fetch + filter, trading partner
/// resolution, batch envelope generation, envelope persistence, and finalize calls.
/// </summary>
public class PaymentRunServiceBatchedTests
{
    private readonly IPaymentRepository _paymentRepo = Substitute.For<IPaymentRepository>();
    private readonly IPaymentRunRepository _runRepo = Substitute.For<IPaymentRunRepository>();
    private readonly IBatchEraGeneratorService _batchGen = Substitute.For<IBatchEraGeneratorService>();
    private readonly ICarcRarcMappingService _mapper = Substitute.For<ICarcRarcMappingService>();
    private readonly IEraEnvelopeRepository _envelopeRepo = Substitute.For<IEraEnvelopeRepository>();
    private readonly ITradingPartnersClient _tpClient = Substitute.For<ITradingPartnersClient>();
    private readonly StubHttpHandler _claimsHandler = new();
    private readonly IHttpClientFactory _httpFactory = Substitute.For<IHttpClientFactory>();
    private readonly IConfiguration _configuration;
    private readonly TestActor _actor = TestActor.Approver();
    private readonly InMemoryClaimReservationRepository _reservations = new();

    public PaymentRunServiceBatchedTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Era:InterchangeSenderId"] = "SENDER",
                ["Era:InterchangeReceiverId"] = "RECEIVER",
                ["Payer:Name"] = "Cloud Health Office",
                ["Payer:Id"] = "CHO",
                ["Era:OriginatingCompanyId"] = "1123456789",
                ["TradingPartners:Environment"] = "Production",
                ["Payment:StartingCheckNumber"] = "1000000"
            })
            .Build();

        var http = new HttpClient(_claimsHandler) { BaseAddress = new Uri("http://claims-service") };
        _httpFactory.CreateClient("ClaimsService").Returns(http);

        _runRepo.TryStartAsync(default!, default!, default).ReturnsForAnyArgs(true);
        _mapper.MapClaimAdjustments(Arg.Any<ClaimAdjudicationSnapshot>())
            .Returns(Array.Empty<ClaimAdjustment>());
        _mapper.MapLineAdjustments(Arg.Any<ClaimAdjudicationSnapshot>())
            .Returns(new Dictionary<int, IReadOnlyList<ServiceLineAdjustment>>());
    }

    private PaymentRunService CreateService() => new(
        _paymentRepo,
        _runRepo,
        _batchGen,
        _mapper,
        _envelopeRepo,
        _tpClient,
        _httpFactory,
        NullLogger<PaymentRunService>.Instance,
        _configuration,
        _actor,
        _actor.SeparationOfDuties(),
        _reservations);

    private static PaymentRun PendingRun() => new()
    {
        Id = "run-1",
        TenantId = "test-tenant",
        PaymentRunNumber = "PR-20260501-A1B2",
        Status = PaymentRunStatus.Pending,
        Criteria = new PaymentRunCriteria { GroupByProvider = true },
        NextCheckNumber = 1000000,
        PaymentDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
        PaymentMethod = "ACH"
    };

    private void SetupClaimsResponse(IEnumerable<ClaimDto> claims)
    {
        _claimsHandler.NextResponse = req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.StartsWith("/api/claims/search"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(claims.ToList())
                };
            }
            // Default success for finalize POST
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_NoClaims_CompletesWithWarning()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        SetupClaimsResponse(Array.Empty<ClaimDto>());

        var result = await CreateService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(PaymentRunStatus.Completed, result.Status);
        Assert.Contains(result.Warnings, w => w.Contains("No approved claims"));
        await _envelopeRepo.DidNotReceive().CreateAsync(Arg.Any<EraEnvelopeRecord>());
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_ResolvesTradingPartnersPerNpi()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());

        var claims = new[]
        {
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A", TotalChargeAmount = 100m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 80m }, MemberId = "m1", Status = ClaimStatus.Approved },
            new ClaimDto { Id = "c2", ClaimNumber = "CLM-2", BillingProviderNPI = "NPI-B", TotalChargeAmount = 200m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 160m }, MemberId = "m2", Status = ClaimStatus.Approved }
        };
        SetupClaimsResponse(claims);

        _tpClient.GetByBillingProviderNpiAsync("test-tenant", "NPI-A", "Production")
            .Returns(new TradingPartnerSummary { TradingPartnerId = "TP-A", X12Config = new X12ConfigDto { SenderId = "SENDA", ReceiverId = "RECVA" } });
        _tpClient.GetByBillingProviderNpiAsync("test-tenant", "NPI-B", "Production")
            .Returns(new TradingPartnerSummary { TradingPartnerId = "TP-B", X12Config = new X12ConfigDto { SenderId = "SENDB", ReceiverId = "RECVB" } });

        _paymentRepo.CreateAsync(Arg.Any<Payment>()).Returns(call => call.Arg<Payment>());
        _envelopeRepo.CreateAsync(Arg.Any<EraEnvelopeRecord>()).Returns(call => { var rec = call.Arg<EraEnvelopeRecord>(); rec.Id = "env-" + rec.TradingPartnerId; return rec; });

        _batchGen.GenerateBatch(Arg.Any<IEnumerable<EraPaymentInput>>(), Arg.Any<IReadOnlyDictionary<string, TradingPartnerInfo>>())
            .Returns(call =>
            {
                var inputs = call.Arg<IEnumerable<EraPaymentInput>>().ToList();
                return inputs
                    .GroupBy(i => i.TradingPartnerId)
                    .Select(g => new EraEnvelope(
                        TradingPartnerId: g.Key,
                        EdiContent: $"ISA*{g.Key}~",
                        ClaimCount: g.Sum(p => p.Payment.ClaimPayments.Count),
                        TotalPaymentAmount: g.Sum(p => p.Payment.TotalPaymentAmount),
                        ControlNumber: "000000001",
                        ClaimIds: g.SelectMany(p => p.Payment.ClaimPayments.Select(cp => cp.ClaimId)).ToList(),
                        IsReversal: false))
                    .ToList();
            });

        var result = await CreateService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(PaymentRunStatus.Completed, result.Status);
        Assert.Equal(2, result.EraEnvelopeIds.Count);
        Assert.Equal(2, result.TotalClaims);
        await _tpClient.Received(1).GetByBillingProviderNpiAsync("test-tenant", "NPI-A", "Production");
        await _tpClient.Received(1).GetByBillingProviderNpiAsync("test-tenant", "NPI-B", "Production");
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_FinalizesEachClaimViaRemittanceEndpoint()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());

        var claims = new[]
        {
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A", TotalChargeAmount = 100m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 80m }, MemberId = "m1", Status = ClaimStatus.Approved }
        };
        SetupClaimsResponse(claims);

        _tpClient.GetByBillingProviderNpiAsync("test-tenant", "NPI-A", "Production")
            .Returns(new TradingPartnerSummary { TradingPartnerId = "TP-A", X12Config = new X12ConfigDto() });

        _paymentRepo.CreateAsync(Arg.Any<Payment>()).Returns(call => call.Arg<Payment>());
        _envelopeRepo.CreateAsync(Arg.Any<EraEnvelopeRecord>()).Returns(call => { var rec = call.Arg<EraEnvelopeRecord>(); rec.Id = "env-1"; return rec; });

        _batchGen.GenerateBatch(Arg.Any<IEnumerable<EraPaymentInput>>(), Arg.Any<IReadOnlyDictionary<string, TradingPartnerInfo>>())
            .Returns(new List<EraEnvelope>
            {
                new("TP-A", "ISA~", 1, 80m, "000000001", new[] { "c1" }, false)
            });

        await CreateService().ExecutePaymentRunAsync(run.Id);

        var finalizeCalls = _claimsHandler.RecordedRequests
            .Where(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.Contains("/remittance"))
            .ToList();
        Assert.Single(finalizeCalls);
        Assert.Contains("/api/claims/c1/remittance", finalizeCalls[0].Uri.AbsolutePath);
        Assert.Contains("\"checkNumber\"", finalizeCalls[0].Body);
        Assert.Contains("\"paymentRunId\"", finalizeCalls[0].Body);
        // EraEnvelopeId audit-trail crumb populated from the persisted record.
        Assert.Contains("\"eraEnvelopeId\":\"env-1\"", finalizeCalls[0].Body);
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_UnresolvedTradingPartner_ClaimNotPaid_ListedForLater()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());

        var claims = new[]
        {
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-MISSING", TotalChargeAmount = 100m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 80m }, MemberId = "m1", Status = ClaimStatus.Approved }
        };
        SetupClaimsResponse(claims);

        _tpClient.GetByBillingProviderNpiAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns((TradingPartnerSummary?)null);

        _paymentRepo.CreateAsync(Arg.Any<Payment>()).Returns(call => call.Arg<Payment>());
        _batchGen.GenerateBatch(Arg.Any<IEnumerable<EraPaymentInput>>(), Arg.Any<IReadOnlyDictionary<string, TradingPartnerInfo>>())
            .Returns(Array.Empty<EraEnvelope>());

        var result = await CreateService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(PaymentRunStatus.Completed, result.Status);
        Assert.Contains(result.Warnings, w => w.Contains("NPI-MISSING"));

        // No trading partner, no 835: the claim is not paid at all, and is
        // listed so a run picks it up once a partner exists.
        Assert.Equal(new[] { "c1" }, result.NeedsTradingPartnerClaimIds);
        Assert.Empty(result.PaymentIds);
        await _paymentRepo.DidNotReceiveWithAnyArgs().CreateAsync(default!);
        Assert.Empty(_reservations.All);
        var finalizeCalls = _claimsHandler.RecordedRequests
            .Where(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.Contains("/remittance"))
            .ToList();
        Assert.Empty(finalizeCalls);
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_MultipleProvidersSameTradingPartner_ShareSingleCheckNumber()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());

        var claims = new[]
        {
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A1", TotalChargeAmount = 100m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 80m }, MemberId = "m1", Status = ClaimStatus.Approved },
            new ClaimDto { Id = "c2", ClaimNumber = "CLM-2", BillingProviderNPI = "NPI-A2", TotalChargeAmount = 100m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 80m }, MemberId = "m2", Status = ClaimStatus.Approved }
        };
        SetupClaimsResponse(claims);

        // Both NPIs route to the same trading partner — should share one check number.
        var tp = new TradingPartnerSummary { TradingPartnerId = "TP-A", X12Config = new X12ConfigDto() };
        _tpClient.GetByBillingProviderNpiAsync("test-tenant", "NPI-A1", "Production").Returns(tp);
        _tpClient.GetByBillingProviderNpiAsync("test-tenant", "NPI-A2", "Production").Returns(tp);

        var capturedPayments = new List<Payment>();
        _paymentRepo.CreateAsync(Arg.Any<Payment>())
            .Returns(call =>
            {
                var p = call.Arg<Payment>();
                capturedPayments.Add(p);
                return p;
            });
        _batchGen.GenerateBatch(Arg.Any<IEnumerable<EraPaymentInput>>(), Arg.Any<IReadOnlyDictionary<string, TradingPartnerInfo>>())
            .Returns(Array.Empty<EraEnvelope>());

        await CreateService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(2, capturedPayments.Count);
        Assert.Equal(capturedPayments[0].CheckNumber, capturedPayments[1].CheckNumber);
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_NonPending_Throws()
    {
        var run = PendingRun();
        run.Status = PaymentRunStatus.Completed;
        _runRepo.GetByIdAsync(run.Id).Returns(run);

        // RunConflictException (an InvalidOperationException) -> 409.
        var ex = await Assert.ThrowsAsync<RunConflictException>(() =>
            CreateService().ExecutePaymentRunAsync(run.Id));
        Assert.Contains("not in Pending status", ex.Message);
    }

    private void SetupSinglePartnerPassThrough()
    {
        _tpClient.GetByBillingProviderNpiAsync("test-tenant", "NPI-A", "Production")
            .Returns(new TradingPartnerSummary { TradingPartnerId = "TP-A", X12Config = new X12ConfigDto() });
        _paymentRepo.CreateAsync(Arg.Any<Payment>()).Returns(call => call.Arg<Payment>());
        _envelopeRepo.CreateAsync(Arg.Any<EraEnvelopeRecord>()).Returns(call => { var rec = call.Arg<EraEnvelopeRecord>(); rec.Id = "env-1"; return rec; });
        _batchGen.GenerateBatch(Arg.Any<IEnumerable<EraPaymentInput>>(), Arg.Any<IReadOnlyDictionary<string, TradingPartnerInfo>>())
            .Returns(call =>
            {
                var inputs = call.Arg<IEnumerable<EraPaymentInput>>().ToList();
                return inputs
                    .GroupBy(i => i.TradingPartnerId)
                    .Select(g => new EraEnvelope(
                        g.Key, "ISA~",
                        g.Sum(p => p.Payment.ClaimPayments.Count),
                        g.Sum(p => p.Payment.TotalPaymentAmount),
                        "000000001",
                        g.SelectMany(p => p.Payment.ClaimPayments.Select(cp => cp.ClaimId)).ToList(),
                        false))
                    .ToList();
            });
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_ClaimWithoutApprovedAmount_NotPaidAtBilledCharges_Reported()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());

        var claims = new[]
        {
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A", TotalChargeAmount = 100m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 80m }, MemberId = "m1", Status = ClaimStatus.Approved },
            new ClaimDto { Id = "c-null", ClaimNumber = "CLM-2", BillingProviderNPI = "NPI-A", TotalChargeAmount = 5000m, AdjudicationResult = null, MemberId = "m2", Status = ClaimStatus.Approved },
            new ClaimDto { Id = "c-zero", ClaimNumber = "CLM-3", BillingProviderNPI = "NPI-A", TotalChargeAmount = 300m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 0m }, MemberId = "m3", Status = ClaimStatus.Approved }
        };
        SetupClaimsResponse(claims);
        SetupSinglePartnerPassThrough();

        var captured = new List<Payment>();
        _paymentRepo.CreateAsync(Arg.Do<Payment>(captured.Add)).Returns(call => call.Arg<Payment>());

        var result = await CreateService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(PaymentRunStatus.Completed, result.Status);

        // The claim without an approved amount is reported, not paid, not reserved.
        Assert.Equal(new[] { "c-null" }, result.MissingPlanPaidAmountClaimIds);
        Assert.Contains(result.Warnings, w => w.Contains("c-null") && w.Contains("no plan-paid amount"));
        Assert.DoesNotContain("c-null", result.ClaimIds);
        Assert.DoesNotContain(_reservations.All, r => r.ClaimId == "c-null");
        Assert.DoesNotContain(captured.SelectMany(p => p.ClaimPayments), cp => cp.ClaimId == "c-null");
        Assert.DoesNotContain(_claimsHandler.RecordedRequests,
            r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.Contains("/c-null/"));

        // The other claims are paid their approved amounts (a zero approval is paid zero).
        Assert.Equal(2, result.TotalClaims);
        Assert.Equal(new[] { "c1", "c-zero" }, result.ClaimIds);
        var payment = Assert.Single(captured);
        Assert.Equal(80m, payment.TotalPaymentAmount);
        Assert.Equal(80m, payment.ClaimPayments.Single(cp => cp.ClaimId == "c1").PaymentAmount);
        Assert.Equal(0m, payment.ClaimPayments.Single(cp => cp.ClaimId == "c-zero").PaymentAmount);
        Assert.Equal(80m, result.TotalPaymentAmount);
        Assert.Equal(new[] { "c-zero", "c1" }, _reservations.All.Select(r => r.ClaimId).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_AllClaimsWithoutApprovedAmount_CompletesWithNothingPaid()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        SetupClaimsResponse(new[]
        {
            new ClaimDto { Id = "c-null", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A", TotalChargeAmount = 5000m, AdjudicationResult = null, MemberId = "m1", Status = ClaimStatus.Approved }
        });
        SetupSinglePartnerPassThrough();

        var result = await CreateService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(PaymentRunStatus.Completed, result.Status);
        Assert.Equal(new[] { "c-null" }, result.MissingPlanPaidAmountClaimIds);
        Assert.Empty(result.PaymentIds);
        Assert.Equal(0m, result.TotalPaymentAmount);
        Assert.Empty(_reservations.All);
        await _paymentRepo.DidNotReceiveWithAnyArgs().CreateAsync(default!);
        // Excluded before trading-partner resolution: no lookup for it.
        await _tpClient.DidNotReceiveWithAnyArgs().GetByBillingProviderNpiAsync(default!, default!, default!);
    }

    private static ClaimDto ClaimWithLines(string id, decimal approved, params decimal?[] linePaid) => new()
    {
        Id = id, ClaimNumber = "CLM-" + id, BillingProviderNPI = "NPI-A", TotalChargeAmount = 100m * linePaid.Length,
        AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = approved }, MemberId = "m-" + id, Status = ClaimStatus.Approved,
        ServiceLines = linePaid.Select((p, i) => new ClaimServiceLineDto
        {
            LineNumber = i + 1, ProcedureCode = "99213", ChargeAmount = 100m, PaidAmount = p, Units = 1
        }).ToList()
    };

    [Fact]
    public async Task ExecutePaymentRunAsync_ServiceLinesDoNotBalance_ClaimNotPaid_Reported_LineNeverPaidAtCharge()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        SetupClaimsResponse(new[]
        {
            ClaimWithLines("c-ok", 80m, 80m),               // balanced
            ClaimWithLines("c-zero-line", 80m, 80m, null),  // unpriced line = 0 is consistent
            ClaimWithLines("c-unpriced", 80m, (decimal?)null), // 0 would not balance; was paid at charge 100
            ClaimWithLines("c-mismatch", 80m, 60m),         // lines do not add up
        });
        SetupSinglePartnerPassThrough();
        var captured = new List<Payment>();
        _paymentRepo.CreateAsync(Arg.Do<Payment>(captured.Add)).Returns(call => call.Arg<Payment>());

        var result = await CreateService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(new[] { "c-unpriced", "c-mismatch" }, result.UnbalancedServiceLineClaimIds);
        Assert.Contains(result.Warnings, w => w.Contains("c-unpriced") && w.Contains("would not balance"));
        Assert.Equal(new[] { "c-ok", "c-zero-line" }, result.ClaimIds);
        Assert.DoesNotContain(_reservations.All, r => r.ClaimId is "c-unpriced" or "c-mismatch");

        var lines = Assert.Single(captured).ClaimPayments.Single(cp => cp.ClaimId == "c-zero-line").ServiceLines;
        Assert.Equal(new[] { 80m, 0m }, lines.Select(l => l.PaymentAmount));
        Assert.Equal(160m, result.TotalPaymentAmount);
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_LinePaidAmount_ReadFromClaimsServiceLineAdjudicationResult()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        SetupSinglePartnerPassThrough();
        var captured = new List<Payment>();
        _paymentRepo.CreateAsync(Arg.Do<Payment>(captured.Add)).Returns(call => call.Arg<Payment>());

        // claims-service's Claim shape: the line's paid amount lives in
        // claimLines[].adjudicationResult.paidAmount (no top-level paidAmount).
        const string json = """
            [{"id":"c1","claimNumber":"CLM-1","memberId":"m1","billingProviderNPI":"NPI-A","totalChargeAmount":200,
              "status":5,"adjudicationResult":{"allowedAmount":190,"payerPayment":150,"patientResponsibility":40},
              "claimLines":[
                {"lineNumber":1,"procedureCode":"99213","chargeAmount":120,"units":1,"adjudicationResult":{"paidAmount":100}},
                {"lineNumber":2,"procedureCode":"85025","chargeAmount":80,"units":1,"adjudicationResult":{"paidAmount":50}}]}]
            """;
        _claimsHandler.NextResponse = req =>
            req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.StartsWith("/api/claims/search")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK);

        var result = await CreateService().ExecutePaymentRunAsync(run.Id);

        Assert.Empty(result.UnbalancedServiceLineClaimIds);
        var cp = Assert.Single(Assert.Single(captured).ClaimPayments);
        Assert.Equal(150m, cp.PaymentAmount);
        Assert.Equal(new[] { 100m, 50m }, cp.ServiceLines.Select(l => l.PaymentAmount));
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_RealGenerator_835Balances_BprEqualsSumClpLessPlb_SvcSumsEqualClp()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        SetupClaimsResponse(new[]
        {
            ClaimWithLines("c1", 80m, 50m, 30m),
            ClaimWithLines("c2", 120m, 120m, null),
            ClaimWithLines("c3", 0m, 0m),
        });
        _tpClient.GetByBillingProviderNpiAsync("test-tenant", "NPI-A", "Production")
            .Returns(new TradingPartnerSummary { TradingPartnerId = "TP-A", X12Config = new X12ConfigDto() });
        _paymentRepo.CreateAsync(Arg.Any<Payment>()).Returns(call => call.Arg<Payment>());
        var envelopes = new List<EraEnvelopeRecord>();
        _envelopeRepo.CreateAsync(Arg.Do<EraEnvelopeRecord>(envelopes.Add)).Returns(call => { var rec = call.Arg<EraEnvelopeRecord>(); rec.Id = "env-1"; return rec; });

        var service = new PaymentRunService(
            _paymentRepo, _runRepo, new BatchEraGeneratorService(NullLogger<BatchEraGeneratorService>.Instance),
            _mapper, _envelopeRepo, _tpClient, _httpFactory,
            NullLogger<PaymentRunService>.Instance, _configuration, _actor, _actor.SeparationOfDuties(), _reservations);

        var result = await service.ExecutePaymentRunAsync(run.Id);

        Assert.Equal(PaymentRunStatus.Completed, result.Status);
        var segments = Assert.Single(envelopes).EdiContent
            .Split('~', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('*')).ToList();
        var bpr02 = decimal.Parse(segments.Single(s => s[0] == "BPR")[2]);
        var clp04 = segments.Where(s => s[0] == "CLP").Select(s => decimal.Parse(s[4])).ToList();
        var plb = segments.Where(s => s[0] == "PLB").Sum(s => s.Skip(3).Where((_, i) => i % 2 == 1).Sum(decimal.Parse));
        Assert.Equal(200m, bpr02);
        Assert.Equal(bpr02, clp04.Sum() - plb);
        Assert.Equal(result.TotalPaymentAmount, bpr02);

        // Per CLP: its SVC03 lines add up to CLP04.
        var clpIndexes = segments.Select((s, i) => (s, i)).Where(x => x.s[0] == "CLP").Select(x => x.i).ToList();
        for (var k = 0; k < clpIndexes.Count; k++)
        {
            var end = k + 1 < clpIndexes.Count ? clpIndexes[k + 1] : segments.FindIndex(s => s[0] == "SE");
            var svc = segments.Skip(clpIndexes[k] + 1).Take(end - clpIndexes[k] - 1)
                .Where(s => s[0] == "SVC").Sum(s => decimal.Parse(s[3]));
            Assert.Equal(decimal.Parse(segments[clpIndexes[k]][4]), svc);
        }
        Assert.Equal("1123456789", segments.Single(s => s[0] == "TRN")[3]);
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_AsksForApprovedOnly_AndRefusesPendedOrDeniedWithStalePayerPayment()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        var pended = ClaimWithLines("c-pended", 80m, 80m);
        pended.Status = ClaimStatus.Pended;
        var denied = ClaimWithLines("c-denied", 80m, 80m);
        denied.Status = ClaimStatus.Denied;
        SetupClaimsResponse(new[] { ClaimWithLines("c-ok", 80m, 80m), pended, denied });
        SetupSinglePartnerPassThrough();

        var result = await CreateService().ExecutePaymentRunAsync(run.Id);

        var search = _claimsHandler.RecordedRequests.First(r => r.Uri.AbsolutePath.StartsWith("/api/claims/search"));
        Assert.Contains("status=5", search.Uri.Query); // claims-service ClaimStatus.Approved
        Assert.Equal(new[] { "c-ok" }, result.ClaimIds);
        Assert.Contains(result.Warnings, w => w.Contains("c-pended") && w.Contains("Pended"));
        Assert.Contains(result.Warnings, w => w.Contains("c-denied") && w.Contains("Denied"));
        Assert.DoesNotContain(_reservations.All, r => r.ClaimId is "c-pended" or "c-denied");
        Assert.Equal(80m, result.TotalPaymentAmount);
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_Clp03IsTotalCharge_Clp04IsPayerPayment_Clp05IsMemberResponsibility()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        var claim = ClaimWithLines("c1", 170m, 120m, 50m);
        claim.TotalChargeAmount = 300m;
        claim.AdjudicationResult!.AllowedAmount = 220m;
        claim.AdjudicationResult.PatientResponsibility = 50m;
        SetupClaimsResponse(new[] { claim });
        SetupSinglePartnerPassThrough();
        var captured = new List<Payment>();
        _paymentRepo.CreateAsync(Arg.Do<Payment>(captured.Add)).Returns(call => call.Arg<Payment>());

        await CreateService().ExecutePaymentRunAsync(run.Id);

        var cp = Assert.Single(Assert.Single(captured).ClaimPayments);
        Assert.Equal(300m, cp.ChargeAmount);                 // CLP03
        Assert.Equal(170m, cp.PaymentAmount);                // CLP04 = payerPayment, not allowed (220)
        Assert.Equal(50m, cp.PatientResponsibilityAmount);   // CLP05
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_LineOfBusinessFilter_SendsClaimsServiceValue()
    {
        var run = PendingRun();
        run.Criteria.LineOfBusiness = LineOfBusiness.Medicare;
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        SetupClaimsResponse(Array.Empty<ClaimDto>());

        await CreateService().ExecutePaymentRunAsync(run.Id);

        var search = _claimsHandler.RecordedRequests.First(r => r.Uri.AbsolutePath.StartsWith("/api/claims/search"));
        Assert.Contains("lineOfBusiness=2", search.Uri.Query); // claims-service Medicare == 2 (was 1 = Commercial)
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_AchWithoutOriginatingCompanyId_FailsBeforeAnyClaimIsReservedOrPaid()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Era:PayerRoutingNumber"] = "021000021",
                ["Era:PayerAccountNumber"] = "111",
                ["Era:PayeeRoutingNumber"] = "021000089",
                ["Era:PayeeAccountNumber"] = "222",
                ["TradingPartners:Environment"] = "Production",
            })
            .Build();
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        SetupClaimsResponse(new[]
        {
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A", TotalChargeAmount = 100m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 80m }, MemberId = "m1", Status = ClaimStatus.Approved }
        });
        SetupSinglePartnerPassThrough();

        var service = new PaymentRunService(
            _paymentRepo, _runRepo, _batchGen, _mapper, _envelopeRepo, _tpClient, _httpFactory,
            NullLogger<PaymentRunService>.Instance, configuration, _actor, _actor.SeparationOfDuties(), _reservations);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecutePaymentRunAsync(run.Id));

        Assert.Contains("BPR10", ex.Message);
        Assert.Equal(PaymentRunStatus.Failed, run.Status);
        Assert.Contains(run.Errors, e => e.Contains("Era:OriginatingCompanyId"));
        Assert.Empty(_reservations.All);
        await _paymentRepo.DidNotReceiveWithAnyArgs().CreateAsync(default!);
        Assert.Empty(_claimsHandler.RecordedRequests);
    }
}

/// <summary>
/// Minimal test handler that records all outbound requests and returns a
/// configured response. Avoids spinning up a full WireMock dep for the unit
/// test surface; sufficient for asserting on path + body shapes.
/// </summary>
internal class StubHttpHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage>? NextResponse { get; set; }
    public List<RecordedRequest> RecordedRequests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        RecordedRequests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));
        return NextResponse?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
    }

    public record RecordedRequest(HttpMethod Method, Uri Uri, string Body);
}
