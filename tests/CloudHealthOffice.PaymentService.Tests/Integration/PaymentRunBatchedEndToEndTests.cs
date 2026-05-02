using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PaymentService.Models;
using PaymentService.Repositories;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests.Integration;

/// <summary>
/// End-to-end exercise of the 5.10 batched PaymentRun flow with the
/// real <see cref="BatchEraGeneratorService"/>, real
/// <see cref="CarcRarcMappingService"/>, and real
/// <see cref="InMemoryEraEnvelopeRepository"/>. claims-service and
/// trading-partner-service are stubbed at the HTTP boundary.
///
/// Verifies the full happy path: 50 claims across 3 trading partners
/// → 3 envelopes generated → all 50 claims finalized via the
/// remittance endpoint → mixed denial/payment scenarios produce the
/// expected CAS structure.
/// </summary>
public class PaymentRunBatchedEndToEndTests
{
    private readonly InMemoryPaymentRepository _paymentRepo = new();
    private readonly InMemoryPaymentRunRepository _runRepo = new();
    private readonly InMemoryEraEnvelopeRepository _envelopeRepo;
    private readonly BatchEraGeneratorService _batchGen;
    private readonly CarcRarcMappingService _mapper;
    private readonly ITradingPartnersClient _tpClient;
    private readonly StubHttpHandler _claimsHandler = new();
    private readonly IHttpClientFactory _httpFactory = Substitute.For<IHttpClientFactory>();
    private readonly IConfiguration _configuration;

    public PaymentRunBatchedEndToEndTests()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = "test-tenant";
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        _envelopeRepo = new InMemoryEraEnvelopeRepository(accessor);

        _batchGen = new BatchEraGeneratorService(NullLogger<BatchEraGeneratorService>.Instance);
        _mapper = new CarcRarcMappingService(NullLogger<CarcRarcMappingService>.Instance);

