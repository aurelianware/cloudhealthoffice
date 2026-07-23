using ClaimsService.Models;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
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
