using System.Net;
using System.Text;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// D-04 / G-12 and the every-line part of G-25 (docs/epa-gap-matrix.md).
/// The stage runs against the real <see cref="HttpAuthorizationValidationClient"/>
/// over a scripted authorization-service, so each case is the wire outcome
/// a claim sees. Owner decision Q-9: pend the claim when the lookup is
/// unavailable; never pay or deny on a failed lookup.
/// </summary>
public sealed class BenefitCalculationStagePriorAuthLookupTests
{
    private const string Tenant = "tenant-1";
    private const string PaUnavailablePendCode = BenefitCalculationStage.PriorAuthorizationUnavailablePendCode;

    private static readonly AccumulatorCommit Prepared = new()
    {
        CommitId = "commit-test", ClaimId = "claim", MemberId = "MEM-1", SubscriberId = "MEM-1",
        BenefitPlanId = Guid.Empty, PlanYear = "2026",
    };

    private readonly IBenefitCalculationEngine _engine = Substitute.For<IBenefitCalculationEngine>();

    public BenefitCalculationStagePriorAuthLookupTests()
    {
        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult { PreparedAccumulatorCommit = Prepared, Success = true, Totals = new ClaimTotals() });
    }

    // ── The auth number does not exist (definitive 404) ───────────────

    [Fact]
    public async Task AuthNumberNotFound_DeniesWithCarc197_NeverReachesEngine()
    {
        var (stage, _) = StageOver(req => NotFoundFromService(req));
        var ctx = InpatientMedicaid("AUTH-DOES-NOT-EXIST");

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.Equal(BenefitCalculationStage.PriorAuthorizationRequiredCode, ctx.AdjudicationResult.DenialReasonCode);
        Assert.Null(ctx.PendDetails);
        await _engine.DidNotReceiveWithAnyArgs().CalculateAsync(default!, default);
    }

    // ── The lookup failed: pend, never pay, never deny ────────────────

    public static TheoryData<string> UnavailableResponses() => new()
    {
        "500", "502", "503", "504", "401", "403", "429", "400",
        "gateway-404", "malformed-json", "null-body",
    };

    [Theory]
    [MemberData(nameof(UnavailableResponses))]
    public async Task LookupNotDefinitive_PendsPaUnavailable_NeverPaysOrDenies(string response)
    {
        var (stage, _) = StageOver(_ => response switch
        {
            "gateway-404" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "malformed-json" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ not json", Encoding.UTF8, "application/json"),
            },
            "null-body" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("null", Encoding.UTF8, "application/json"),
            },
            _ => new HttpResponseMessage((HttpStatusCode)int.Parse(response)),
        });
        var ctx = InpatientMedicaid("AUTH-123");

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        AssertPendedPaUnavailable(result, ctx);
    }

    [Fact]
    public async Task LookupTimesOut_PendsPaUnavailable()
    {
        var (stage, _) = StageOver(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        var ctx = InpatientMedicaid("AUTH-123");

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        AssertPendedPaUnavailable(result, ctx);
    }

    [Fact]
    public async Task LookupTransportError_PendsPaUnavailable()
    {
        var (stage, _) = StageOver(_ => throw new HttpRequestException("Connection refused"));
        var ctx = InpatientMedicaid("AUTH-123");

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        AssertPendedPaUnavailable(result, ctx);
    }

    [Fact]
    public async Task LookupThrowsUnexpectedly_PendsPaUnavailable()
    {
        var (stage, _) = StageOver(_ => throw new InvalidOperationException("handler misconfigured"));
        var ctx = InpatientMedicaid("AUTH-123");

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        AssertPendedPaUnavailable(result, ctx);
    }

    // ── Required, but no number was given ─────────────────────────────

    [Fact]
    public async Task AuthRequiredButNoNumber_DeniesWithCarc197_WithoutLookup()
    {
        var (stage, handler) = StageOver(_ => throw new InvalidOperationException("must not be called"));
        var ctx = InpatientMedicaid(null);

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.Equal(BenefitCalculationStage.PriorAuthorizationRequiredCode, ctx.AdjudicationResult.DenialReasonCode);
        Assert.Equal(BenefitCalculationStage.PriorAuthorizationRequiredReason, ctx.AdjudicationResult.DenialReason);
        Assert.Equal(0, handler.RequestCount);
        await _engine.DidNotReceiveWithAnyArgs().CalculateAsync(default!, default);
    }

    // ── Every line that requires PA is checked (G-25) ─────────────────

    [Fact]
    public async Task SecondLineNotCoveredByTheAuth_Denies()
    {
        var (stage, handler) = StageOver(req => Query(req, "procedureCode") == "27447"
            ? ValidationBody("AUTH-123", isValid: false, "Procedure 27447 not approved")
            : ValidationBody("AUTH-123", isValid: true, "Authorization valid"));
        var ctx = InpatientMedicaid("AUTH-123", "99223", "27447");

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.Equal(BenefitCalculationStage.PriorAuthorizationRequiredCode, ctx.AdjudicationResult.DenialReasonCode);
        Assert.Equal("Procedure 27447 not approved", ctx.AdjudicationResult.DenialReason);
        Assert.Equal(2, handler.RequestCount);
        await _engine.DidNotReceiveWithAnyArgs().CalculateAsync(default!, default);
    }

    [Fact]
    public async Task SecondLineLookupUnavailable_Pends()
    {
        var (stage, _) = StageOver(req => Query(req, "procedureCode") == "27447"
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : ValidationBody("AUTH-123", isValid: true, "Authorization valid"));
        var ctx = InpatientMedicaid("AUTH-123", "99223", "27447");

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        AssertPendedPaUnavailable(result, ctx);
    }

    [Fact]
    public async Task OneLineDefinitivelyInvalid_AnotherUnavailable_PendsRatherThanDenies()
    {
        // A failed lookup on any line means this run cannot decide the claim:
        // the definitive denial is re-derived on the re-run.
        var (stage, _) = StageOver(req => Query(req, "procedureCode") == "27447"
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : ValidationBody("AUTH-123", isValid: false, "Authorization not approved"));
        var ctx = InpatientMedicaid("AUTH-123", "99223", "27447");

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        AssertPendedPaUnavailable(result, ctx);
    }

    [Fact]
    public async Task EveryLineValid_PassesToEngine()
    {
        var (stage, handler) = StageOver(req => ValidationBody("AUTH-123", isValid: true, "Authorization valid"));
        var ctx = InpatientMedicaid("AUTH-123", "99223", "27447");

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(2, handler.RequestCount);
        Assert.All(handler.Requests, r => Assert.Equal(Tenant, r.Headers.GetValues("X-Tenant-ID").Single()));
        await _engine.Received(1).CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>());
    }

    // ── helpers ───────────────────────────────────────────────────────

    private void AssertPendedPaUnavailable(ClaimAdjudicationStageResult result, ClaimAdjudicationContext ctx)
    {
        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.NotNull(ctx.PendDetails);
        Assert.Equal(PaUnavailablePendCode, ctx.PendDetails!.PendCode);
        Assert.Contains("PA lookup unavailable", ctx.PendDetails.PendReason);
        Assert.Null(ctx.AdjudicationResult.DenialReasonCode);
        Assert.Null(ctx.BenefitResolutionResult);
        _engine.DidNotReceiveWithAnyArgs().CalculateAsync(default!, default);
    }

    private (BenefitCalculationStage Stage, RecordingHandler Handler) StageOver(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new RecordingHandler(responder);
        var client = new HttpAuthorizationValidationClient(
            new BaseAddressFactory(handler),
            NullLogger<HttpAuthorizationValidationClient>.Instance);
        var stage = new BenefitCalculationStage(
            _engine,
            Substitute.For<IMemberResolver>(),
            client,
            NullLogger<BenefitCalculationStage>.Instance);
        return (stage, handler);
    }

    /// <summary>What authorization-service's <c>/validate</c> returns for an unknown number.</summary>
    private static HttpResponseMessage NotFoundFromService(HttpRequestMessage request)
    {
        var number = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.Split('/')[3]);
        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"Authorization {number} not found", Encoding.UTF8, "text/plain"),
        };
    }

    private static HttpResponseMessage ValidationBody(string number, bool isValid, string message) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {
                  "authorizationNumber": "{{number}}",
                  "isValid": {{(isValid ? "true" : "false")}},
                  "status": "{{(isValid ? "Approved" : "Denied")}}",
                  "validationMessage": "{{message}}"
                }
                """,
                Encoding.UTF8,
                "application/json"),
        };

    private static string? Query(HttpRequestMessage request, string name) =>
        request.RequestUri!.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .Where(p => p[0] == name)
            .Select(p => Uri.UnescapeDataString(p[1]))
            .FirstOrDefault();

    private static ClaimAdjudicationContext InpatientMedicaid(string? authNumber, params string[] procedureCodes)
    {
        if (procedureCodes.Length == 0) procedureCodes = ["99223"];
        var serviceDate = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        var claim = new AdapterClaim
        {
            TenantId = Tenant,
            Id = "claim-1",
            ClaimVersionId = "claim-1",
            ClaimNumber = "CLM-1",
            MemberId = "MEM-1",
            SubscriberId = "MEM-1",
            BenefitPlanId = Guid.NewGuid().ToString(),
            BillingProviderNPI = "1234567890",
            LineOfBusiness = LineOfBusiness.Medicaid,
            ClaimType = ClaimType.Institutional,
            PlaceOfServiceCode = "21",
            PriorAuthorizationNumber = authNumber,
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            ClaimLines = procedureCodes
                .Select((code, i) => new AdapterClaimLine
                {
                    LineNumber = i + 1,
                    ProcedureCode = code,
                    ChargeAmount = 100m,
                    Units = 1,
                    ServiceDateFrom = serviceDate,
                    ServiceDateTo = serviceDate,
                })
                .ToList(),
        };
        return new ClaimAdjudicationContext
        {
            TenantId = Tenant,
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = new PricingOutcome
            {
                AllowedAmounts = claim.ClaimLines.ToDictionary(l => l.LineNumber, _ => 72m),
            },
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
        };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<HttpRequestMessage> Requests { get; } = new();
        public int RequestCount => Requests.Count;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class BaseAddressFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://authorization-service.test") };
    }
}