        _tpClient = Substitute.For<ITradingPartnersClient>();

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Era:InterchangeSenderId"] = "DEFAULTSENDER",
                ["Era:InterchangeReceiverId"] = "DEFAULTRECEIVER",
                ["TradingPartners:Environment"] = "Production",
                ["Payer:Name"] = "Cloud Health Office",
                ["Payer:Id"] = "CHO",
                ["Payment:StartingCheckNumber"] = "1000000"
            })
            .Build();

        var http = new HttpClient(_claimsHandler) { BaseAddress = new Uri("http://claims-service") };
        _httpFactory.CreateClient("ClaimsService").Returns(http);
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
        _configuration);

    [Fact]
    public async Task EndToEnd_50ClaimsAcross3Partners_ProducesThreeEnvelopesAndFinalizesAll()
    {
        var run = await _runRepo.CreateAsync(new PaymentRun
        {
            Id = "run-1",
            TenantId = "test-tenant",
            PaymentRunNumber = "PR-20260501-A1B2C3",
            Status = PaymentRunStatus.Pending,
            Criteria = new PaymentRunCriteria { GroupByProvider = true },
            NextCheckNumber = 1000000,
            PaymentMethod = "ACH",
            PaymentDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc)
        });

        var partnerNpis = new[] { ("NPI-A", "TP-A"), ("NPI-B", "TP-B"), ("NPI-C", "TP-C") };
        var claims = new List<ClaimDto>();
        for (int i = 0; i < 50; i++)
        {
            var (npi, _) = partnerNpis[i % 3];
            claims.Add(new ClaimDto
            {
                Id = $"c{i}",
                ClaimNumber = $"CLM-{i:000}",
                BillingProviderNPI = npi,
                MemberId = $"m{i}",
                Status = ClaimStatus.Approved,
                TotalChargeAmount = 100m + i,
                ApprovedAmount = 80m + i,
                PatientResponsibility = 20m,
                ServiceDateFrom = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        }

        _claimsHandler.NextResponse = req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.StartsWith("/api/claims/search"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(claims)
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        foreach (var (npi, partnerId) in partnerNpis)
        {
            _tpClient.GetByBillingProviderNpiAsync("test-tenant", npi, "Production")
                .Returns(new TradingPartnerSummary
                {
                    TradingPartnerId = partnerId,
                    X12Config = new X12ConfigDto { SenderId = $"S{partnerId}", ReceiverId = $"R{partnerId}" }
                });
        }

        var result = await CreateService().ExecutePaymentRunAsync(run.Id);

        Assert.Equal(PaymentRunStatus.Completed, result.Status);
        Assert.Equal(50, result.TotalClaims);
        Assert.Equal(3, result.EraEnvelopeIds.Count);

        var envelopes = (await _envelopeRepo.GetByPaymentRunIdAsync(run.Id)).ToList();
        Assert.Equal(3, envelopes.Count);
        Assert.All(envelopes, e =>
        {
            Assert.StartsWith("ISA*", e.EdiContent);
            Assert.Contains("ST*835*0001*005010X221A1~", e.EdiContent);
            Assert.Contains("IEA*1*", e.EdiContent);
        });

        var totalClaimsInEnvelopes = envelopes.Sum(e => e.ClaimCount);
        Assert.Equal(50, totalClaimsInEnvelopes);

        var finalizeRequests = _claimsHandler.RecordedRequests
            .Where(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.Contains("/remittance"))
            .ToList();
        Assert.Equal(50, finalizeRequests.Count);
    }

    [Fact]
    public async Task EndToEnd_MixedDeniedAndPaid_EmitsCASForEditFailures()
    {
        var run = await _runRepo.CreateAsync(new PaymentRun
        {
            Id = "run-2",
            TenantId = "test-tenant",
            PaymentRunNumber = "PR-mixed",
            Status = PaymentRunStatus.Pending,
            Criteria = new PaymentRunCriteria { GroupByProvider = false },
            NextCheckNumber = 1000000,
            PaymentMethod = "ACH",
            PaymentDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc)
        });

        var paid = new ClaimDto
        {
            Id = "c-paid",
            ClaimNumber = "CLM-PAID",
            BillingProviderNPI = "NPI-A",
            MemberId = "m-paid",
            Status = ClaimStatus.Approved,
            TotalChargeAmount = 1000m,
            ApprovedAmount = 800m,
            PatientResponsibility = 200m,
            ServiceLines = new List<ClaimServiceLineDto>
            {
                new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 1000m, PaidAmount = 800m, Units = 1 }
            },
            AdjudicationResult = new ClaimAdjudicationDto
            {
                AdjustmentReasons = new List<ClaimAdjustmentReasonDto>
                {
                    new() { GroupCode = "PR", ReasonCode = "1", Amount = 200m, Description = "Deductible" }
                }
            }
        };

        var denied = new ClaimDto
        {
            Id = "c-denied",
            ClaimNumber = "CLM-DENIED",
            BillingProviderNPI = "NPI-A",
            MemberId = "m-denied",
            Status = ClaimStatus.Denied,
            TotalChargeAmount = 500m,
            ApprovedAmount = 0m,
            ServiceLines = new List<ClaimServiceLineDto>
            {
                new() { LineNumber = 1, ProcedureCode = "27447", ChargeAmount = 250m, PaidAmount = 0m, Units = 1 },
                new() { LineNumber = 2, ProcedureCode = "27486", ChargeAmount = 250m, PaidAmount = 0m, Units = 1 }
            },
            PendDetails = new PendDetailsDto
            {
                PendCode = "NCCI",
                EditFailures = new List<EditFailureDto>
                {
                    new()
                    {
                        EditType = "NCCI_PAIR",
                        RuleId = "NE001",
                        SuggestedCarc = "236",
                        SuggestedRarc = "M86",
                        AffectedLineNumbers = new List<int> { 2 },
                        Message = "Bundled procedure"
                    }
                }
            }
        };

        var claims = new List<ClaimDto> { paid, denied };

        _claimsHandler.NextResponse = req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.StartsWith("/api/claims/search"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(claims) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        _tpClient.GetByBillingProviderNpiAsync("test-tenant", "NPI-A", "Production")
            .Returns(new TradingPartnerSummary
            {
                TradingPartnerId = "TP-A",
                X12Config = new X12ConfigDto { SenderId = "STP-A", ReceiverId = "RTP-A" }
            });

        var result = await CreateService().ExecutePaymentRunAsync(run.Id);

        var envelope = Assert.Single(await _envelopeRepo.GetByPaymentRunIdAsync(run.Id));
        Assert.Contains("CAS*PR*1*200.00~", envelope.EdiContent); // header CAS for paid
        Assert.Contains("CAS*CO*236*0.00*M86~", envelope.EdiContent); // line CAS for denied (suggested CARC + RARC)
        Assert.Equal(2, envelope.ClaimCount);
    }

    // ── Test infrastructure ────────────────────────────────────────────

    private class InMemoryPaymentRepository : IPaymentRepository
    {
        private readonly List<Payment> _items = new();
        public Task<Payment?> GetByIdAsync(string id) => Task.FromResult<Payment?>(_items.FirstOrDefault(p => p.Id == id));
        public Task<Payment?> GetByCheckNumberAsync(string checkNumber) => Task.FromResult<Payment?>(_items.FirstOrDefault(p => p.CheckNumber == checkNumber));
        public Task<IEnumerable<Payment>> GetByClaimIdAsync(string claimId) => Task.FromResult<IEnumerable<Payment>>(_items.Where(p => p.ClaimPayments.Any(cp => cp.ClaimId == claimId)).ToList());
        public Task<IEnumerable<Payment>> SearchAsync(DateTime? paymentDateFrom, DateTime? paymentDateTo, string? payerId, PaymentStatus? status, int page = 1, int pageSize = 50) => Task.FromResult<IEnumerable<Payment>>(_items.ToList());
        public Task<PaymentsSummary> GetPaymentsSummaryAsync(DateTime from, DateTime to) => Task.FromResult(new PaymentsSummary());
        public Task<Payment> CreateAsync(Payment payment) { _items.Add(payment); return Task.FromResult(payment); }
        public Task<Payment> UpdateAsync(Payment payment) { _items.RemoveAll(p => p.Id == payment.Id); _items.Add(payment); return Task.FromResult(payment); }
        public Task DeleteAsync(string id) { _items.RemoveAll(p => p.Id == id); return Task.CompletedTask; }
    }

    private class InMemoryPaymentRunRepository : IPaymentRunRepository
    {
        private readonly List<PaymentRun> _items = new();
        public Task<PaymentRun?> GetByIdAsync(string id) => Task.FromResult<PaymentRun?>(_items.FirstOrDefault(r => r.Id == id));
        public Task<PaymentRun?> GetByPaymentRunNumberAsync(string paymentRunNumber) => Task.FromResult<PaymentRun?>(_items.FirstOrDefault(r => r.PaymentRunNumber == paymentRunNumber));
        public Task<IEnumerable<PaymentRun>> SearchAsync(DateTime from, DateTime to, PaymentRunStatus? status = null) => Task.FromResult<IEnumerable<PaymentRun>>(_items.ToList());
        public Task<PaymentRun> CreateAsync(PaymentRun run) { _items.RemoveAll(r => r.Id == run.Id); _items.Add(run); return Task.FromResult(run); }
        public Task<PaymentRun> UpdateAsync(PaymentRun run) { _items.RemoveAll(r => r.Id == run.Id); _items.Add(run); return Task.FromResult(run); }
        public Task DeleteAsync(string id) { _items.RemoveAll(r => r.Id == id); return Task.CompletedTask; }
    }
}
