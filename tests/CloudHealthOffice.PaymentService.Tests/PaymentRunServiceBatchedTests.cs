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
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A", TotalChargeAmount = 100m, ApprovedAmount = 80m, MemberId = "m1", Status = ClaimStatus.Approved },
            new ClaimDto { Id = "c2", ClaimNumber = "CLM-2", BillingProviderNPI = "NPI-B", TotalChargeAmount = 200m, ApprovedAmount = 160m, MemberId = "m2", Status = ClaimStatus.Approved }
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
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A", TotalChargeAmount = 100m, ApprovedAmount = 80m, MemberId = "m1", Status = ClaimStatus.Approved }
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
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-MISSING", TotalChargeAmount = 100m, ApprovedAmount = 80m, MemberId = "m1", Status = ClaimStatus.Approved }
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
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A1", TotalChargeAmount = 100m, ApprovedAmount = 80m, MemberId = "m1", Status = ClaimStatus.Approved },
            new ClaimDto { Id = "c2", ClaimNumber = "CLM-2", BillingProviderNPI = "NPI-A2", TotalChargeAmount = 100m, ApprovedAmount = 80m, MemberId = "m2", Status = ClaimStatus.Approved }
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
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A", TotalChargeAmount = 100m, ApprovedAmount = 80m, MemberId = "m1", Status = ClaimStatus.Approved },
            new ClaimDto { Id = "c-null", ClaimNumber = "CLM-2", BillingProviderNPI = "NPI-A", TotalChargeAmount = 5000m, ApprovedAmount = null, MemberId = "m2", Status = ClaimStatus.Approved },
            new ClaimDto { Id = "c-zero", ClaimNumber = "CLM-3", BillingProviderNPI = "NPI-A", TotalChargeAmount = 300m, ApprovedAmount = 0m, MemberId = "m3", Status = ClaimStatus.Approved }
        };
        SetupClaimsResponse(claims);
        SetupSinglePartnerPassThrough();

        var captured = new List<Payment>();
        _paymentRepo.CreateAsync(Arg.Do<Payment>(captured.Add)).Returns(call => call.Arg<Payment>());

        var result = await CreateService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(PaymentRunStatus.Completed, result.Status);

        // The claim without an approved amount is reported, not paid, not reserved.
        Assert.Equal(new[] { "c-null" }, result.MissingApprovedAmountClaimIds);
        Assert.Contains(result.Warnings, w => w.Contains("c-null") && w.Contains("no approved amount"));
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
            new ClaimDto { Id = "c-null", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A", TotalChargeAmount = 5000m, ApprovedAmount = null, MemberId = "m1", Status = ClaimStatus.Approved }
        });
        SetupSinglePartnerPassThrough();

        var result = await CreateService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(PaymentRunStatus.Completed, result.Status);
        Assert.Equal(new[] { "c-null" }, result.MissingApprovedAmountClaimIds);
        Assert.Empty(result.PaymentIds);
        Assert.Equal(0m, result.TotalPaymentAmount);
        Assert.Empty(_reservations.All);
        await _paymentRepo.DidNotReceiveWithAnyArgs().CreateAsync(default!);
        // Excluded before trading-partner resolution: no lookup for it.
        await _tpClient.DidNotReceiveWithAnyArgs().GetByBillingProviderNpiAsync(default!, default!, default!);
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
            new ClaimDto { Id = "c1", ClaimNumber = "CLM-1", BillingProviderNPI = "NPI-A", TotalChargeAmount = 100m, ApprovedAmount = 80m, MemberId = "m1", Status = ClaimStatus.Approved }
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
