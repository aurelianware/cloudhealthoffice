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

    [Fact]
    public async Task ExplicitAuthorizationLines_AreIndependentOfClaimOrdinalsAndLookupCache()
    {
        var (stage, handler) = StageOver(req =>
        {
            var authLine = Query(req, "lineNumber");
            return ValidationBody("AUTH-1", authLine == "7", authLine == "7" ? "Authorization valid" : "Procedure not approved");
        });
        var ctx = InpatientMedicaid("AUTH-1", "72148", "72148");
        ctx.Claim.ClaimLines[0].PriorAuthorizationLineNumber = 7;
        ctx.Claim.ClaimLines[1].PriorAuthorizationLineNumber = 12;

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.Equal(2, handler.RequestCount);
        await _engine.DidNotReceiveWithAnyArgs().CalculateAsync(default!, default);
    }

    [Fact]
    public async Task UnselectedRepeatedProcedure_PendsInsteadOfDenyingAndDoesNotInferClaimOrdinal()
    {
        string? sentAuthorizationLine = "not-called";
        var (stage, _) = StageOver(req =>
        {
            sentAuthorizationLine = Query(req, "lineNumber");
            return ValidationBody("AUTH-1", false, "Procedure occurs on multiple lines; specify lineNumber");
        });
        var ctx = InpatientMedicaid("AUTH-1", "72148");
        ctx.Claim.ClaimLines[0].LineNumber = 7;
        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);
        AssertPendedPaUnavailable(result, ctx);
        Assert.Null(sentAuthorizationLine);
    }

    [Fact]
    public async Task ExplicitApprovedAuthorizationLine_PassesWithClaimNumberFallback()
    {
        var (stage, _) = StageOver(req =>
        {
            Assert.Equal("12", Query(req, "lineNumber"));
            Assert.Contains("/AUTH-1/validate", req.RequestUri!.AbsolutePath);
            return ValidationBody("AUTH-1", true, "Authorization valid");
        });
        var ctx = InpatientMedicaid("AUTH-1", "72148");
        ctx.Claim.ClaimLines[0].PriorAuthorizationLineNumber = 12;
        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);
        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        await _engine.Received(1).CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ExplicitAuthorizationLine_SurvivesAdapterRoundTrip()
    {
        var line = new ClaimLine { LineNumber = 2, PriorAuthorizationNumber = "AUTH-1", PriorAuthorizationLineNumber = 12 };
        var roundTrip = AdapterClaimLine.From(line).ToClaimLine();
        Assert.Equal(12, roundTrip.PriorAuthorizationLineNumber);
        Assert.Equal(2, roundTrip.LineNumber);
        Assert.Equal("AUTH-1", roundTrip.PriorAuthorizationNumber);
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

    // ── Line-level REF*G1 overrides the claim's (D-05) ────────────────

    [Fact]
    public async Task LineLevelNumber_OverridesClaimLevelForThatLine()
    {
        var (stage, handler) = StageOver(req =>
            ValidationBody(Uri.UnescapeDataString(req.RequestUri!.AbsolutePath.Split('/')[3]), isValid: true, "Authorization valid"));
        var ctx = InpatientMedicaid("AUTH-CLAIM", "99223", "27447");
        ctx.Claim.ClaimLines[1].PriorAuthorizationNumber = "AUTH-LINE";

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(
            new[] { "/api/authorizations/AUTH-CLAIM/validate", "/api/authorizations/AUTH-LINE/validate" },
            handler.Requests.Select(r => r.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task LineWithoutAnyNumber_DeniesEvenWhenAnotherLineHasOne()
    {
        var (stage, _) = StageOver(req => ValidationBody("AUTH-LINE", isValid: true, "Authorization valid"));
        var ctx = InpatientMedicaid(null, "99223", "27447");
        ctx.Claim.ClaimLines[1].PriorAuthorizationNumber = "AUTH-LINE";

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.Equal(BenefitCalculationStage.PriorAuthorizationRequiredReason, ctx.AdjudicationResult.DenialReason);
    }

    // ── Which lines require PA is configurable per plan (G-25) ───────

    [Fact]
    public async Task PlanFlagsProcedure_ChecksOnlyThatLine_OnAnyClaimType()
    {
        var (stage, handler) = StageOver(req => ValidationBody("AUTH-1", isValid: true, "Authorization valid"), usePlanFlags: true);
        var ctx = CommercialProfessional("AUTH-1", "72148", "99213");
        ctx.ResolvedPlan = PlanRequiringPriorAuthFor("72148");

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("72148", Query(request, "procedureCode"));
    }

    [Fact]
    public async Task PlanFlagsProcedure_NoNumber_DeniesWithCarc197()
    {
        var (stage, handler) = StageOver(_ => throw new InvalidOperationException("must not be called"), usePlanFlags: true);
        var ctx = CommercialProfessional(null, "99213", "72148");
        ctx.ResolvedPlan = PlanRequiringPriorAuthFor("72148");

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.Equal(BenefitCalculationStage.PriorAuthorizationRequiredCode, ctx.AdjudicationResult.DenialReasonCode);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task PlanFlagsProcedure_LookupUnavailable_Pends()
    {
        var (stage, _) = StageOver(_ => new HttpResponseMessage(HttpStatusCode.BadGateway), usePlanFlags: true);
        var ctx = CommercialProfessional("AUTH-1", "72148");
        ctx.ResolvedPlan = PlanRequiringPriorAuthFor("72148");

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        AssertPendedPaUnavailable(result, ctx);
    }

    [Fact]
    public async Task PlanWithoutPriorAuthConfiguration_KeepsDefaultScope_NoLookupOutsideInpatientMedicaid()
    {
        var (stage, handler) = StageOver(_ => throw new InvalidOperationException("must not be called"));
        var ctx = CommercialProfessional(null, "72148");
        ctx.ResolvedPlan = new ResolvedBenefitPlan { Id = "plan-1", PlanGuid = Guid.NewGuid() };

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(0, handler.RequestCount);
    }

    // ── #1299 S3: plan-flagged codes are off by default ───────────────

    /// <summary>
    /// Until line-level decisions (G-07) land, <c>/validate</c> with a code is
    /// Invalid for API-decided auths, so plan flags would deny approved claims.
    /// Off by default: a plan flag alone adds no check.
    /// </summary>
    [Fact]
    public async Task PlanFlaggedCodes_OffByDefault_AddNoCheck()
    {
        var (stage, handler) = StageOver(_ => throw new InvalidOperationException("must not be called"));
        var ctx = CommercialProfessional(null, "72148");
        ctx.ResolvedPlan = PlanRequiringPriorAuthFor("72148");

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(0, handler.RequestCount);
    }

    // ── #1299 S2: benefit plan unavailable ────────────────────────────

    /// <summary>
    /// Plan flags in use, plan unavailable, claim outside the floor: which
    /// lines need PA is unknown, so the claim pends PLANUNAVAIL (owner
    /// decision 2026-10-11) — never priced and paid without the check.
    /// </summary>
    [Fact]
    public async Task PlanUnavailable_PlanFlagsOn_OutsideTheFloor_PendsPlanUnavailable()
    {
        var (stage, handler) = StageOver(_ => throw new InvalidOperationException("must not be called"), usePlanFlags: true);
        var ctx = CommercialProfessional("AUTH-1", "72148");
        ctx.PlanResolutionFailed = true;

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(BenefitCalculationStage.PlanUnavailablePendCode, ctx.PendDetails!.PendCode);
        Assert.StartsWith(BenefitCalculationStage.PlanUnavailableReason, ctx.PendDetails.PendReason);
        Assert.True(ctx.IsCheckUnavailable(ctx.PendDetails.PendReason));
        Assert.Null(ctx.BenefitResolutionResult);
        Assert.Equal(0, handler.RequestCount);
        await _engine.DidNotReceiveWithAnyArgs().CalculateAsync(default!, default);
    }

    /// <summary>Plan flags not in use: the floor alone settles the scope, so a plan outage creates no PA pend.</summary>
    [Fact]
    public async Task PlanUnavailable_PlanFlagsOff_NoPlanPend()
    {
        var (stage, handler) = StageOver(_ => throw new InvalidOperationException("must not be called"));
        var ctx = CommercialProfessional("AUTH-1", "72148");
        ctx.PlanResolutionFailed = true;

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.PendDetails);
        Assert.Equal(0, handler.RequestCount);
    }

    /// <summary>A floor claim already has every line in scope: a plan outage changes nothing about the PA check.</summary>
    [Fact]
    public async Task PlanUnavailable_FloorClaim_ChecksEveryLineAsUsual()
    {
        var (stage, handler) = StageOver(_ => ValidationBody("AUTH-1", isValid: true, "Authorization valid"), usePlanFlags: true);
        var ctx = InpatientMedicaid("AUTH-1", "99223");
        ctx.PlanResolutionFailed = true;

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(1, handler.RequestCount);
    }

    // ── #1299 S1 / nit 5 ──────────────────────────────────────────────

    /// <summary>The pend text says how the claim is cleared; it promises no automatic re-run.</summary>
    [Fact]
    public async Task PaUnavailablePendReason_NamesHowItIsCleared()
    {
        var (stage, _) = StageOver(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var ctx = InpatientMedicaid("AUTH-1");

        await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.DoesNotContain("re-adjudicated once", ctx.PendDetails!.PendReason);
        Assert.Contains("examiner approval or the PAUNAVAIL re-run", ctx.PendDetails.PendReason);
    }

    /// <summary>Nit 5: once one line is Unavailable the claim pends; no further lookups.</summary>
    [Fact]
    public async Task FirstUnavailableLine_StopsFurtherLookups()
    {
        var (stage, handler) = StageOver(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var ctx = InpatientMedicaid("AUTH-1", "99223", "27447", "71045");

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        AssertPendedPaUnavailable(result, ctx);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task PlanConfiguration_NeverStopsTheDefaultInpatientMedicaidCheck()
    {
        var (stage, handler) = StageOver(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var ctx = InpatientMedicaid("AUTH-1", "99223");
        ctx.ResolvedPlan = PlanRequiringPriorAuthFor("72148");

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        AssertPendedPaUnavailable(result, ctx);
        Assert.Equal(1, handler.RequestCount);
    }

    /// <summary>
    /// D-05 end to end: an 837P whose line carries its own REF*G1 reaches
    /// authorization-service with that number for that line.
    /// </summary>
    [Fact]
    public async Task Edi837P_LineLevelRefG1_IsTheNumberValidatedForThatLine()
    {
        const string edi =
            "ISA*00*          *00*          *ZZ*SUBMITTER01    *ZZ*CHOPAYER       *260301*1200*^*00501*000000203*0*T*:~" +
            "GS*HC*SUBMITTER01*CHOPAYER*20260301*1200*203*X*005010X222A1~" +
            "ST*837*0203*005010X222A1~" +
            "BHT*0019*00*PA-PROF-0003*20260301*1200*CH~" +
            "NM1*41*2*SYNTHETIC CLINIC*****46*SUBMITTER01~" +
            "NM1*40*2*CHO TEST PAYER*****46*CHOPAYER~" +
            "HL*1**20*1~" +
            "NM1*85*2*SYNTHETIC CLINIC*****XX*1234567890~" +
            "HL*2*1*22*0~" +
            "SBR*P*18*GRP-0003******CI~" +
            "NM1*IL*1*TESTPATIENT*SAM****MI*MEM-1~" +
            "NM1*PR*2*CHO TEST PAYER*****PI*PAYER01~" +
            "CLM*PA-PROF-0003*1250.00***11:B:1*Y*A*Y*Y~" +
            "REF*G1*AUTH-CLAIM-0003~" +
            "HI*ABK:M5416~" +
            "LX*1~" +
            "SV1*HC:72148*1000.00*UN*1*11**1~" +
            "DTP*472*D8*20260305~" +
            "REF*G1*AUTH-LINE-0003~" +
            "LX*2~" +
            "SV1*HC:99214*250.00*UN*1*11**1~" +
            "DTP*472*D8*20260305~" +
            "SE*21*0203~" +
            "GE*1*203~" +
            "IEA*1*000000203~";
        var claim = global::ClaimsService.EDI.Inbound.X12837ClaimMapper.Map(
            Assert.Single(global::ClaimsService.EDI.Inbound.X12837Parser.Parse(edi)), Tenant);
        claim.Id = "claim-edi";
        claim.ClaimVersionId = "claim-edi";
        claim.BenefitPlanId = Guid.NewGuid().ToString();
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = Tenant,
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = new PricingOutcome
            {
                AllowedAmounts = claim.ClaimLines.ToDictionary(l => l.LineNumber, _ => 72m),
            },
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
            ResolvedPlan = PlanRequiringPriorAuthFor("72148"),
        };
        var (stage, handler) = StageOver(req =>
            ValidationBody(Uri.UnescapeDataString(req.RequestUri!.AbsolutePath.Split('/')[3]), isValid: true, "Authorization valid"),
            usePlanFlags: true);

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/authorizations/AUTH-LINE-0003/validate", request.RequestUri!.AbsolutePath);
        Assert.Equal("72148", Query(request, "procedureCode"));
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static ResolvedBenefitPlan PlanRequiringPriorAuthFor(params string[] codes) => new()
    {
        Id = "plan-1",
        PlanGuid = Guid.NewGuid(),
        PriorAuthRequiredProcedureCodes = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase),
    };

    private static ClaimAdjudicationContext CommercialProfessional(string? authNumber, params string[] procedureCodes)
    {
        var ctx = InpatientMedicaid(authNumber, procedureCodes);
        ctx.Claim.LineOfBusiness = LineOfBusiness.Commercial;
        ctx.Claim.ClaimType = ClaimType.Professional;
        ctx.Claim.PlaceOfServiceCode = "11";
        return ctx;
    }

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
        Func<HttpRequestMessage, HttpResponseMessage> responder, bool usePlanFlags = false)
    {
        var handler = new RecordingHandler(responder);
        var client = new HttpAuthorizationValidationClient(
            new BaseAddressFactory(handler),
            NullLogger<HttpAuthorizationValidationClient>.Instance);
        var stage = new BenefitCalculationStage(
            _engine,
            Substitute.For<IMemberResolver>(),
            client,
            NullLogger<BenefitCalculationStage>.Instance,
            Microsoft.Extensions.Options.Options.Create(new PriorAuthAdjudicationOptions { UsePlanFlaggedCodes = usePlanFlags }));
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
