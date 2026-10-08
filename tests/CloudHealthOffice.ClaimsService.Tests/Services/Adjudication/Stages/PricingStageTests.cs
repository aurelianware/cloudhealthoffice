using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

public class PricingStageTests
{
    private readonly IFeeSchedulePricingClient _client = Substitute.For<IFeeSchedulePricingClient>();
    private readonly PricingStage _sut;

    public PricingStageTests()
    {
        _sut = new PricingStage(_client, NullLogger<PricingStage>.Instance);
    }

    [Fact]
    public void Stage_RunsAfterNetworkAndBeforeBenefits()
    {
        Assert.Equal("Pricing", _sut.Name);
        var network = new NetworkCredentialingStage(
            Substitute.For<IProviderMembershipClient>(),
            Substitute.For<ICredentialingStatusClient>(),
            Options.Create(new TenantEnforcementPolicyOptions()),
            NullLogger<NetworkCredentialingStage>.Instance);
        var benefits = new BenefitCalculationStage(
            Substitute.For<CloudHealthOffice.BenefitEngine.Services.IBenefitCalculationEngine>(),
            Substitute.For<IMemberResolver>(),
            Substitute.For<IAuthorizationValidationClient>(),
            NullLogger<BenefitCalculationStage>.Instance);
        Assert.True(_sut.Order > network.Order);
        Assert.True(_sut.Order < benefits.Order);
        Assert.False(_sut.IsRequired);
    }

    [Fact]
    public async Task Execute_AllLinesContracted_StoresAllowedAmountsAndPasses()
    {
        var ctx = BuildContext();
        IReadOnlyList<PricingRequest>? captured = null;
        _client.ResolveBatchAsync("tenant-1", Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.ArgAt<IReadOnlyList<PricingRequest>>(1);
                return new PricingResultSet
                {
                    LineResults = new[]
                    {
                        Priced(1, 85m, 200m),
                        Priced(2, 20m, 60m),
                    },
                };
            });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.True(ctx.PricingResult!.IsFullyPriced);
        Assert.Equal(85m, ctx.PricingResult.AllowedAmounts[1]);
        Assert.Equal(20m, ctx.PricingResult.AllowedAmounts[2]);
        Assert.Null(ctx.PendDetails);

