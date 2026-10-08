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

    [Fact]
    public void ApplyToContext_MapsEngineLineAdjustmentsOntoLineCas()
    {
        var ctx = ContextFor(BuildClaim(Guid.NewGuid().ToString()));

        BenefitCalculationStage.ApplyToContext(ctx, SingleLineResult(new LineBenefitResult
        {
            LineNumber = 1, IsCovered = true, BilledAmount = 100m, AllowedAmount = 80m,
            ContractualAdjustment = 20m, DeductibleAmount = 10m, CopayAmount = 5m, CoinsuranceAmount = 13m,
            MemberResponsibility = 28m, PlanPaidAmount = 52m,
            Adjustments =
            [
                new AdjustmentReason { GroupCode = "CO", ReasonCode = "45", Amount = 20m },
                new AdjustmentReason { GroupCode = "PR", ReasonCode = "1", Amount = 10m },
                new AdjustmentReason { GroupCode = "PR", ReasonCode = "3", Amount = 5m },
                new AdjustmentReason { GroupCode = "PR", ReasonCode = "2", Amount = 13m, RemarkCode = "N130" },
            ],
        }));

        var cas = Assert.Single(ctx.LineAdjudicationResults).AdjustmentReasons;
        Assert.Equal(
            new[] { ("CO", "45", 20m), ("PR", "1", 10m), ("PR", "3", 5m), ("PR", "2", 13m) },
            cas.Select(r => (r.GroupCode, r.ReasonCode, r.Amount)));
        Assert.Equal("N130", cas[3].RemarkCode);
        Assert.Equal(52m, 100m - cas.Sum(r => r.Amount));
    }

    [Fact]
    public void ApplyToContext_DrgLineWithoutAdjustments_SynthesizesBalancingCas()
    {
        var ctx = ContextFor(BuildClaim(Guid.NewGuid().ToString()));

        // Allocated DRG share: raw cost share 60 capped to member 50 by the
        // OOP max (allocation rounding lands in the same OA-23 entry).
        BenefitCalculationStage.ApplyToContext(ctx, SingleLineResult(new LineBenefitResult
        {
            LineNumber = 1, IsCovered = true, IsDrgPriced = true,
            BilledAmount = 100m, AllowedAmount = 90m, ContractualAdjustment = 10m,
            DeductibleAmount = 40m, CoinsuranceAmount = 20m, OopMaxReduction = 10m,
            MemberResponsibility = 50m, PlanPaidAmount = 40m,
        }));

        var cas = Assert.Single(ctx.LineAdjudicationResults).AdjustmentReasons;
        Assert.Equal(
            new[] { ("CO", "45", 10m), ("PR", "1", 40m), ("PR", "2", 20m), ("OA", "23", -10m) },
            cas.Select(r => (r.GroupCode, r.ReasonCode, r.Amount)));
        Assert.Equal(40m, 100m - cas.Sum(r => r.Amount));
    }

    [Fact]
    public void ApplyToContext_DeniedLine_AddsContractualSoLineBalances()
    {
        var ctx = ContextFor(BuildClaim(Guid.NewGuid().ToString()));

        BenefitCalculationStage.ApplyToContext(ctx, SingleLineResult(new LineBenefitResult
        {
            LineNumber = 1, IsCovered = false, BilledAmount = 100m, AllowedAmount = 70m,
            ContractualAdjustment = 30m, DenialReasonCode = "96",
            Adjustments = [new AdjustmentReason { GroupCode = "CO", ReasonCode = "96", Amount = 70m }],
        }));

        var cas = Assert.Single(ctx.LineAdjudicationResults).AdjustmentReasons;
        Assert.Equal(
            new[] { ("CO", "45", 30m), ("CO", "96", 70m) },
            cas.Select(r => (r.GroupCode, r.ReasonCode, r.Amount)));
        Assert.Equal(0m, 100m - cas.Sum(r => r.Amount));
    }

    [Fact]
    public void ApplyToContext_PriorLineAdjustments_MergedWithoutDoubleCounting()
    {
        var ctx = ContextFor(BuildClaim(Guid.NewGuid().ToString()));
        ctx.LineAdjudicationResults.Add(new LineAdjudicationResult
        {
            AdjustmentReasons =
            {
                // Same pair the engine emits — engine wins, not summed.
                new ClaimAdjustmentReason { GroupCode = "PR", ReasonCode = "1", Amount = 999m },
                // Not engine-owned — survives.
                new ClaimAdjustmentReason { GroupCode = "CO", ReasonCode = "B7", Amount = 0.01m },
            },
        });

        BenefitCalculationStage.ApplyToContext(ctx, SingleLineResult(new LineBenefitResult
        {
            LineNumber = 1, IsCovered = true, BilledAmount = 100m, AllowedAmount = 100m,
            DeductibleAmount = 25m, MemberResponsibility = 25m, PlanPaidAmount = 75m,
            Adjustments = [new AdjustmentReason { GroupCode = "PR", ReasonCode = "1", Amount = 25m }],
        }));

        var cas = Assert.Single(ctx.LineAdjudicationResults).AdjustmentReasons;
        Assert.Equal(
            new[] { ("PR", "1", 25m), ("CO", "B7", 0.01m) },
            cas.Select(r => (r.GroupCode, r.ReasonCode, r.Amount)));
    }

    [Fact]
    public void ApplyToContext_OrdersLineResultsByClaimLineOrder()
    {
        // PersistenceStage applies line results positionally; the engine
        // sorts by line number.
        var claim = BuildClaim(Guid.NewGuid().ToString());
        claim.ClaimLines = new List<AdapterClaimLine>
        {
            new() { LineNumber = 2, ProcedureCode = "99214", ChargeAmount = 200m, Units = 1 },
            new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 100m, Units = 1 },
        };
        var ctx = ContextFor(claim);

        BenefitCalculationStage.ApplyToContext(ctx, new BenefitResolutionResult
        {
            Success = true,
            Lines =
            [
                new LineBenefitResult { LineNumber = 1, IsCovered = true, AllowedAmount = 100m, PlanPaidAmount = 100m },
                new LineBenefitResult { LineNumber = 2, IsCovered = true, AllowedAmount = 200m, PlanPaidAmount = 200m },
            ],
        });

        Assert.Equal(new[] { 200m, 100m }, ctx.LineAdjudicationResults.Select(l => l.AllowedAmount));
    }

    private static ClaimAdjudicationContext ContextFor(AdapterClaim claim) => new()
    {
        TenantId = "tenant-1",
        ClaimVersionId = claim.Id,
        Claim = claim,
    };

    private static BenefitResolutionResult SingleLineResult(LineBenefitResult line) => new()
    {
        Success = line.IsCovered,
        Lines = [line],
    };

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
