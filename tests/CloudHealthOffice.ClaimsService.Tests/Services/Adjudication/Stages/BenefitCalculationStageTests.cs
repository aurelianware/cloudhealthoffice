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
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

public class BenefitCalculationStageTests
{
    private readonly IBenefitCalculationEngine _engine = Substitute.For<IBenefitCalculationEngine>();
    private readonly IMemberResolver _memberResolver = Substitute.For<IMemberResolver>();
    private readonly IAuthorizationValidationClient _authorizationValidationClient = Substitute.For<IAuthorizationValidationClient>();
    private readonly BenefitCalculationStage _sut;

    public BenefitCalculationStageTests()
    {
        _sut = new BenefitCalculationStage(
            _engine,
            _memberResolver,
            _authorizationValidationClient,
            NullLogger<BenefitCalculationStage>.Instance);
    }

    [Fact]
    public async Task Execute_HappyPath_PopulatesAdjudicationResult()
    {
        var planGuid = Guid.NewGuid();
        var claim = BuildClaim(planGuid.ToString());
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
        };

        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult
            {
                Success = true,
                Totals = new ClaimTotals
                {
                    TotalAllowed = 80m,
                    TotalDeductible = 10m,
                    TotalCoinsurance = 14m,
                    TotalCopay = 5m,
                    TotalMemberResponsibility = 29m,
                    TotalPlanPaid = 51m,
                },
                Lines = new List<LineBenefitResult>
                {
                    new()
                    {
                        LineNumber = 1, IsCovered = true, ServiceTypeCode = "1",
                        ServiceTypeDescription = "Office", AllowedAmount = 80m,
                        PlanPaidAmount = 51m, MemberResponsibility = 29m,
                    },
                },
            });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.True(result.Continue);
        Assert.Equal(80m, ctx.AdjudicationResult.AllowedAmount);
        Assert.Equal(29m, ctx.AdjudicationResult.PatientResponsibility);
        Assert.Equal(51m, ctx.AdjudicationResult.PayerPayment);
        Assert.Single(ctx.LineAdjudicationResults);
        Assert.Equal(80m, ctx.LineAdjudicationResults[0].AllowedAmount);
    }

    [Fact]
    public async Task Execute_HappyPath_ClearsStaleDenialReason()
    {
        var planGuid = Guid.NewGuid();
        var claim = BuildClaim(planGuid.ToString());
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
            AdjudicationResult = new AdjudicationResult
            {
                DenialReasonCode = "96",
                DenialReason = "Prior denied projection"
            }
        };

        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult
            {
                Success = true,
                Totals = new ClaimTotals
                {
                    TotalAllowed = 80m,
                    TotalMemberResponsibility = 29m,
                    TotalPlanPaid = 51m,
                },
                Lines = new List<LineBenefitResult>
                {
                    new()
                    {
                        LineNumber = 1,
                        IsCovered = true,
                        AllowedAmount = 80m,
                        PlanPaidAmount = 51m,
                        MemberResponsibility = 29m,
                    },
                },
            });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.AdjudicationResult.DenialReasonCode);
        Assert.Null(ctx.AdjudicationResult.DenialReason);
        Assert.Equal(80m, ctx.AdjudicationResult.AllowedAmount);
        Assert.Equal(51m, ctx.AdjudicationResult.PayerPayment);
    }

    [Fact]
    public async Task Execute_MissingBenefitPlanId_RejectsWithoutEngineCall()
    {
        var claim = BuildClaim(planId: null);
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
        };

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Reject, result.Outcome);
        Assert.False(result.Continue);
        await _engine.DidNotReceiveWithAnyArgs().CalculateAsync(default!, default);
    }

    [Fact]
    public async Task Execute_NonGuidBenefitPlanId_RejectsWhenNoResolvedGuid()
    {
        var claim = BuildClaim(planId: "legacy-plan-A");
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedPlan = new ResolvedBenefitPlan { Id = "legacy-plan-A", PlanGuid = null },
        };

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Reject, result.Outcome);
        Assert.Contains("not a GUID", result.Reason);
    }

    [Fact]
    public async Task Execute_NonGuidBenefitPlanId_UsesResolvedPlanGuidWhenAvailable()
    {
        var claim = BuildClaim(planId: "PLAN-FRIENDLY-NAME");
        var resolvedGuid = Guid.NewGuid();
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
            ResolvedPlan = new ResolvedBenefitPlan { Id = "PLAN-FRIENDLY-NAME", PlanGuid = resolvedGuid },
        };

        BenefitResolutionRequest? capturedRequest = null;
        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedRequest = ci.Arg<BenefitResolutionRequest>();
                return new BenefitResolutionResult { Success = true, Totals = new ClaimTotals() };
            });

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(resolvedGuid, capturedRequest!.BenefitPlanId);
    }

    [Fact]
    public async Task Execute_EngineThrows_RejectsWithoutPropagating()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
        };

        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("engine down"));

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Reject, result.Outcome);
        Assert.Contains("InvalidOperationException", result.Reason);
    }

    [Fact]
    public async Task Execute_EngineReturnsFailure_DeniesAndShortCircuits()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
        };

        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult
            {
                Success = false,
                DenialReasonCode = "96",
                DenialReasonDescription = "Non-covered service",
                Totals = new ClaimTotals(),
            });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.False(result.Continue);
        Assert.Equal("Non-covered service", result.Reason);
    }

    [Fact]
    public async Task Execute_ServiceDateAfterMemberTermination_DeniesWithCarc27WithoutEngineCall()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember
            {
                MemberId = "MEM-1",
                IsSubscriber = true,
                EffectiveDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                TerminationDate = new DateTime(2026, 4, 14, 0, 0, 0, DateTimeKind.Utc),
                EnrollmentStatus = "Active",
            },
        };

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.False(result.Continue);
        Assert.Equal(BenefitCalculationStage.MemberNotEligibleCarc, ctx.AdjudicationResult.DenialReasonCode);
        Assert.Equal("Service date after member coverage termination date", ctx.AdjudicationResult.DenialReason);
        await _engine.DidNotReceiveWithAnyArgs().CalculateAsync(default!, default);
        await _engine.DidNotReceiveWithAnyArgs().CalculateWithModeAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task Execute_ServiceDateOnOrAfterRetroactivePlanChange_PendsWithoutEngineCall()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember
            {
                MemberId = "MEM-1",
                IsSubscriber = true,
                PlanChangeEffectiveDate = claim.ServiceDateFrom.AddDays(-14),
            },
        };

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(result.Continue);
        Assert.NotNull(ctx.PendDetails);
        Assert.Equal(BenefitCalculationStage.RetroactivePlanChangePendCode, ctx.PendDetails!.PendCode);
        await _engine.DidNotReceiveWithAnyArgs().CalculateAsync(default!, default);
    }

    [Fact]
    public async Task Execute_RetroactivePlanChangeEffectiveAfterServiceDate_ContinuesToBenefitEngine()
    {
        var planGuid = Guid.NewGuid();
        var claim = BuildClaim(planGuid.ToString());
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember
            {
                MemberId = "MEM-1",
                IsSubscriber = true,
                PlanChangeEffectiveDate = claim.ServiceDateFrom.AddDays(14),
            },
        };

        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult { Success = true, Totals = new ClaimTotals(), Lines = new List<LineBenefitResult>() });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.PendDetails);
        await _engine.Received(1).CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("AA")]
    [InlineData("EM")]
    [InlineData("OA")]
    public async Task Execute_ClaimHasRelatedCausesCode_PendsWithoutEngineCall(string relatedCausesCode)
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        claim.RelatedCausesCode = relatedCausesCode;
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
        };

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(result.Continue);
        Assert.NotNull(ctx.PendDetails);
        Assert.Equal(BenefitCalculationStage.SubrogationReviewPendCode, ctx.PendDetails!.PendCode);
        await _engine.DidNotReceiveWithAnyArgs().CalculateAsync(default!, default);
    }

    [Fact]
    public async Task Execute_ClaimHasNoRelatedCausesCode_ContinuesToBenefitEngine()
    {
        var planGuid = Guid.NewGuid();
        var claim = BuildClaim(planGuid.ToString());
        claim.RelatedCausesCode = null;
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
        };

        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult { Success = true, Totals = new ClaimTotals(), Lines = new List<LineBenefitResult>() });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.PendDetails);
        await _engine.Received(1).CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_MedicaidSpendDownLiabilityNotYetMet_PendsWithoutEngineCall()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember
            {
                MemberId = "MEM-1",
                IsSubscriber = true,
                MedicaidSpendDownLiabilityAmount = 800m,
                MedicaidSpendDownAmountMet = 300m,
            },
        };

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(result.Continue);
        Assert.NotNull(ctx.PendDetails);
        Assert.Equal(BenefitCalculationStage.MedicaidSpendDownPendCode, ctx.PendDetails!.PendCode);
        await _engine.DidNotReceiveWithAnyArgs().CalculateAsync(default!, default);
    }

    [Fact]
    public async Task Execute_MedicaidSpendDownLiabilityMet_ContinuesToBenefitEngine()
    {
        var planGuid = Guid.NewGuid();
        var claim = BuildClaim(planGuid.ToString());
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember
            {
                MemberId = "MEM-1",
                IsSubscriber = true,
                MedicaidSpendDownLiabilityAmount = 800m,
                MedicaidSpendDownAmountMet = 800m,
            },
        };

        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult { Success = true, Totals = new ClaimTotals(), Lines = new List<LineBenefitResult>() });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.PendDetails);
        await _engine.Received(1).CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_MedicaidInstitutionalInpatientWithoutPriorAuth_DeniesWithoutEngineCall()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        claim.LineOfBusiness = LineOfBusiness.Medicaid;
        claim.ClaimType = ClaimType.Institutional;
        claim.PlaceOfServiceCode = "21";
        claim.PriorAuthorizationNumber = null;

        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
        };

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.False(result.Continue);
        Assert.Equal(BenefitCalculationStage.PriorAuthorizationRequiredCode, ctx.AdjudicationResult.DenialReasonCode);
        Assert.Equal(BenefitCalculationStage.PriorAuthorizationRequiredReason, ctx.AdjudicationResult.DenialReason);
        await _engine.DidNotReceiveWithAnyArgs().CalculateAsync(default!, default);
        await _authorizationValidationClient.DidNotReceiveWithAnyArgs()
            .ValidateAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task Execute_ExchangeInstitutionalInpatientWithoutPriorAuth_ContinuesToBenefitEngine()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        claim.LineOfBusiness = LineOfBusiness.Exchange;
        claim.ClaimType = ClaimType.Institutional;
        claim.PlaceOfServiceCode = "21";
        claim.PriorAuthorizationNumber = null;

        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
        };

        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult { Success = true, Totals = new ClaimTotals() });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        await _engine.Received(1).CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_InpatientWithPriorAuth_ContinuesToBenefitEngine()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        claim.LineOfBusiness = LineOfBusiness.Medicaid;
        claim.ClaimType = ClaimType.Institutional;
        claim.PlaceOfServiceCode = "21";
        claim.PriorAuthorizationNumber = "AUTH-123";
        claim.RenderingProviderNPI = "9876543210";

        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
        };

        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult { Success = true, Totals = new ClaimTotals() });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        await _authorizationValidationClient.Received(1).ValidateAsync(
            "tenant-1",
            "AUTH-123",
            "99213",
            claim.ServiceDateFrom,
            "9876543210",
            Arg.Any<CancellationToken>());
        await _engine.Received(1).CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_InpatientWithInvalidPriorAuth_DeniesWithoutEngineCall()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        claim.LineOfBusiness = LineOfBusiness.Medicaid;
        claim.ClaimType = ClaimType.Institutional;
        claim.PlaceOfServiceCode = "21";
        claim.PriorAuthorizationNumber = "AUTH-EXPIRED";

        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
        };

        _authorizationValidationClient.ValidateAsync(
                "tenant-1",
                "AUTH-EXPIRED",
                "99213",
                claim.ServiceDateFrom,
                "1234567890",
                Arg.Any<CancellationToken>())
            .Returns(new AuthorizationValidationResult(
                "AUTH-EXPIRED",
                false,
                "Approved",
                claim.ServiceDateFrom.AddDays(-30),
                claim.ServiceDateFrom.AddDays(-1),
                claim.ServiceDateFrom.AddDays(-1),
                1,
                "Authorization expired or not yet active"));

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.False(result.Continue);
        Assert.Equal(BenefitCalculationStage.PriorAuthorizationRequiredCode, ctx.AdjudicationResult.DenialReasonCode);
        Assert.Equal("Authorization expired or not yet active", ctx.AdjudicationResult.DenialReason);
        await _engine.DidNotReceiveWithAnyArgs().CalculateAsync(default!, default);
    }

    [Fact]
    public async Task Execute_InpatientWithPriorAuthLookupDegraded_ContinuesToBenefitEngine()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        claim.LineOfBusiness = LineOfBusiness.Medicaid;
        claim.ClaimType = ClaimType.Institutional;
        claim.PlaceOfServiceCode = "21";
        claim.PriorAuthorizationNumber = "AUTH-UNKNOWN";

        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
        };

        _authorizationValidationClient.ValidateAsync(
                "tenant-1",
                "AUTH-UNKNOWN",
                "99213",
                claim.ServiceDateFrom,
                "1234567890",
                Arg.Any<CancellationToken>())
            .Returns((AuthorizationValidationResult?)null);
        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult { Success = true, Totals = new ClaimTotals() });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        await _engine.Received(1).CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_NoSubscriberOnClaim_FallsBackThroughResolverAndMemberId()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        claim.SubscriberId = null;
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
        };
        _memberResolver.GetMemberAsync("tenant-1", "MEM-1", Arg.Any<CancellationToken>())
            .Returns(new ResolvedMember { MemberId = "MEM-1", SubscriberMemberId = "SUB-7" });

        BenefitResolutionRequest? captured = null;
        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.Arg<BenefitResolutionRequest>();
                return new BenefitResolutionResult { Success = true, Totals = new ClaimTotals() };
            });

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal("SUB-7", captured!.SubscriberId);
    }

    [Fact]
    public async Task Execute_PricedLines_FlowIntoEngineAsAllowedAmounts_NotBilled()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
        };

        BenefitResolutionRequest? captured = null;
        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.Arg<BenefitResolutionRequest>();
                return new BenefitResolutionResult { Success = true, Totals = new ClaimTotals() };
            });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.NotNull(captured);
        Assert.Equal(100m, captured!.Lines.Single().BilledAmount);
        Assert.Equal(72m, captured.AllowedAmounts[1]);
        Assert.Single(captured.AllowedAmounts);
    }

    [Fact]
    public async Task Execute_MultiUnitLine_SendsLineTotalAsBilled()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        claim.ClaimLines[0].ChargeAmount = 300m;
        claim.ClaimLines[0].Units = 3m;
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 210m),
        };

        BenefitResolutionRequest? captured = null;
        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.Arg<BenefitResolutionRequest>();
                return new BenefitResolutionResult { Success = true, Totals = new ClaimTotals() };
            });

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        var line = captured!.Lines.Single();
        Assert.Equal(300m, line.BilledAmount);
        Assert.Equal(3m, line.Units);
        Assert.Equal(210m, captured.AllowedAmounts[1]);
    }

    [Fact]
    public async Task Execute_PricingDidNotRun_PendsWithoutEngineCall()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
        };

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(BenefitCalculationStage.PricingRequiredPendCode, ctx.PendDetails!.PendCode);
        Assert.Equal(0m, ctx.AdjudicationResult.AllowedAmount);
        await _engine.DidNotReceive().CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_PricingIncomplete_PendsWithoutEngineCall_AndKeepsPricingPendDetails()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        var pricingPend = new PendDetails { PendCode = PricingStage.NoContractPendCode, PendReason = "line 1 unpriced" };
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PendDetails = pricingPend,
            PricingResult = new PricingOutcome
            {
                UnpricedLines = new[] { new UnpricedLine(1, "99213", "no rate") },
            },
        };

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Same(pricingPend, ctx.PendDetails);
        await _engine.DidNotReceive().CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_MembershipEvaluatedWithoutMatchedTier_SendsOutOfNetwork()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
        };
        // NetworkCredentialingStage ran (SoftValidation) but no tier matched.
        ctx.EnforcementOutcomes.Add(new EnforcementOutcome(
            EnforcementCheck.Membership, EnforcementDecision.Observe, "SoftValidation",
            "Billing provider is not an active member of any plan tier on the service date.",
            claim.ServiceDateFrom));

        BenefitResolutionRequest? captured = null;
        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.Arg<BenefitResolutionRequest>();
                return new BenefitResolutionResult { Success = true, Totals = new ClaimTotals() };
            });

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(NetworkTier.OutOfNetwork, captured!.NetworkTier);
        Assert.Equal("OutOfNetwork", ctx.AdjudicationResult.NetworkTier);
    }

    [Fact]
    public async Task Execute_MatchedNetworkTier_SendsInNetwork()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            MatchedNetworkTier = new ResolvedNetworkTier { TierName = "Tier 1", TierLevel = 1, NetworkId = "NET-1" },
        };
        ctx.EnforcementOutcomes.Add(new EnforcementOutcome(
            EnforcementCheck.Membership, EnforcementDecision.Allow, "FailClosed", null,
            claim.ServiceDateFrom, "NET-1", "Tier 1", 1));

        BenefitResolutionRequest? captured = null;
        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.Arg<BenefitResolutionRequest>();
                return new BenefitResolutionResult { Success = true, Totals = new ClaimTotals() };
            });

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(NetworkTier.InNetwork, captured!.NetworkTier);
        Assert.Equal("InNetwork", ctx.AdjudicationResult.NetworkTier);
    }

    [Fact]
    public void ResolveNetworkTier_NetworkStageDidNotRun_DefaultsInNetwork()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
        };

        Assert.Equal(NetworkTier.InNetwork, BenefitCalculationStage.ResolveNetworkTier(ctx));
    }

    // ── DRG / all-inclusive per diem: cost share once per stay ──────────

    [Fact]
    public void BuildRequest_PerStayPricedInpatientClaim_RoutesToClaimLevelInpatientPath()
    {
        var ctx = BuildDrgContext(Guid.NewGuid());

        var request = BenefitCalculationStage.BuildRequest(ctx, Guid.NewGuid(), "MEM-1");

        Assert.Equal("837I", request.ClaimType);
        Assert.Equal(InpatientPricingMethod.DrgCaseRate, request.InpatientPricingMethod);
        Assert.Equal("470", request.DrgCode);
        Assert.Equal(12000m, request.DrgAllowedAmount); // Σ line allowed
        Assert.Equal(4, request.LengthOfStay);
    }

    [Fact]
    public void BuildRequest_PerLinePricedClaim_StaysOnPerLinePath()
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
        };

        var request = BenefitCalculationStage.BuildRequest(ctx, Guid.NewGuid(), "MEM-1");

        Assert.Null(request.InpatientPricingMethod);
        Assert.Null(request.DrgAllowedAmount);
        Assert.Null(request.DrgCode);
    }

    /// <summary>
    /// End to end through the real benefit engine: a 3-line DRG claim whose
    /// case rate the pricing stage allocated across the lines gets one
    /// inpatient copay and the deductible once, accumulators are written
    /// once, and the claim lines reconcile to the claim result
    /// (Σ line paid = PayerPayment) so the 835 balances.
    /// </summary>
    [Fact]
    public async Task Execute_ThreeLineDrgClaim_OneCopay_DeductibleOnce_LinesReconcile()
    {
        var planGuid = Guid.NewGuid();
        var plan = new BenefitPlanConfig
        {
            Id = planGuid,
            TenantId = "tenant-1",
            PlanName = "Test PPO",
            PlanYear = "2026",
            IndividualDeductible = 500,
            FamilyDeductible = 1500,
            IndividualOopMax = 10000,
            FamilyOopMax = 20000,
            Categories =
            [
                new BenefitCategoryConfig
                {
                    ServiceTypeCode = "48",
                    ServiceTypeDescription = "Hospital - Inpatient",
                    IsCovered = true,
                    InNetworkCostSharing =
                    [
                        new CostShareRuleConfig { CostShareType = CostShareType.Deductible, DeductibleApplies = true },
                        new CostShareRuleConfig { CostShareType = CostShareType.Copay, CopayAmount = 250 },
                        new CostShareRuleConfig { CostShareType = CostShareType.Coinsurance, CoinsurancePercent = 0.20m },
                    ],
                },
            ],
        };

        var planProvider = Substitute.For<IBenefitPlanProvider>();
        planProvider.GetPlanAsync(planGuid, Arg.Any<CancellationToken>()).Returns(plan);
        var resolver = Substitute.For<IServiceCategoryResolver>();
        resolver.ResolveAsync(default!, default, default, default!, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(new ServiceCategoryMatch
            {
                ServiceTypeCode = "48", ServiceTypeDescription = "Hospital - Inpatient",
                MatchedBy = "Test", MatchedRule = "Fixed:48",
            });
        var accumulators = Substitute.For<IAccumulatorService>();
        accumulators.GetAccumulatorsAsync(default!, default!, default, default!, default)
            .ReturnsForAnyArgs(new List<AccumulatorSnapshot>
            {
                Snapshot(AccumulatorType.IndividualDeductible, AccumulatorScope.Individual, 500),
                Snapshot(AccumulatorType.FamilyDeductible, AccumulatorScope.Family, 1500),
                Snapshot(AccumulatorType.IndividualOutOfPocketMax, AccumulatorScope.Individual, 10000),
                Snapshot(AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family, 20000),
            });
        var engine = new BenefitCalculationEngine(
            resolver, planProvider, accumulators,
            new BenefitRuleGate(NullLogger<BenefitRuleGate>.Instance),
            NullLogger<BenefitCalculationEngine>.Instance);
        var sut = new BenefitCalculationStage(
            engine, _memberResolver, _authorizationValidationClient,
            NullLogger<BenefitCalculationStage>.Instance);
        var ctx = BuildDrgContext(planGuid);

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        var claimResult = ctx.AdjudicationResult;
        Assert.Equal(12000m, claimResult.AllowedAmount);
        Assert.Equal(500m, claimResult.DeductibleAmount);   // once
        Assert.Equal(250m, claimResult.CopayAmount);        // one copay per stay, not 3 × $250
        Assert.Equal(2250m, claimResult.CoinsuranceAmount); // 20% of 12000 − 750
        Assert.Equal(3000m, claimResult.PatientResponsibility);
        Assert.Equal(9000m, claimResult.PayerPayment);

        var lines = ctx.LineAdjudicationResults;
        Assert.Equal(3, lines.Count);
        Assert.Equal(claimResult.PayerPayment, lines.Sum(l => l.PaidAmount));
        Assert.Equal(claimResult.PatientResponsibility, lines.Sum(l => l.PatientResponsibility));
        Assert.Equal(claimResult.AllowedAmount, lines.Sum(l => l.AllowedAmount));
        Assert.All(lines, l => Assert.Equal(l.AllowedAmount - l.PatientResponsibility, l.PaidAmount));

        await accumulators.ReceivedWithAnyArgs(1).ApplyUpdatesAsync(
            default!, default!, default, default!, default!, default!, default);
    }

    private static AccumulatorSnapshot Snapshot(AccumulatorType type, AccumulatorScope scope, decimal limit) => new()
    {
        Type = type,
        Scope = scope,
        NetworkTier = NetworkTier.InNetwork,
        LimitAmount = limit,
        RemainingAmount = limit,
    };

    /// <summary>
    /// Inpatient DRG claim as PricingStage leaves it: DRG 470 case rate
    /// $12,000 allocated across three lines by billed charges ($42,500).
    /// </summary>
    private static ClaimAdjudicationContext BuildDrgContext(Guid planGuid)
    {
        var admit = new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
        var claim = BuildClaim(planGuid.ToString());
        claim.ClaimType = ClaimType.Institutional;
        claim.ServiceDateFrom = admit;
        claim.ServiceDateTo = admit.AddDays(4);
        claim.PriorAuthorizationNumber = "AUTH-1";
        claim.Institutional = new InstitutionalClaimDetails
        {
            FacilityTypeCode = "11",
            AdmissionDate = admit,
            StatementFromDate = admit,
            StatementToDate = admit.AddDays(4),
            PatientStatusCode = "01",
            DrgCode = "470",
        };
        claim.ClaimLines = new List<AdapterClaimLine>
        {
            new() { LineNumber = 1, RevenueCode = "0120", ChargeAmount = 12000m, Units = 4, ServiceDateFrom = admit, ServiceDateTo = admit.AddDays(4) },
            new() { LineNumber = 2, RevenueCode = "0250", ChargeAmount = 1500m, Units = 10, ServiceDateFrom = admit, ServiceDateTo = admit.AddDays(4) },
            new() { LineNumber = 3, RevenueCode = "0360", ProcedureCode = "27447", ChargeAmount = 29000m, Units = 1, ServiceDateFrom = admit, ServiceDateTo = admit },
        };

        var allowed = new Dictionary<int, decimal> { [1] = 3388.23m, [2] = 423.52m, [3] = 8188.25m };
        return new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = new PricingOutcome
            {
                AllowedAmounts = allowed,
                RawResult = new CloudHealthOffice.FeeScheduleEngine.Models.PricingResultSet
                {
                    LineResults = allowed.Select(kv => new CloudHealthOffice.FeeScheduleEngine.Models.PricingResult
                    {
                        LineNumber = kv.Key,
                        AllowedAmount = kv.Value,
                        BilledAmount = claim.ClaimLines[kv.Key - 1].ChargeAmount,
                        RateSource = CloudHealthOffice.FeeScheduleEngine.Domain.RateSource.Drg,
                        FeeScheduleType = CloudHealthOffice.FeeScheduleEngine.Domain.FeeScheduleType.Drg,
                        FeeScheduleId = "DRG-1",
                        IsPerStayRate = true,
                    }).ToList(),
                },
            },
        };
    }

    private static PricingOutcome PricedAt(AdapterClaim claim, decimal allowedPerLine) => new()
    {
        AllowedAmounts = claim.ClaimLines.ToDictionary(l => l.LineNumber, _ => allowedPerLine),
    };

    private static AdapterClaim BuildClaim(string? planId)
    {
        var serviceDate = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        return new AdapterClaim
        {
            TenantId = "tenant-1",
            Id = "claim-1",
            ClaimVersionId = "claim-1",
            ClaimNumber = "CLM-1",
            MemberId = "MEM-1",
            SubscriberId = "MEM-1",
            BenefitPlanId = planId,
            BillingProviderNPI = "1234567890",
            LineOfBusiness = LineOfBusiness.Commercial,
            ClaimType = ClaimType.Professional,
            PlaceOfServiceCode = "11",
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            ClaimLines = new List<AdapterClaimLine>
            {
                new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 100m, Units = 1,
                        ServiceDateFrom = serviceDate, ServiceDateTo = serviceDate },
            },
        };
    }
}