        // Request carries per-line pricing inputs.
        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Count);
        var line1 = captured[0];
        Assert.Equal("99213", line1.ProcedureCode);
        Assert.Equal(new[] { "25" }, line1.Modifiers);
        Assert.Equal("1111111112", line1.ProviderNpi); // rendering NPI preferred
        Assert.Equal("11", line1.PlaceOfServiceCode); // claim-level POS fallback
        Assert.Equal(200m, line1.BilledAmount);
        Assert.Equal(1m, line1.Units);
        Assert.Equal(1, line1.LineNumber);
        Assert.Equal(2, line1.TotalLineCount);
        Assert.Equal(ctx.ResolvedPlan!.PlanGuid!.Value.ToString(), line1.PlanId);
        Assert.Equal(new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc), line1.ServiceDate);
        var line2 = captured[1];
        Assert.Equal("22", line2.PlaceOfServiceCode); // line-level POS override
        Assert.Equal(60m, line2.BilledAmount); // line total, not x units
        Assert.Equal(2m, line2.Units);
        Assert.Equal(new DateTime(2026, 4, 16, 0, 0, 0, DateTimeKind.Utc), line2.ServiceDate);
    }

    [Fact]
    public async Task Execute_LineFellBackToBilledCharges_PendsNoContract()
    {
        var ctx = BuildContext();
        _client.ResolveBatchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>())
            .Returns(new PricingResultSet
            {
                LineResults = new[]
                {
                    Priced(1, 85m, 200m),
                    new PricingResult
                    {
                        LineNumber = 2, ProcedureCode = "36415", AllowedAmount = 60m, BilledAmount = 60m,
                        RateSource = RateSource.BilledCharges, FeeScheduleType = FeeScheduleType.Ucr,
                    },
                },
            });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(PricingStage.NoContractPendCode, ctx.PendDetails!.PendCode);
        Assert.Contains("line 2", result.Reason);
        Assert.False(ctx.PricingResult!.IsFullyPriced);
        var unpriced = Assert.Single(ctx.PricingResult.UnpricedLines);
        Assert.Equal(2, unpriced.LineNumber);
        // The billed-charge fallback must not leak into the allowed map.
        Assert.False(ctx.PricingResult.AllowedAmounts.ContainsKey(2));
    }

    [Fact]
    public async Task Execute_LineRateUnresolved_PendsNoContract_WithEngineReason()
    {
        var ctx = BuildContext();
        _client.ResolveBatchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>())
            .Returns(new PricingResultSet
            {
                LineResults = new[]
                {
                    Priced(1, 85m, 200m),
                    new PricingResult
                    {
                        LineNumber = 2, ProcedureCode = "36415", AllowedAmount = 0m, BilledAmount = 60m,
                        RateSource = RateSource.Unresolved, FeeScheduleType = FeeScheduleType.Commercial,
                        FeeScheduleId = "FS-1",
                        UnresolvedReason = "PercentOfMedicare rate with no Medicare reference schedule",
                    },
                },
            });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(PricingStage.NoContractPendCode, ctx.PendDetails!.PendCode);
        Assert.Contains("no Medicare reference", result.Reason);
        var unpriced = Assert.Single(ctx.PricingResult!.UnpricedLines);
        Assert.Equal(2, unpriced.LineNumber);
        // The engine's $0 must not be accepted as an allowed amount.
        Assert.False(ctx.PricingResult.AllowedAmounts.ContainsKey(2));
        Assert.False(ctx.PricingResult.IsFullyPriced);
    }

    [Fact]
    public void BuildRequests_MultiUnitLine_SendsLineTotalAsBilled()
    {
        var ctx = BuildContext();
        ctx.Claim.ClaimLines[0].ChargeAmount = 300m;
        ctx.Claim.ClaimLines[0].Units = 3m;

        var request = PricingStage.BuildRequests(ctx)[0];

        Assert.Equal(300m, request.BilledAmount);
        Assert.Equal(3m, request.Units);
    }

    [Fact]
    public async Task Execute_PricingServiceUnavailable_PendsWithPricingCode()
    {
        var ctx = BuildContext();
        _client.ResolveBatchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>())
            .Returns((PricingResultSet?)null);

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(PricingStage.PricingUnavailablePendCode, ctx.PendDetails!.PendCode);
        Assert.True(ctx.PricingResult!.PricingUnavailable);
        Assert.False(ctx.PricingResult.IsFullyPriced);
        Assert.Empty(ctx.PricingResult.AllowedAmounts);
    }

    [Fact]
    public async Task Execute_ClientThrows_PendsInsteadOfPropagating()
    {
        var ctx = BuildContext();
        _client.ResolveBatchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(ctx.PricingResult!.PricingUnavailable);
    }

    [Fact]
    public async Task Execute_LineMissingFromResponse_Pends()
    {
        var ctx = BuildContext();
        _client.ResolveBatchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>())
            .Returns(new PricingResultSet { LineResults = new[] { Priced(1, 85m, 200m) } });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(2, Assert.Single(ctx.PricingResult!.UnpricedLines).LineNumber);
    }

    [Fact]
    public async Task Execute_ZeroChargeLineWithoutRate_PricesAtZero()
    {
        var ctx = BuildContext();
        ctx.Claim.ClaimLines[1].ChargeAmount = 0m;
        _client.ResolveBatchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>())
            .Returns(new PricingResultSet
            {
                LineResults = new[]
                {
                    Priced(1, 85m, 200m),
                    new PricingResult
                    {
                        LineNumber = 2, AllowedAmount = 0m, BilledAmount = 0m,
                        RateSource = RateSource.BilledCharges, FeeScheduleType = FeeScheduleType.Ucr,
                    },
                },
            });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(0m, ctx.PricingResult!.AllowedAmounts[2]);
    }

    [Fact]
    public async Task Execute_CapitatedLine_IsPricedAtZero()
    {
        var ctx = BuildContext();
        _client.ResolveBatchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>())
            .Returns(new PricingResultSet
            {
                LineResults = new[]
                {
                    Priced(1, 85m, 200m),
                    new PricingResult
                    {
                        LineNumber = 2, AllowedAmount = 0m, BilledAmount = 60m,
                        RateSource = RateSource.Capitation, FeeScheduleType = FeeScheduleType.Capitation,
                    },
                },
            });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(0m, ctx.PricingResult!.AllowedAmounts[2]);
    }

    [Fact]
    public async Task Execute_DuplicateLineNumbers_PendsWithoutCallingPricing()
    {
        var ctx = BuildContext();
        ctx.Claim.ClaimLines[1].LineNumber = 1;

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.False(ctx.PricingResult!.IsFullyPriced);
        await _client.DidNotReceive().ResolveBatchAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_EarlierPendDetails_ArePreserved()
    {
        var ctx = BuildContext();
        var earlier = new PendDetails { PendCode = ProviderIntegrityStage.MedicalReviewPendCode, PendReason = "integrity" };
        ctx.PendDetails = earlier;
        _client.ResolveBatchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>())
            .Returns((PricingResultSet?)null);

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Same(earlier, ctx.PendDetails);
    }

    [Fact]
    public void BuildRequests_NoRenderingProvider_UsesBillingNpi()
    {
        var ctx = BuildContext();
        ctx.Claim.RenderingProviderNPI = null;

        var requests = PricingStage.BuildRequests(ctx);

        Assert.All(requests, r => Assert.Equal("1234567893", r.ProviderNpi));
    }

    private static PricingResult Priced(int line, decimal allowed, decimal billed) => new()
    {
        LineNumber = line,
        AllowedAmount = allowed,
        BilledAmount = billed,
        RateSource = RateSource.ContractedRate,
        FeeScheduleType = FeeScheduleType.Commercial,
        NetworkStatus = NetworkStatus.InNetwork,
        FeeScheduleId = "FS-1",
    };

    private static ClaimAdjudicationContext BuildContext()
    {
        var serviceDate = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        var planGuid = Guid.NewGuid();
        var claim = new AdapterClaim
        {
            TenantId = "tenant-1",
            Id = "claim-1",
            ClaimVersionId = "claim-1",
            MemberId = "MEM-1",
            BenefitPlanId = "PLAN-CODE",
            BillingProviderNPI = "1234567893",
            RenderingProviderNPI = "1111111112",
            PlaceOfServiceCode = "11",
            ClaimType = ClaimType.Professional,
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate.AddDays(1),
            ClaimLines = new List<AdapterClaimLine>
            {
                new()
                {
                    LineNumber = 1, ProcedureCode = "99213", Modifiers = new List<string> { "25" },
                    ChargeAmount = 200m, Units = 1m, ServiceDateFrom = serviceDate,
                },
                new()
                {
                    LineNumber = 2, ProcedureCode = "36415", ChargeAmount = 60m, Units = 2m,
                    PlaceOfServiceCode = "22", ServiceDateFrom = serviceDate.AddDays(1),
                },
            },
        };

        return new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.ClaimVersionId,
            Claim = claim,
            ResolvedPlan = new ResolvedBenefitPlan { Id = "PLAN-CODE", PlanGuid = planGuid },
        };
    }
}
