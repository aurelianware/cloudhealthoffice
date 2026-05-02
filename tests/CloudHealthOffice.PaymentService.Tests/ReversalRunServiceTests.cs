using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PaymentService.Models;
using PaymentService.Repositories;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// Capability 5.12b — covers <see cref="ReversalRunService"/>: lifecycle
/// (Create / Execute / Get / Cancel), reversal Payment construction
/// (sign-flipped amounts, CLP02="22"), envelope persistence with
/// ReversalRunId, cross-service void invocation, partial-failure
/// warnings, and idempotent re-execution guard.
/// </summary>
public class ReversalRunServiceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IPaymentRepository _paymentRepo = Substitute.For<IPaymentRepository>();
    private readonly IReversalRunRepository _runRepo = Substitute.For<IReversalRunRepository>();
    private readonly IBatchEraGeneratorService _batchGen = Substitute.For<IBatchEraGeneratorService>();
    private readonly IEraEnvelopeRepository _envelopeRepo = Substitute.For<IEraEnvelopeRepository>();
    private readonly ITradingPartnersClient _tpClient = Substitute.For<ITradingPartnersClient>();
    private readonly StubHttpHandler _claimsHandler = new();
    private readonly IHttpClientFactory _httpFactory = Substitute.For<IHttpClientFactory>();
    private readonly IConfiguration _configuration;

    public ReversalRunServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Era:InterchangeSenderId"] = "SENDER",
                ["Era:InterchangeReceiverId"] = "RECEIVER",
                ["Payer:Name"] = "Cloud Health Office",
                ["Payer:Id"] = "CHO",
                ["TradingPartners:Environment"] = "Production",
            })
            .Build();

        var http = new HttpClient(_claimsHandler) { BaseAddress = new Uri("http://claims-service") };
        _httpFactory.CreateClient("ClaimsService").Returns(http);

        _paymentRepo.CreateAsync(Arg.Any<Payment>()).Returns(call =>
        {
            var p = call.Arg<Payment>();
            if (string.IsNullOrEmpty(p.Id)) p.Id = Guid.NewGuid().ToString();
            return p;
        });
        _envelopeRepo.CreateAsync(Arg.Any<EraEnvelopeRecord>()).Returns(call =>
        {
            var rec = call.Arg<EraEnvelopeRecord>();
            if (string.IsNullOrEmpty(rec.Id)) rec.Id = "env-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            return rec;
        });
    }

    private ReversalRunService CreateService() => new(
        _paymentRepo,
        _runRepo,
        _batchGen,
        _envelopeRepo,
        _tpClient,
        _httpFactory,
        NullLogger<ReversalRunService>.Instance,
        _configuration);

    private static ReversalRun PendingRun() => new()
    {
        Id = "rr-1",
        TenantId = "test-tenant",
        ReversalRunNumber = "RR-20260502-AB12CD",
        Status = ReversalRunStatus.Pending,
        Criteria = new ReversalRunCriteria(),
    };

    [Fact]
    public async Task CreateReversalRunAsync_PersistsRowInPendingState()
    {
        _runRepo.CreateAsync(Arg.Any<ReversalRun>()).Returns(call =>
        {
            var r = call.Arg<ReversalRun>();
            r.TenantId = "test-tenant";
            return r;
        });

        var run = await CreateService().CreateReversalRunAsync(
            new ReversalRunCriteria(), createdBy: "operator-1");

        Assert.Equal(ReversalRunStatus.Pending, run.Status);
        Assert.StartsWith("RR-", run.ReversalRunNumber);
        Assert.Equal("operator-1", run.CreatedBy);
    }

    [Fact]
    public async Task ExecuteReversalRunAsync_HappyPath_PersistsEnvelopesWithReversalRunId()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<ReversalRun>()).Returns(call => call.Arg<ReversalRun>());

        SetupAdjustmentList(new[]
        {
            BuildAdjustmentDto(id: "adj-1", predecessorId: "pred-1"),
        });
        SetupClaimResponse("pred-1", BuildClaim("pred-1", approvedAmount: 800m));

        _tpClient.GetByBillingProviderNpiAsync("test-tenant", "1234567890", "Production")
            .Returns(new TradingPartnerSummary
            {
                TradingPartnerId = "TP-A",
                X12Config = new X12ConfigDto { SenderId = "S", ReceiverId = "R" },
            });

        _batchGen.GenerateBatch(Arg.Any<IEnumerable<EraPaymentInput>>(), Arg.Any<IReadOnlyDictionary<string, TradingPartnerInfo>>())
            .Returns(call =>
            {
                var inputs = call.Arg<IEnumerable<EraPaymentInput>>().ToList();
                Assert.All(inputs, i => Assert.True(i.IsReversal));
                return inputs
                    .GroupBy(i => i.TradingPartnerId)
                    .Select(g => new EraEnvelope(
                        TradingPartnerId: g.Key,
                        EdiContent: "ISA*REV~",
                        ClaimCount: 1,
                        TotalPaymentAmount: g.Sum(i => i.Payment.TotalPaymentAmount),
                        ControlNumber: "000000001",
                        ClaimIds: g.SelectMany(i => i.Payment.ClaimPayments.Select(cp => cp.ClaimId)).ToList(),
                        IsReversal: true))
                    .ToList();
            });

        // Default void response is 200 OK from the stub.
        var executed = await CreateService().ExecuteReversalRunAsync(run.Id);

        Assert.Equal(ReversalRunStatus.Completed, executed.Status);
        Assert.Single(executed.EraEnvelopeIds);
        Assert.Single(executed.AdjustmentIds); // void succeeded
        Assert.Equal(1, executed.TotalAdjustments);
        Assert.True(executed.TotalReversalAmount < 0); // sign-flipped

        // Envelope persisted with ReversalRunId set, PaymentRunId blank.
        await _envelopeRepo.Received().CreateAsync(
            Arg.Is<EraEnvelopeRecord>(r => r.ReversalRunId == "rr-1" && string.IsNullOrEmpty(r.PaymentRunId)));

        // Payment carried IsReversal=true and CLP02="22".
        await _paymentRepo.Received().CreateAsync(
            Arg.Is<Payment>(p => p.IsReversal &&
                p.ClaimPayments.All(cp => cp.ClaimStatusCode == "22") &&
                p.TotalPaymentAmount < 0));
    }

    [Fact]
    public async Task ExecuteReversalRunAsync_VoidEndpointReturnsWarning_RunCompletesWithWarning()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<ReversalRun>()).Returns(call => call.Arg<ReversalRun>());

        SetupAdjustmentList(new[] { BuildAdjustmentDto(id: "adj-1", predecessorId: "pred-1") });
        SetupClaimResponse("pred-1", BuildClaim("pred-1", approvedAmount: 800m));

        _tpClient.GetByBillingProviderNpiAsync("test-tenant", "1234567890", "Production")
            .Returns(new TradingPartnerSummary
            {
                TradingPartnerId = "TP-A",
                X12Config = new X12ConfigDto(),
            });

        _batchGen.GenerateBatch(Arg.Any<IEnumerable<EraPaymentInput>>(), Arg.Any<IReadOnlyDictionary<string, TradingPartnerInfo>>())
            .Returns(call =>
            {
                var inputs = call.Arg<IEnumerable<EraPaymentInput>>().ToList();
                return inputs.Select(i => new EraEnvelope("TP-A", "ISA~", 1, i.Payment.TotalPaymentAmount, "00001",
                    new[] { i.Payment.ClaimPayments[0].ClaimId }, true)).ToList();
            });

        // Override void response to 422 InvalidSourceState.
        _claimsHandler.NextResponse = req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/api/v1/adjustments"))
                return _adjustmentsResponse!;
            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath.StartsWith("/api/claims/"))
                return _claimResponse!;
            if (req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath.EndsWith("/void"))
                return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                {
                    Content = new StringContent("{\"message\":\"already voided in another flow\"}"),
                };
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        var executed = await CreateService().ExecuteReversalRunAsync(run.Id);

        Assert.Equal(ReversalRunStatus.Completed, executed.Status);
        Assert.NotEmpty(executed.Warnings);
        Assert.Empty(executed.AdjustmentIds); // void didn't succeed → not added
    }

    [Fact]
    public async Task ExecuteReversalRunAsync_NoAdjustments_CompletesWithWarning()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<ReversalRun>()).Returns(call => call.Arg<ReversalRun>());

        SetupAdjustmentList(Array.Empty<ClaimAdjustmentDto>());

        var executed = await CreateService().ExecuteReversalRunAsync(run.Id);

        Assert.Equal(ReversalRunStatus.Completed, executed.Status);
        Assert.Contains(executed.Warnings, w => w.Contains("No PendingReversal"));
        Assert.Empty(executed.EraEnvelopeIds);
    }

    [Fact]
    public async Task ExecuteReversalRunAsync_AlreadyRunning_Throws()
    {
        var run = PendingRun();
        run.Status = ReversalRunStatus.Running;
        _runRepo.GetByIdAsync(run.Id).Returns(run);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().ExecuteReversalRunAsync(run.Id));
    }

    [Fact]
    public async Task ExecuteReversalRunAsync_RunNotFound_Throws()
    {
        _runRepo.GetByIdAsync("missing").Returns((ReversalRun?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().ExecuteReversalRunAsync("missing"));
    }

    [Fact]
    public async Task CancelReversalRunAsync_Pending_TransitionsToCancelled()
    {
        var run = PendingRun();
        _runRepo.GetByIdAsync(run.Id).Returns(run);
        _runRepo.UpdateAsync(Arg.Any<ReversalRun>()).Returns(call => call.Arg<ReversalRun>());

        await CreateService().CancelReversalRunAsync(run.Id);

        Assert.Equal(ReversalRunStatus.Cancelled, run.Status);
    }

    [Fact]
    public async Task CancelReversalRunAsync_Running_Throws()
    {
        var run = PendingRun();
        run.Status = ReversalRunStatus.Running;
        _runRepo.GetByIdAsync(run.Id).Returns(run);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().CancelReversalRunAsync(run.Id));
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private HttpResponseMessage? _adjustmentsResponse;
    private HttpResponseMessage? _claimResponse;

    private void SetupAdjustmentList(IEnumerable<ClaimAdjustmentDto> items)
    {
        var listResponse = new ClaimAdjustmentListResponseDto
        {
            Items = items.ToList(),
            Total = items.Count(),
            Page = 1,
            PageSize = 200,
        };
        _adjustmentsResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(listResponse, options: Json),
        };
        WireDefaultHandler();
    }

    private void SetupClaimResponse(string claimId, ClaimDto claim)
    {
        _claimResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(claim, options: Json),
        };
        WireDefaultHandler();
    }

    private void WireDefaultHandler()
    {
        _claimsHandler.NextResponse = req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/api/v1/adjustments"))
                return _adjustmentsResponse ?? new HttpResponseMessage(HttpStatusCode.OK);
            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath.StartsWith("/api/claims/"))
                return _claimResponse ?? new HttpResponseMessage(HttpStatusCode.OK);
            // Default: void POST returns 200.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { outcome = "Voided" }, options: Json),
            };
        };
    }

    private static ClaimAdjustmentDto BuildAdjustmentDto(string id, string predecessorId) => new()
    {
        Id = id,
        ClaimVersionId = predecessorId,
        PredecessorClaimId = predecessorId,
        PredecessorVersionId = predecessorId,
        NewClaimId = id + "-new",
        AdjustmentReason = "operator correction",
        Status = ClaimAdjustmentDtoStatus.PendingReversal,
        CreatedBy = "operator",
        CreatedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static ClaimDto BuildClaim(string id, decimal approvedAmount) => new()
    {
        Id = id,
        ClaimNumber = "CLM-" + id,
        MemberId = "m1",
        BillingProviderNPI = "1234567890",
        ProviderName = "Acme",
        TotalChargeAmount = 1000m,
        ApprovedAmount = approvedAmount,
        PatientResponsibility = 200m,
        Status = ClaimStatus.Paid,
        ServiceDateFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        AdjudicationResult = new ClaimAdjudicationDto
        {
            AllowedAmount = approvedAmount,
            PayerPayment = approvedAmount,
            DeductibleAmount = 0,
            CoinsuranceAmount = 0,
            CopayAmount = 0,
            PatientResponsibility = 200m,
            AdjustmentReasons = new List<ClaimAdjustmentReasonDto>
            {
                new() { GroupCode = "PR", ReasonCode = "1", Amount = 200m, Description = "Deductible Amount" },
            },
        },
        ServiceLines = new List<ClaimServiceLineDto>
        {
            new()
            {
                LineNumber = 1,
                ProcedureCode = "99213",
                ChargeAmount = 1000m,
                PaidAmount = approvedAmount,
                Units = 1,
            },
        },
    };
}
