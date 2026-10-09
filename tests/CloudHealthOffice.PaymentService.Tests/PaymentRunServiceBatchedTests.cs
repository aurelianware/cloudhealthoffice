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
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A", TotalChargeAmount = 100m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 80m }, ServiceLines = OneLine(80m), MemberId = "m1", Status = ClaimStatus.Approved },
            new ClaimDto { Id = "c2", ClaimNumber = "CLM-2", BillingProviderNPI = "NPI-B", TotalChargeAmount = 200m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 160m }, ServiceLines = OneLine(160m, 200m), MemberId = "m2", Status = ClaimStatus.Approved }
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
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A", TotalChargeAmount = 100m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 80m }, ServiceLines = OneLine(80m), MemberId = "m1", Status = ClaimStatus.Approved }
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
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-MISSING", TotalChargeAmount = 100m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 80m }, ServiceLines = OneLine(80m), MemberId = "m1", Status = ClaimStatus.Approved }
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
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A1", TotalChargeAmount = 100m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 80m }, ServiceLines = OneLine(80m), MemberId = "m1", Status = ClaimStatus.Approved },
            new ClaimDto { Id = "c2", ClaimNumber = "CLM-2", BillingProviderNPI = "NPI-A2", TotalChargeAmount = 100m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 80m }, ServiceLines = OneLine(80m), MemberId = "m2", Status = ClaimStatus.Approved }
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
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A", TotalChargeAmount = 100m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 80m }, ServiceLines = OneLine(80m), MemberId = "m1", Status = ClaimStatus.Approved },
            new ClaimDto { Id = "c-null", ClaimNumber = "CLM-2", BillingProviderNPI = "NPI-A", TotalChargeAmount = 5000m, AdjudicationResult = null, MemberId = "m2", Status = ClaimStatus.Approved },
            new ClaimDto { Id = "c-zero", ClaimNumber = "CLM-3", BillingProviderNPI = "NPI-A", TotalChargeAmount = 300m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 0m }, ServiceLines = OneLine(0m, 300m), MemberId = "m3", Status = ClaimStatus.Approved }
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

    /// <summary>One service line paying <paramref name="paid"/> (SVC03 balances CLP04).</summary>
    private static List<ClaimServiceLineDto> OneLine(decimal paid, decimal? charge = null) =>
        new() { new ClaimServiceLineDto { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = charge ?? paid + 20m, PaidAmount = paid, Units = 1 } };

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
            ClaimWithLines("c-partial", 80m, 50m, null),    // some lines priced, 50 + 0 != 80
            ClaimWithLines("c-mismatch", 80m, 60m),         // lines do not add up
        });
        SetupSinglePartnerPassThrough();
        var captured = new List<Payment>();
        _paymentRepo.CreateAsync(Arg.Do<Payment>(captured.Add)).Returns(call => call.Arg<Payment>());

        var result = await CreateService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(new[] { "c-partial", "c-mismatch" }, result.UnbalancedServiceLineClaimIds);
        Assert.Contains(result.Warnings, w => w.Contains("c-partial") && w.Contains("would not balance"));
        Assert.Equal(new[] { "c-ok", "c-zero-line" }, result.ClaimIds);
        Assert.DoesNotContain(_reservations.All, r => r.ClaimId is "c-partial" or "c-mismatch");

        var lines = Assert.Single(captured).ClaimPayments.Single(cp => cp.ClaimId == "c-zero-line").ServiceLines;
        Assert.Equal(new[] { 80m, 0m }, lines.Select(l => l.PaymentAmount));
        Assert.Equal(160m, result.TotalPaymentAmount);
    }

    // ── Claim-level-only adjudication (no line paid amounts) ──────────────

    [Fact]
    public async Task ExecutePaymentRunAsync_NoLinePaidAmounts_PaidAtClaimLevel_ClpWithoutSvc_Balances()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        SetupClaimsResponse(new[]
        {
            ClaimWithLines("c-claim-level", 80m, null, null), // payerPayment only, no line results
            ClaimWithLines("c-lines", 50m, 30m, 20m),
            ClaimWithLines("c-partial", 80m, 50m, null),      // some lines priced and short: excluded
        });
        var (envelopes, payments) = SetupRealGenerator();

        var result = await CreateService(new BatchEraGeneratorService(NullLogger<BatchEraGeneratorService>.Instance))
            .ExecutePaymentRunAsync(run.Id);

        Assert.Equal(new[] { "c-claim-level", "c-lines" }, result.ClaimIds);
        Assert.Equal(new[] { "c-partial" }, result.UnbalancedServiceLineClaimIds);
        var claimLevel = Assert.Single(payments).ClaimPayments.Single(cp => cp.ClaimId == "c-claim-level");
        Assert.Equal(80m, claimLevel.PaymentAmount);
        Assert.Empty(claimLevel.ServiceLines);

        var segments = Segments(Assert.Single(envelopes).EdiContent);
        Assert.Empty(SvcOf(segments, "CLM-c-claim-level"));
        Assert.Equal(new[] { 30m, 20m }, SvcOf(segments, "CLM-c-lines").Select(s => decimal.Parse(s[3])));
        Assert.Equal(130m, decimal.Parse(segments.Single(s => s[0] == "BPR")[2]));
        Assert.Equal(130m, segments.Where(s => s[0] == "CLP").Sum(s => decimal.Parse(s[4])));
    }

    // ── claims-service wire: payee name and institutional CLP ──────────────

    [Fact]
    public async Task ExecutePaymentRunAsync_ClaimsServiceWire_PayeeIsBillingProviderName_InstitutionalClpCarriesCodes()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        // As claims-service sends Claim: camelCase, enums as numbers,
        // billingProviderName, institutional detail on Claim.Institutional.
        const string approved = """
            [{"id":"c-ip","claimNumber":"CLM-IP","memberId":"m1","billingProviderNPI":"NPI-A",
              "billingProviderName":"SYNTHETIC GENERAL HOSPITAL","totalChargeAmount":1000,"status":5,
              "claimType":2,"claimFrequencyCode":"1",
              "institutional":{"facilityTypeCode":"11","drgCode":"470"},
              "adjudicationResult":{"payerPayment":600,"patientResponsibility":0,
                "adjustmentReasons":[{"groupCode":"CO","reasonCode":"45","amount":400}]}},
             {"id":"c-prof","claimNumber":"CLM-PROF","memberId":"m2","billingProviderNPI":"NPI-B",
              "totalChargeAmount":100,"status":5,"claimType":1,
              "adjudicationResult":{"payerPayment":80,"patientResponsibility":0,
                "adjustmentReasons":[{"groupCode":"CO","reasonCode":"45","amount":20}]}}]
            """;
        _claimsHandler.NextResponse = req =>
            req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.StartsWith("/api/claims/search")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(req.RequestUri.Query.Contains("status=6") ? "[]" : approved,
                        System.Text.Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK);
        var (envelopes, payments) = SetupRealGenerator();

        await CreateRealService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal("SYNTHETIC GENERAL HOSPITAL", payments.Single(p => p.PayeeNPI == "NPI-A").PayeeName);
        Assert.Equal("NPI-B", payments.Single(p => p.PayeeNPI == "NPI-B").PayeeName); // no name sent: the NPI
        var a = Segments(envelopes.Single(e => e.TradingPartnerId == "TP-A").EdiContent);
        Assert.Equal(new[] { "N1", "PE", "SYNTHETIC GENERAL HOSPITAL", "XX", "NPI-A" }, a.Single(s => s[0] == "N1" && s[1] == "PE"));
        Assert.Equal(new[] { "CLP", "CLM-IP", "1", "1000.00", "600.00", "0.00", "HM", "c-ip", "11", "1", "", "470" },
            a.Single(s => s[0] == "CLP"));
        var b = Segments(envelopes.Single(e => e.TradingPartnerId == "TP-B").EdiContent);
        Assert.Equal(8, b.Single(s => s[0] == "CLP").Length); // professional: ends at CLP07
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_DistinctPayToNpi_PayeeNameIsThePayToNpi_NotTheBillingProviderName()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        var claim = ClaimWithLines("c-payto", 80m, 80m);
        claim.ProviderName = "ACME BILLING GROUP"; // the billing provider (NPI-A), not the payee
        claim.PayToProviderNPI = "NPI-B";
        SetupClaimsResponse(new[] { claim }, Array.Empty<ClaimDto>());
        var (envelopes, payments) = SetupRealGenerator();

        await CreateRealService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(("NPI-B", "NPI-B"), (Assert.Single(payments).PayeeNPI, payments[0].PayeeName));
        var segments = Segments(Assert.Single(envelopes).EdiContent);
        Assert.Equal(new[] { "N1", "PE", "NPI-B", "XX", "NPI-B" }, segments.Single(s => s[0] == "N1" && s[1] == "PE"));
    }

    // ── Denials: zero-pay claims in the run's 835 ─────────────────────────

    private void SetupClaimsResponse(IEnumerable<ClaimDto> approved, IEnumerable<ClaimDto> denied)
    {
        _claimsHandler.NextResponse = req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.StartsWith("/api/claims/search"))
            {
                var list = req.RequestUri.Query.Contains("status=6") ? denied : approved;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(list.ToList()) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
    }

    private (List<EraEnvelopeRecord> Envelopes, List<Payment> Payments) SetupRealGenerator()
    {
        _tpClient.GetByBillingProviderNpiAsync("test-tenant", "NPI-A", "Production")
            .Returns(new TradingPartnerSummary { TradingPartnerId = "TP-A", X12Config = new X12ConfigDto() });
        _tpClient.GetByBillingProviderNpiAsync("test-tenant", "NPI-B", "Production")
            .Returns(new TradingPartnerSummary { TradingPartnerId = "TP-B", X12Config = new X12ConfigDto() });
        var payments = new List<Payment>();
        _paymentRepo.CreateAsync(Arg.Do<Payment>(payments.Add)).Returns(call => call.Arg<Payment>());
        var envelopes = new List<EraEnvelopeRecord>();
        _envelopeRepo.CreateAsync(Arg.Do<EraEnvelopeRecord>(envelopes.Add))
            .Returns(call => { var rec = call.Arg<EraEnvelopeRecord>(); rec.Id = "env-" + rec.TradingPartnerId; return rec; });
        _envelopeRepo.GetClaimIdsWithEnvelopeAsync(default!, default).ReturnsForAnyArgs(Array.Empty<string>());
        _paymentRepo.GetClaimIdsWithPaymentAsync(default!, default).ReturnsForAnyArgs(Array.Empty<string>());
        return (envelopes, payments);
    }

    private PaymentRunService CreateService(IBatchEraGeneratorService generator, ICarcRarcMappingService? mapper = null) => new(
        _paymentRepo, _runRepo, generator, mapper ?? _mapper, _envelopeRepo, _tpClient, _httpFactory,
        NullLogger<PaymentRunService>.Instance, _configuration, _actor, _actor.SeparationOfDuties(), _reservations);

    private PaymentRunService CreateRealService() => CreateService(
        new BatchEraGeneratorService(NullLogger<BatchEraGeneratorService>.Instance),
        new CarcRarcMappingService(NullLogger<CarcRarcMappingService>.Instance));

    private static ClaimDto Denied(string id, string npi = "NPI-A", string? carc = "50", params string[] rarcs) => new()
    {
        Id = id, ClaimNumber = "CLM-" + id, BillingProviderNPI = npi, TotalChargeAmount = 200m, MemberId = "m-" + id,
        Status = ClaimStatus.Denied,
        AdjudicationResult = new ClaimAdjudicationDto
        {
            PayerPayment = 0m, DenialReasonCode = carc, DenialReason = "Not medically necessary", RemarkCodes = rarcs.ToList()
        },
        ServiceLines = new List<ClaimServiceLineDto>
        {
            new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 200m, Units = 1 }
        }
    };

    private static List<string[]> Segments(string edi) =>
        edi.Split('~', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('*')).ToList();

    /// <summary>The segments of one claim's 2100 loop (CLP up to the next CLP / PLB / SE).</summary>
    private static List<string[]> LoopOf(List<string[]> segments, string patientControlNumber)
    {
        var start = segments.FindIndex(s => s[0] == "CLP" && s[1] == patientControlNumber);
        Assert.True(start >= 0, $"no CLP for {patientControlNumber}");
        var end = segments.FindIndex(start + 1, s => s[0] is "CLP" or "PLB" or "SE");
        return segments.Skip(start).Take(end - start).ToList();
    }

    private static List<string[]> SvcOf(List<string[]> segments, string patientControlNumber) =>
        LoopOf(segments, patientControlNumber).Where(s => s[0] == "SVC").ToList();

    [Fact]
    public async Task ExecutePaymentRunAsync_Denial_RemittedAsZeroPayClaim_InSameEnvelope_NoPaymentNoReservationNoFinalize()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        SetupClaimsResponse(new[] { ClaimWithLines("c1", 80m, 80m) }, new[] { Denied("d1", rarcs: "N115") });
        var (envelopes, payments) = SetupRealGenerator();

        var result = await CreateRealService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(PaymentRunStatus.Completed, result.Status);
        Assert.Equal(new[] { "d1" }, result.RemittedDeniedClaimIds);
        Assert.Equal(new[] { "c1" }, result.ClaimIds);
        Assert.Equal(80m, result.TotalPaymentAmount);
        Assert.DoesNotContain(payments.SelectMany(p => p.ClaimPayments), cp => cp.ClaimId == "d1");
        Assert.DoesNotContain(_reservations.All, r => r.ClaimId == "d1");
        Assert.DoesNotContain(_claimsHandler.RecordedRequests, r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.Contains("/d1/"));
        Assert.Contains(_claimsHandler.RecordedRequests, r => r.Uri.Query.Contains("status=6"));

        var envelope = Assert.Single(envelopes);
        Assert.Equal(new[] { "c1", "d1" }, envelope.ClaimIds);
        var segments = Segments(envelope.EdiContent);
        var bpr = segments.Single(s => s[0] == "BPR");
        Assert.Equal("80.00", bpr[2]); // denial adds 0
        Assert.Equal(80m, segments.Where(s => s[0] == "CLP").Sum(s => decimal.Parse(s[4])));
        Assert.Equal(Assert.Single(payments).CheckNumber, segments.Single(s => s[0] == "TRN")[2]);

        var loop = LoopOf(segments, "CLM-d1");
        Assert.Equal(new[] { "CLP", "CLM-d1", "4", "200.00", "0.00", "0.00", "HM", "d1" }, loop[0]);
        Assert.Contains(loop, s => s.SequenceEqual(new[] { "CAS", "CO", "50", "200.00" }));
        Assert.Contains(loop, s => s.SequenceEqual(new[] { "MOA", "", "", "N115" }));
        Assert.DoesNotContain(loop, s => s[0] == "SVC"); // no line paid amounts: claim level
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_InstitutionalDenial_RemarksInMia_ProfessionalInMoa()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        var institutional = Denied("d-inst", rarcs: "MA130");
        institutional.ClaimType = ClaimFormType.Institutional;
        SetupClaimsResponse(Array.Empty<ClaimDto>(), new[] { institutional, Denied("d-prof", rarcs: "N115") });
        var (envelopes, _) = SetupRealGenerator();

        await CreateRealService().ExecutePaymentRunAsync(run.Id);

        var segments = Segments(Assert.Single(envelopes).EdiContent);
        var inst = LoopOf(segments, "CLM-d-inst");
        Assert.Contains(inst, s => s[0] == "MIA" && s[1] == "0" && s[5] == "MA130");
        Assert.DoesNotContain(inst, s => s[0] == "MOA");
        var prof = LoopOf(segments, "CLM-d-prof");
        Assert.Contains(prof, s => s.SequenceEqual(new[] { "MOA", "", "", "N115" }));
        Assert.DoesNotContain(prof, s => s[0] == "MIA");
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_OnlyDenials_NonPayment835_Bpr02Zero_NoCheckOrPayment()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        SetupClaimsResponse(Array.Empty<ClaimDto>(), new[] { Denied("d1"), Denied("d2", npi: "NPI-B") });
        var (envelopes, payments) = SetupRealGenerator();

        var result = await CreateRealService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(PaymentRunStatus.Completed, result.Status);
        Assert.Equal(new[] { "d1", "d2" }, result.RemittedDeniedClaimIds);
        Assert.Empty(payments);
        Assert.Empty(result.PaymentIds);
        Assert.Equal(0m, result.TotalPaymentAmount);
        Assert.Equal(1000000, result.NextCheckNumber); // no check allocated
        Assert.Equal(2, envelopes.Count);
        Assert.Equal(2, result.EraEnvelopeIds.Count);

        var traces = new List<string>();
        foreach (var envelope in envelopes)
        {
            var segments = Segments(envelope.EdiContent);
            var bpr = segments.Single(s => s[0] == "BPR");
            Assert.Equal(17, bpr.Length);
            Assert.Equal("H", bpr[1]);
            Assert.Equal("0.00", bpr[2]);
            Assert.Equal("NON", bpr[4]); // run method is ACH, but nothing is paid
            Assert.All(bpr.Skip(5).Take(11), e => Assert.Equal(string.Empty, e));
            Assert.Equal("20260504", bpr[16]);
            var trn = segments.Single(s => s[0] == "TRN");
            Assert.Equal("1123456789", trn[3]);
            traces.Add(trn[2]);
        }
        Assert.Equal(new[] { "PR-20260501-A1B2-D1", "PR-20260501-A1B2-D2" }, traces.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_DenialAlreadyIn835_NotRemittedAgain()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        SetupClaimsResponse(Array.Empty<ClaimDto>(), new[] { Denied("d-old"), Denied("d-new") });
        var (envelopes, _) = SetupRealGenerator();
        _envelopeRepo.GetClaimIdsWithEnvelopeAsync(Arg.Any<IReadOnlyCollection<string>>(), false)
            .Returns(call => call.Arg<IReadOnlyCollection<string>>().Where(id => id == "d-old").ToList());

        var result = await CreateRealService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(new[] { "d-new" }, result.RemittedDeniedClaimIds);
        Assert.Equal(new[] { "d-new" }, Assert.Single(envelopes).ClaimIds);
        await _envelopeRepo.Received().GetClaimIdsWithEnvelopeAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains("d-old") && ids.Contains("d-new")), false);
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_DenialWithoutReason_NotRemitted_Listed()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        SetupClaimsResponse(Array.Empty<ClaimDto>(), new[] { Denied("d-no-carc", carc: null) });
        var (envelopes, _) = SetupRealGenerator();

        var result = await CreateRealService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(new[] { "d-no-carc" }, result.DeniedWithoutReasonClaimIds);
        Assert.Empty(result.RemittedDeniedClaimIds);
        Assert.Empty(envelopes);
        Assert.Contains(result.Warnings, w => w.Contains("d-no-carc") && w.Contains("CARC"));
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_DenialWithLinePaidAmounts_SvcBalanceToZero_ElseExcluded()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        var zeroLines = Denied("d-lines");
        zeroLines.ServiceLines![0].PaidAmount = 0m;
        var paidLine = Denied("d-paid-line");
        paidLine.ServiceLines![0].PaidAmount = 40m; // a denial cannot pay a line
        SetupClaimsResponse(Array.Empty<ClaimDto>(), new[] { zeroLines, paidLine });
        var (envelopes, _) = SetupRealGenerator();

        var result = await CreateRealService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(new[] { "d-lines" }, result.RemittedDeniedClaimIds);
        Assert.Equal(new[] { "d-paid-line" }, result.UnbalancedServiceLineClaimIds);
        var svc = Assert.Single(SvcOf(Segments(Assert.Single(envelopes).EdiContent), "CLM-d-lines"));
        Assert.Equal("0.00", svc[3]);
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_IncludeDeniedClaimsFalse_DoesNotSearchDenials()
    {
        var run = PendingRun();
        run.Criteria.IncludeDeniedClaims = false;
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        SetupClaimsResponse(Array.Empty<ClaimDto>(), new[] { Denied("d1") });
        var (envelopes, _) = SetupRealGenerator();

        var result = await CreateRealService().ExecutePaymentRunAsync(run.Id);

        Assert.Empty(result.RemittedDeniedClaimIds);
        Assert.Empty(envelopes);
        Assert.DoesNotContain(_claimsHandler.RecordedRequests, r => r.Uri.Query.Contains("status=6"));
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
        claim.ServiceLines![0].ChargeAmount = 200m; // CLP03 = sum of SVC02
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
    public async Task ExecutePaymentRunAsync_NegativePlanPaid_NotPaid_Reported_ZeroPayStillPaid()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        SetupClaimsResponse(new[]
        {
            ClaimWithLines("c-ok", 80m, 80m),
            ClaimWithLines("c-negative", -50m, -50m),   // balanced lines, but a negative plan payment
            ClaimWithLines("c-zero", 0m, 0m),
        });
        SetupSinglePartnerPassThrough();
        var captured = new List<Payment>();
        _paymentRepo.CreateAsync(Arg.Do<Payment>(captured.Add)).Returns(call => call.Arg<Payment>());

        var result = await CreateService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(new[] { "c-negative" }, result.NegativePlanPaidClaimIds);
        Assert.Contains(result.Warnings, w => w.Contains("c-negative") && w.Contains("negative"));
        Assert.DoesNotContain(_reservations.All, r => r.ClaimId == "c-negative");
        Assert.Equal(new[] { "c-ok", "c-zero" }, result.ClaimIds);
        Assert.Equal(80m, Assert.Single(captured).TotalPaymentAmount);
        Assert.Equal(80m, result.TotalPaymentAmount);
    }

    [Fact]
    public async Task ExecutePaymentRunAsync_ClaimWithNoServiceLines_PaidAtClaimLevel()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<PaymentRun>()).Returns(call => call.Arg<PaymentRun>());
        SetupClaimsResponse(new[]
        {
            ClaimWithLines("c-ok", 80m, 80m),
            ClaimWithLines("c-no-lines", 800m),          // claim-level-only adjudication
        });
        SetupSinglePartnerPassThrough();
        var captured = new List<Payment>();
        _paymentRepo.CreateAsync(Arg.Do<Payment>(captured.Add)).Returns(call => call.Arg<Payment>());

        var result = await CreateService().ExecutePaymentRunAsync(run.Id);

        // 005010X221A1: the 2110 loop is situational; CLP04 alone balances to BPR02.
        Assert.Empty(result.UnbalancedServiceLineClaimIds);
        Assert.Equal(new[] { "c-ok", "c-no-lines" }, result.ClaimIds);
        Assert.Empty(Assert.Single(captured).ClaimPayments.Single(cp => cp.ClaimId == "c-no-lines").ServiceLines);
        Assert.Equal(880m, result.TotalPaymentAmount);
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
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A", TotalChargeAmount = 100m, AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = 80m }, ServiceLines = OneLine(80m), MemberId = "m1", Status = ClaimStatus.Approved }
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
