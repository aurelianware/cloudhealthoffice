using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenefitPlanService.Controllers;
using BenefitPlanService.Services;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.FeeScheduleEngine.Services;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.NcciEngine.Models;
using CloudHealthOffice.NcciEngine.Services;
using CloudHealthOffice.ClaimsScrubEngine.Models;
using CloudHealthOffice.ClaimsScrubEngine.Services;
using CloudHealthOffice.OperatingMode;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Gates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using StackExchange.Redis;

namespace CloudHealthOffice.AdjudicationController.Tests;

public class AdjudicationControllerTests : IClassFixture<AdjudicationControllerTests.Factory>
{
    private const string TenantId = "test-tenant-001";
    private static readonly Guid PlanId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    // Match the server's wire format (string enums via JsonStringEnumConverter
    // registered by AddCloudHealthOfficeJsonOptions).
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Factory _factory;

    public AdjudicationControllerTests(Factory factory) => _factory = factory;

    // ─────────────────────────────────────────────────────────────
    // WebApplicationFactory with mocked engine interfaces
    // ─────────────────────────────────────────────────────────────

    public class Factory : WebApplicationFactory<Program>
    {
        public IBenefitCalculationEngine BenefitEngine { get; } = Substitute.For<IBenefitCalculationEngine>();
        public IRateResolutionService RateEngine { get; } = Substitute.For<IRateResolutionService>();
        public INcciEditService NcciEngine { get; } = Substitute.For<INcciEditService>();
        public IClaimRoutingService ScrubEngine { get; } = Substitute.For<IClaimRoutingService>();
        public IOperatingModeProvider OperatingModeProvider { get; } = Substitute.For<IOperatingModeProvider>();
        public IProviderIntegrityGate ProviderIntegrityGate { get; } = Substitute.For<IProviderIntegrityGate>();
        public IPriorAuthRuleEngine PriorAuthEngine { get; } = Substitute.For<IPriorAuthRuleEngine>();
        public IPaymentEstimateService EstimateService { get; } = Substitute.For<IPaymentEstimateService>();
        public IBenefitPlanService PlanService { get; } = Substitute.For<IBenefitPlanService>();
        public CloudHealthOffice.BenefitEngine.Services.IAccumulatorService Accumulators { get; } =
            Substitute.For<CloudHealthOffice.BenefitEngine.Services.IAccumulatorService>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureServices(services =>
            {
                // Remove hosted services that require real DB connections (PriorAuthRuleEngineSeeder etc.)
                services.RemoveAll<IHostedService>();

                // Remove infrastructure services that require real connections
                services.RemoveAll<IConnectionMultiplexer>();
                services.RemoveAll<IClaimsAccumulatorSource>();
                services.RemoveAll<IAccumulatorAuditWriter>();

                // Replace the four engine interfaces with mocks
                services.RemoveAll<IBenefitCalculationEngine>();
                services.AddSingleton(BenefitEngine);

                services.RemoveAll<IRateResolutionService>();
                services.AddSingleton(RateEngine);

                services.RemoveAll<INcciEditService>();
                services.AddSingleton(NcciEngine);

                services.RemoveAll<IClaimRoutingService>();
                services.AddSingleton(ScrubEngine);

                // Stub out PriorAuthRuleEngine and its repository
                services.RemoveAll<IPriorAuthRuleEngine>();
                services.AddSingleton(PriorAuthEngine);
                services.RemoveAll<IPaRuleRepository>();
                services.AddSingleton(Substitute.For<IPaRuleRepository>());

                // Stub out ProviderEnrollment gate (passthrough allows all claims)
                services.RemoveAll<IEnrollmentDecisionGate>();
                services.AddSingleton<IEnrollmentDecisionGate, PassthroughEnrollmentGate>();

                // Stub out new pipeline services with defaults that pass
                services.RemoveAll<IOperatingModeProvider>();
                services.AddSingleton(OperatingModeProvider);

                services.RemoveAll<IClaimTypeRouter>();
                services.AddSingleton<IClaimTypeRouter, ClaimTypeRouter>();

                services.RemoveAll<IProviderIntegrityGate>();
                services.AddSingleton(ProviderIntegrityGate);

                // Plan and estimate services, so the auth pipeline tests can
                // observe the tenant and actor the controllers pass down.
                services.RemoveAll<IPaymentEstimateService>();
                services.AddSingleton(EstimateService);
                services.RemoveAll<IBenefitPlanService>();
                services.AddSingleton(PlanService);
                services.RemoveAll<CloudHealthOffice.BenefitEngine.Services.IAccumulatorService>();
                services.AddSingleton(Accumulators);

                // Stub out Redis connection with a no-op
                services.AddSingleton(Substitute.For<IConnectionMultiplexer>());

                // Stub out claims accumulator source and audit writer
                services.AddSingleton(Substitute.For<IClaimsAccumulatorSource>());
                services.AddSingleton(Substitute.For<IAccumulatorAuditWriter>());
            });
        }
    }

    private HttpClient CreateClientWithTenant(string? tenantId = TenantId)
    {
        // The handler turns X-Tenant-ID into a development-signed token for that
        // tenant; the server takes the tenant from the token. With no tenant
        // header the request carries no token at all.
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler());
        if (tenantId is not null)
            client.DefaultRequestHeaders.Add("X-Tenant-ID", tenantId);
        return client;
    }

    // ─────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// What the payment estimate needs from the factory's seams: prior auth
    /// matches no rule, the tenant runs CHO in Replace mode, the provider is clear.
    /// </summary>
    private void SetupEstimateDefaults()
    {
        _factory.PriorAuthEngine
            .EvaluateAsync(Arg.Any<PaRuleContext>(), Arg.Any<CancellationToken>())
            .Returns(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Pend,
                FiringRuleId = "NoRuleMatch",
                FiringRuleName = "No rules matched",
                ResolvedRuleSetKey = "test"
            });

        _factory.OperatingModeProvider
            .GetConfigurationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OperatingModeConfiguration { TenantId = TenantId });

        // The forceRefresh parameter (capability 5.10) defaults to false; the
        // stub matches any value.
        _factory.ProviderIntegrityGate
            .CheckAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new ProviderIntegrityResult { Passed = true, Rating = "Clear", IntegrityScore = 95 });
    }

    private PaymentEstimateService Estimator(IRateResolutionService? rateEngine = null) => new(
        rateEngine ?? _factory.RateEngine, _factory.BenefitEngine, _factory.ProviderIntegrityGate, _factory.PriorAuthEngine,
        _factory.OperatingModeProvider, new ClaimTypeRouter(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentEstimateService>.Instance);

    // ── Facility setting: institutional claim type vs. a valid bill type ──

    /// <summary>Prices requests with the real engine against 99213 at $110 non-facility / $75 facility.</summary>
    internal static async Task<decimal> PriceAtFacilityScheduleAsync(IReadOnlyList<PricingRequest> requests)
    {
        var schedules = Substitute.For<IFeeScheduleRepository>();
        schedules.GetDefaultForPlanAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(new FeeSchedule
            {
                Id = "MPFS-FAC", TenantId = TenantId, Name = "MPFS",
                Type = FeeScheduleType.MedicareMpfs,
                EffectiveDate = new DateTime(2026, 1, 1),
                Lines = [new FeeScheduleLine { ProcedureCode = "99213", Rate = 110m, FacilityRate = 75m }],
            });
        var engine = new RateResolutionService(
            schedules, Substitute.For<IProviderContractRepository>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RateResolutionService>.Instance);
        var priced = await engine.ResolveBatchAsync(requests, CancellationToken.None);
        return Assert.Single(priced.LineResults).AllowedAmount;
    }

    /// <summary>
    /// The payment estimate sends the facility-setting inputs (claim type, bill
    /// type, place of service) to the fee schedule engine so the line prices at the
    /// facility or non-facility rate the claims pipeline would use. An 837I with
    /// no bill type carries the facility type ("13") in the POS slot, which is
    /// non-facility if read as a CMS place of service; a junk bill type on a
    /// professional claim leaves the POS rule in charge.
    /// </summary>
    [Theory]
    [InlineData("Institutional", null, "13", true, 75)]    // 837I, no bill type: facility
    [InlineData("Institutional", "131", "13", true, 75)]   // 837I with TOB: facility
    [InlineData("Professional", "131", "11", false, 75)]   // a valid TOB prices at the facility rate
    [InlineData("Professional", "N/A", "11", false, 110)]  // junk TOB: POS rule, office = non-facility
    [InlineData("Professional", "0", "11", false, 110)]
    [InlineData("Professional", "11", "11", false, 110)]
    [InlineData("Professional", null, "22", false, 75)]    // POS 22 on-campus outpatient: facility
    public async Task Estimate_PricesTheFacilitySetting(
        string claimType, string? billType, string pos, bool expectInstitutional, int expectedAllowed)
    {
        SetupEstimateDefaults();
        IReadOnlyList<PricingRequest>? estimate = null;
        var rateEngine = Substitute.For<IRateResolutionService>();
        rateEngine.ResolveBatchAsync(Arg.Do<IReadOnlyList<PricingRequest>>(r => estimate = r), Arg.Any<CancellationToken>())
            .Returns(new PricingResultSet { LineResults = [] });

        await Estimator(rateEngine).EstimateAsync(TenantId, new BenefitPlanService.Models.Estimate.PaymentEstimateRequest
        {
            MemberId = "MBR-001",
            BenefitPlanId = PlanId,
            ProviderNpi = "1234567890",
            ServiceDate = new DateOnly(2026, 1, 15),
            ClaimType = claimType,
            BillType = billType,
            Lines =
            [
                new BenefitPlanService.Models.Estimate.PaymentEstimateLineRequest
                {
                    LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 200m, PlaceOfService = pos,
                    DiagnosisCodes = ["Z00.00"],
                },
            ],
        });

        var e = Assert.Single(estimate!);
        Assert.Equal(expectInstitutional, e.IsInstitutional);
        Assert.Equal(expectedAllowed, await PriceAtFacilityScheduleAsync(estimate!));
    }

    // ── DRG stay through the real fee schedule and benefit engines ──

    /// <summary>
    /// A 3-line DRG stay is priced once by the real fee schedule engine (case
    /// rate $12,000 allocated across lines by billed charges) and the real
    /// benefit engine takes the claim-level inpatient path — one $250 inpatient
    /// copay, not 3 × $250, deductible once. The estimate is Prospective:
    /// nothing is written to the accumulators.
    /// </summary>
    [Fact]
    public async Task Estimate_InstitutionalDrgStay_RealEngines_CostSharesOncePerStay()
    {
        var accumulators = WireRealEngines(caseRate: 12000m);

        var estimate = await Estimator().EstimateAsync(TenantId, MakeDrgStayEstimate() with { DrgCode = " 470 " });

        Assert.Equal("estimated", estimate.Status);
        Assert.Equal(42500m, estimate.Totals.BilledAmount);
        Assert.Equal(12000m, estimate.Totals.AllowedAmount);
        Assert.Equal(500m, estimate.Totals.DeductibleAmount);    // once
        Assert.Equal(250m, estimate.Totals.CopayAmount);         // one copay per stay, not 3 x $250
        Assert.Equal(2250m, estimate.Totals.CoinsuranceAmount);  // 20% × (12000 − 750)
        Assert.Equal(3000m, estimate.Totals.PatientResponsibility);
        Assert.Equal(9000m, estimate.Totals.PayerResponsibility);
        Assert.Equal(new[] { 3388.23m, 423.52m, 8188.25m }, estimate.Lines.Select(l => l.AllowedAmount));
        Assert.Equal(new[] { "0120", "0250", "0360" }, estimate.Lines.Select(l => l.RevenueCode));
        Assert.All(estimate.Lines, l => Assert.Contains(l.Messages, m => m.Code == "DRG_CASE_RATE_APPLIED"));
        await accumulators.DidNotReceiveWithAnyArgs().ApplyUpdatesAsync(
            default!, default!, default, default!, default!, default!, default);
    }

    /// <summary>
    /// A case rate above total billed would allocate every line more than it
    /// billed (negative CO-45). The estimate reports needs_review (PRICING_REVIEW,
    /// the pend the claims pipeline raises) with no amounts quoted.
    /// </summary>
    [Fact]
    public async Task Estimate_InstitutionalDrgStay_CaseRateAboveBilled_RealEngines_NeedsReview()
    {
        var accumulators = WireRealEngines(caseRate: 50000m);

        var estimate = await Estimator().EstimateAsync(TenantId, MakeDrgStayEstimate());

        Assert.Equal("needs_review", estimate.Status);
        Assert.All(estimate.Lines, l =>
        {
            Assert.Equal("needs_review", l.Status);
            Assert.Equal(0m, l.PatientResponsibility);
            Assert.Contains(l.Messages, m => m.Code == "PRICING_REVIEW");
        });
        Assert.Equal(42500m, estimate.Totals.BilledAmount);
        Assert.Equal(0m, estimate.Totals.AllowedAmount);
        await accumulators.DidNotReceiveWithAnyArgs().ApplyUpdatesAsync(
            default!, default!, default, default!, default!, default!, default);
    }

    /// <summary>
    /// The estimate's PlaceOfService is a CMS place of service. With no service
    /// category mapping, the stay (BillType 111, POS 21) must be categorized from
    /// the type of bill as Inpatient Hospital — POS 21 must not be read as
    /// facility type 21 (skilled nursing). The plan is keyed by X12 "48" and
    /// matches through the category-name alias.
    /// </summary>
    [Fact]
    public async Task Estimate_InstitutionalBillType111Pos21_NoMapping_ResolvesInpatientHospitalFromTob()
    {
        var realResolver = new ServiceCategoryResolver(
            new EmptyCategoryRepository(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ServiceCategoryResolver>.Instance);
        WireRealEngines(caseRate: 12000m, categoryResolver: realResolver);

        var estimate = await Estimator().EstimateAsync(TenantId, MakeDrgStayEstimate());

        Assert.Equal("estimated", estimate.Status);
        Assert.Equal(9000m, estimate.Totals.PayerResponsibility);

        var sent = _factory.BenefitEngine.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IBenefitCalculationEngine.CalculateAsync))
            .Select(c => (BenefitResolutionRequest)c.GetArguments()[0]!)
            .Last();
        Assert.Equal("111", sent.TypeOfBill);
        Assert.False(sent.PlaceOfServiceIsFacilityType);

        var match = await realResolver.ResolveAsync(TenantId, PlanId, sent.ServiceDate, "", "CPT", "21",
            Array.Empty<string>(), "0120",
            new ServiceCategoryClaimContext(sent.ClaimType, sent.TypeOfBill, sent.PlaceOfServiceIsFacilityType));
        Assert.Equal(ServiceCategoryNames.InpatientHospital, match!.ServiceTypeCode);
        Assert.Equal("TOB-fallback:11", match.MatchedRule);
    }

    private static BenefitPlanService.Models.Estimate.PaymentEstimateRequest MakeDrgStayEstimate()
    {
        var billed = new[] { 12000m, 1500m, 29000m };
        var revenue = new[] { "0120", "0250", "0360" };
        return new()
        {
            MemberId = "MBR-001",
            SubscriberId = "SUB-001",
            BenefitPlanId = PlanId,
            ProviderNpi = "1234567890",
            ServiceDate = new DateOnly(2026, 1, 15),
            ClaimType = "Institutional",
            BillType = "111",
            DrgCode = "470",
            LengthOfStay = 4,
            Lines = Enumerable.Range(0, 3).Select(i => new BenefitPlanService.Models.Estimate.PaymentEstimateLineRequest
            {
                LineNumber = i + 1,
                ProcedureCode = "99213",
                RevenueCode = revenue[i],
                ChargeAmount = billed[i],
                PlaceOfService = "21",
                DiagnosisCodes = ["Z00.00"],
            }).ToList(),
        };
    }

    private sealed class EmptyCategoryRepository : IServiceCategoryMappingRepository
    {
        public Task<IReadOnlyList<ServiceCategoryMapping>> GetMappingsAsync(
            string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ServiceCategoryMapping>>(Array.Empty<ServiceCategoryMapping>());
    }

    /// <summary>
    /// Routes the factory's rate and benefit engine seams to real engines:
    /// a plan-default DRG schedule (DRG 470 at <paramref name="caseRate"/>)
    /// and an inpatient benefit with a $500 deductible, $250 copay and 20%
    /// coinsurance. Returns the accumulator service so writes can be counted.
    /// </summary>
    private CloudHealthOffice.BenefitEngine.Services.IAccumulatorService WireRealEngines(
        decimal caseRate, IServiceCategoryResolver? categoryResolver = null)
    {
        SetupEstimateDefaults();

        var schedules = Substitute.For<IFeeScheduleRepository>();
        schedules.GetDefaultForPlanAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(new FeeSchedule
            {
                Id = "DRG-SYNC", TenantId = TenantId, Name = "DRG",
                Type = FeeScheduleType.Drg,
                EffectiveDate = new DateTime(2026, 1, 1),
                Lines = [new FeeScheduleLine { ProcedureCode = "470", Rate = caseRate }],
            });
        var contracts = Substitute.For<IProviderContractRepository>();
        var rateEngine = new RateResolutionService(
            schedules, contracts,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RateResolutionService>.Instance);
        _factory.RateEngine
            .ResolveBatchAsync(Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>())
            .Returns(ci => rateEngine.ResolveBatchAsync(ci.Arg<IReadOnlyList<PricingRequest>>(), CancellationToken.None));

        var plan = new BenefitPlanConfig
        {
            Id = PlanId,
            TenantId = TenantId,
            PlanName = "Sync PPO",
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
        planProvider.GetPlanAsync(PlanId, Arg.Any<CancellationToken>()).Returns(plan);
        var resolver = categoryResolver;
        if (resolver is null)
        {
            resolver = Substitute.For<IServiceCategoryResolver>();
            resolver.ResolveAsync(default!, default, default, default!, default!, default!, default!, default, default, default)
                .ReturnsForAnyArgs(new ServiceCategoryMatch
                {
                    ServiceTypeCode = "48", ServiceTypeDescription = "Hospital - Inpatient",
                    MatchedBy = "Test", MatchedRule = "Fixed:48",
                });
        }
        var accumulators = Substitute.For<CloudHealthOffice.BenefitEngine.Services.IAccumulatorService>();
        accumulators.GetAccumulatorsAsync(default!, default!, default, default!, default)
            .ReturnsForAnyArgs(new List<AccumulatorSnapshot>
            {
                Snapshot(AccumulatorType.IndividualDeductible, AccumulatorScope.Individual, 500),
                Snapshot(AccumulatorType.FamilyDeductible, AccumulatorScope.Family, 1500),
                Snapshot(AccumulatorType.IndividualOutOfPocketMax, AccumulatorScope.Individual, 10000),
                Snapshot(AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family, 20000),
            });
        var benefitEngine = new BenefitCalculationEngine(
            resolver, planProvider, accumulators,
            new BenefitRuleGate(Microsoft.Extensions.Logging.Abstractions.NullLogger<BenefitRuleGate>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BenefitCalculationEngine>.Instance);
        // The payment estimate calls the engine directly (Prospective mode).
        _factory.BenefitEngine
            .CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => benefitEngine.CalculateAsync(ci.ArgAt<BenefitResolutionRequest>(0), CancellationToken.None));

        return accumulators;
    }

    private static AccumulatorSnapshot Snapshot(AccumulatorType type, AccumulatorScope scope, decimal limit) => new()
    {
        Type = type,
        Scope = scope,
        NetworkTier = NetworkTier.InNetwork,
        LimitAmount = limit,
        RemainingAmount = limit,
    };

    // ═══════════════════════════════════════════════════════════════
    // GET /api/v1/adjudication/provider-integrity/{npi} — standalone,
    // side-effect-free integrity check exposed for claims-service's
    // ProviderIntegrityStage (closes the gap where calculate-benefits
    // never checked federal exclusion at all).
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckProviderIntegrity_DelegatesToGate_ReturnsResultVerbatim()
    {
        _factory.ProviderIntegrityGate
            .CheckAsync("1234567890", TenantId, forceRefresh: false, Arg.Any<CancellationToken>())
            .Returns(new ProviderIntegrityResult
            {
                Passed = false,
                IsExcluded = true,
                Rating = "Blocked",
                IntegrityScore = 0,
                DenialCode = "B7",
                DenialReason = "Provider is excluded from federal healthcare programs",
            });

        using var client = CreateClientWithTenant();

        var response = await client.GetAsync("/api/v1/adjudication/provider-integrity/1234567890");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ProviderIntegrityResult>(Json);
        Assert.NotNull(result);
        Assert.False(result!.Passed);
        Assert.True(result.IsExcluded);
        Assert.Equal("B7", result.DenialCode);
    }

    // ═══════════════════════════════════════════════════════════════
    // 3. /calculate-benefits with HDHP plan → deductible before coinsurance
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CalculateBenefits_HdhpPlan_AppliesDeductibleBeforeCoinsurance()
    {
        // Arrange — Simulate HDHP: full deductible applied, then coinsurance on remainder
        decimal allowed = 300m;
        decimal deductible = 300m; // HDHP forces full deductible first
        decimal coinsurance = 0m;  // Nothing remains after deductible
        decimal copay = 0m;
        decimal memberResp = deductible;
        decimal planPaid = allowed - memberResp;

        _factory.BenefitEngine
            .CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult
            {
                Success = true,
                Lines =
                [
                    new LineBenefitResult
                    {
                        LineNumber = 1,
                        IsCovered = true,
                        ServiceTypeCode = "98",
                        ServiceTypeDescription = "Office Visit",
                        AllowedAmount = allowed,
                        BilledAmount = 400m,
                        DeductibleAmount = deductible,
                        CopayAmount = copay,
                        CoinsuranceAmount = coinsurance,
                        CoinsurancePercent = 0.20m,
                        MemberResponsibility = memberResp,
                        PlanPaidAmount = planPaid,
                        Adjustments =
                        [
                            new AdjustmentReason { GroupCode = "CO", ReasonCode = "45", Amount = 100m },
                            new AdjustmentReason { GroupCode = "PR", ReasonCode = "1", Amount = deductible }
                        ]
                    }
                ],
                Totals = new ClaimTotals
                {
                    TotalBilled = 400m,
                    TotalAllowed = allowed,
                    TotalDeductible = deductible,
                    TotalCopay = copay,
                    TotalCoinsurance = coinsurance,
                    TotalMemberResponsibility = memberResp,
                    TotalPlanPaid = planPaid
                },
                AccumulatorSnapshot =
                [
                    new AccumulatorState
                    {
                        Type = AccumulatorType.IndividualDeductible,
                        Scope = AccumulatorScope.Individual,
                        NetworkTier = NetworkTier.InNetwork,
                        LimitAmount = 3000m, // HDHP typically high deductible
                        AccumulatedAmountBefore = 0m,
                        AmountApplied = deductible,
                        AccumulatedAmountAfter = deductible,
                        RemainingAmount = 2700m
                    }
                ]
            });

        using var client = CreateClientWithTenant();
        var request = new BenefitResolutionRequest
        {
            MemberId = "MBR-001",
            SubscriberId = "SUB-001",
            BenefitPlanId = PlanId,
            ServiceDate = new DateOnly(2026, 1, 15),
            NetworkTier = NetworkTier.InNetwork,
            ClaimId = "CLM-003",
            Lines =
            [
                new ClaimLineInput
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    PlaceOfService = "11",
                    BilledAmount = 400m,
                    Units = 1,
                    DiagnosisCodes = ["Z00.00"]
                }
            ]
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/adjudication/calculate-benefits", request, Json);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<BenefitResolutionResult>(Json);
        Assert.NotNull(result);
        Assert.True(result.Success);

        var line = Assert.Single(result.Lines);
        // HDHP: deductible consumes entire allowed, coinsurance is zero
        Assert.Equal(300m, line.DeductibleAmount);
        Assert.Equal(0m, line.CoinsuranceAmount);
        Assert.Equal(0m, line.CopayAmount);
        Assert.Equal(300m, line.MemberResponsibility);
        Assert.Equal(0m, line.PlanPaidAmount);

        // Deductible adjustment applied before coinsurance in adjustment reasons
        var deductAdj = line.Adjustments.First(a => a.ReasonCode == "1");
        Assert.Equal(300m, deductAdj.Amount);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4. /resolve-rates with in-network provider → contracted rate
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveRates_InNetworkProvider_ReturnsContractedRate()
    {
        // Arrange — clear calls accumulated from earlier tests in this shared fixture
        _factory.RateEngine.ClearReceivedCalls();
        _factory.RateEngine
            .ResolveBatchAsync(Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>())
            .Returns(new PricingResultSet
            {
                LineResults =
                [
                    new PricingResult
                    {
                        LineNumber = 1,
                        ProcedureCode = "99213",
                        AllowedAmount = 125.50m,
                        BilledAmount = 200m,
                        FeeScheduleType = FeeScheduleType.Commercial,
                        RateSource = RateSource.ContractedRate,
                        NetworkStatus = NetworkStatus.InNetwork,
                        FeeScheduleId = "FS-COMM-2026",
                        FeeScheduleName = "Commercial PPO 2026"
                    }
                ]
            });

        using var client = CreateClientWithTenant();
        var requests = new List<PricingRequest>
        {
            new()
            {
                ProcedureCode = "99213",
                ProviderNpi = "1234567890",
                PlaceOfServiceCode = "11",
                ServiceDate = new DateTime(2026, 1, 15),
                PlanId = PlanId.ToString(),
                BilledAmount = 200m,
                Units = 1,
                LineNumber = 1
            }
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/adjudication/resolve-rates", requests);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<PricingResultSet>(Json);
        Assert.NotNull(result);

        var line = Assert.Single(result.LineResults);
        Assert.Equal(125.50m, line.AllowedAmount);
        Assert.Equal(NetworkStatus.InNetwork, line.NetworkStatus);
        Assert.Equal(RateSource.ContractedRate, line.RateSource);
        Assert.Equal(FeeScheduleType.Commercial, line.FeeScheduleType);

        // Verify tenant ID was injected
        await _factory.RateEngine.Received(1)
            .ResolveBatchAsync(
                Arg.Is<IReadOnlyList<PricingRequest>>(r => r.All(p => p.TenantId == TenantId)),
                Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    // 5. /resolve-rates with OON provider → Medicare-based allowable
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveRates_OutOfNetworkProvider_ReturnsMedicareBasedAllowable()
    {
        // Arrange — OON falls back to Medicare MPFS
        _factory.RateEngine
            .ResolveBatchAsync(Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>())
            .Returns(new PricingResultSet
            {
                LineResults =
                [
                    new PricingResult
                    {
                        LineNumber = 1,
                        ProcedureCode = "99213",
                        AllowedAmount = 92.34m,
                        BilledAmount = 250m,
                        FeeScheduleType = FeeScheduleType.MedicareMpfs,
                        RateSource = RateSource.MedicareMpfs,
                        NetworkStatus = NetworkStatus.OutOfNetwork,
                        FeeScheduleId = "FS-MPFS-2026",
                        FeeScheduleName = "Medicare MPFS 2026 Locality 01"
                    }
                ]
            });

        using var client = CreateClientWithTenant();
        var requests = new List<PricingRequest>
        {
            new()
            {
                ProcedureCode = "99213",
                ProviderNpi = "9999999999",
                PlaceOfServiceCode = "11",
                ServiceDate = new DateTime(2026, 1, 15),
                PlanId = PlanId.ToString(),
                BilledAmount = 250m,
                Units = 1,
                LineNumber = 1
            }
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/adjudication/resolve-rates", requests);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<PricingResultSet>(Json);
        Assert.NotNull(result);

        var line = Assert.Single(result.LineResults);
        Assert.Equal(92.34m, line.AllowedAmount);
        Assert.Equal(NetworkStatus.OutOfNetwork, line.NetworkStatus);
        Assert.Equal(RateSource.MedicareMpfs, line.RateSource);
        Assert.Equal(FeeScheduleType.MedicareMpfs, line.FeeScheduleType);
    }

    // ═══════════════════════════════════════════════════════════════
    // 6. Missing tenant ID → 401
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AnyEndpoint_MissingTenantId_Returns401()
    {
        // Arrange — no X-Tenant-ID header, and therefore no token
        using var client = CreateClientWithTenant(tenantId: null);

        // Act
        var benefitsResponse = await client.PostAsJsonAsync("/api/v1/adjudication/calculate-benefits",
            new BenefitResolutionRequest
            {
                MemberId = "MBR-001", SubscriberId = "SUB-001",
                BenefitPlanId = PlanId, ServiceDate = new DateOnly(2026, 1, 15),
                ClaimId = "CLM-X", NetworkTier = NetworkTier.InNetwork,
                Lines = [new ClaimLineInput { LineNumber = 1, ProcedureCode = "99213", PlaceOfService = "11", BilledAmount = 100m }]
            });
        var ratesResponse = await client.PostAsJsonAsync("/api/v1/adjudication/resolve-rates",
            new List<PricingRequest> { new() { ProcedureCode = "99213", BilledAmount = 100m } });
        var ncciResponse = await client.PostAsJsonAsync("/api/v1/adjudication/ncci-check",
            new NcciScrubRequest { ClaimId = "CLM-X", ServiceLines = [new ClaimServiceLine { LineNumber = 1, ProcedureCode = "99213", Units = 1, ServiceDate = new DateOnly(2026, 1, 15) }] });

        // Assert — no token → 401
        Assert.Equal(HttpStatusCode.Unauthorized, benefitsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, ratesResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, ncciResponse.StatusCode);
    }

    [Fact]
    public async Task ResolveRates_EmptyRequestBody_Returns400()
    {
        using var client = CreateClientWithTenant();

        // Send malformed JSON
        var stringContent = new StringContent("{invalid", System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/v1/adjudication/resolve-rates", stringContent);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NcciCheck_ValidRequest_ReturnsResult()
    {
        // Arrange
        _factory.NcciEngine
            .ScrubAsync(Arg.Any<NcciScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NcciScrubResult
            {
                ClaimId = "CLM-NCCI-01",
                NcciPairsChecked = 3,
                MueChecked = 2
            });

        using var client = CreateClientWithTenant();
        var request = new NcciScrubRequest
        {
            TenantId = TenantId,
            ClaimId = "CLM-NCCI-01",
            ClaimType = "837P",
            ServiceLines =
            [
                new ClaimServiceLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    Units = 1,
                    ServiceDate = new DateOnly(2026, 1, 15),
                    PlaceOfServiceCode = "11"
                }
            ]
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/adjudication/ncci-check", request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<NcciScrubResult>(Json);
        Assert.NotNull(result);
        Assert.True(result.Passed);
        Assert.Equal("CLM-NCCI-01", result.ClaimId);
        Assert.Equal(3, result.NcciPairsChecked);
        Assert.Equal(2, result.MueChecked);
        Assert.Empty(result.EditFailures);
    }

    // ═══════════════════════════════════════════════════════════════
    // Claim type routing (ClaimTypeRouter, used by the payment estimate
    // to decide whether CHO is the authoritative payer)
    // ═══════════════════════════════════════════════════════════════

    // Every claim-type spelling routes to the CHO engine in Replace mode.
    [Theory]
    [InlineData("Professional")]
    [InlineData("Institutional")]
    [InlineData("Dental")]
    [InlineData("professional")]  // case-insensitive
    [InlineData(null)]            // default
    public void EveryClaimTypeSpelling_RoutesToChoReplace(string? claimType)
    {
        var router = new ClaimTypeRouter();
        var config = new OperatingModeConfiguration
        {
            TenantId = TenantId,
            Engines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["benefitCalculation"] = "replace"
            }
        };

        // Route should succeed for all valid claim types
        var decision = router.Route(config, claimType ?? "Professional", lineOfBusiness: null);
        Assert.Equal(AdjudicationRoute.ChoReplace, decision.Route);
    }
}

/// <summary>
/// Capability 5.12a — covers the new
/// <c>POST /api/v1/adjudication/reverse-claim</c> endpoint added per
/// Decision 15. Verifies the controller forwards to
/// <see cref="IBenefitCalculationEngine.ReverseClaimAsync"/> with the
/// expected arguments and returns 204 on success / 400 on bad input.
/// </summary>
public class AdjudicationControllerReverseClaimTests : IClassFixture<AdjudicationControllerTests.Factory>
{
    private const string TenantId = "test-tenant-001";
    private static readonly Guid PlanId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private readonly AdjudicationControllerTests.Factory _factory;

    public AdjudicationControllerReverseClaimTests(AdjudicationControllerTests.Factory factory) => _factory = factory;

    private HttpClient CreateClient()
    {
        var c = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler());
        c.DefaultRequestHeaders.Add("X-Tenant-ID", TenantId);
        return c;
    }

    [Fact]
    public async Task ReverseClaim_HappyPath_ReturnsNoContentAndCallsEngine()
    {
        var client = CreateClient();
        var body = new ReverseClaimRequest
        {
            MemberId = "m1",
            SubscriberId = "sub-1",
            BenefitPlanId = PlanId,
            ServiceDate = new DateOnly(2026, 5, 1),
            OriginalClaimId = "claim-99",
        };

        var response = await client.PostAsJsonAsync("/api/v1/adjudication/reverse-claim", body);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await _factory.BenefitEngine.Received(1).ReverseClaimAsync(
            "m1", "sub-1", PlanId, new DateOnly(2026, 5, 1), "claim-99", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReverseClaim_MissingMemberId_Returns400()
    {
        var client = CreateClient();
        var body = new ReverseClaimRequest
        {
            MemberId = "",
            SubscriberId = "sub-1",
            BenefitPlanId = PlanId,
            ServiceDate = new DateOnly(2026, 5, 1),
            OriginalClaimId = "claim-99",
        };

        var response = await client.PostAsJsonAsync("/api/v1/adjudication/reverse-claim", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReverseClaim_MissingOriginalClaimId_Returns400()
    {
        var client = CreateClient();
        var body = new ReverseClaimRequest
        {
            MemberId = "m1",
            SubscriberId = "sub-1",
            BenefitPlanId = PlanId,
            ServiceDate = new DateOnly(2026, 5, 1),
            OriginalClaimId = "",
        };

        var response = await client.PostAsJsonAsync("/api/v1/adjudication/reverse-claim", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReverseClaim_EmptyBenefitPlanId_Returns400()
    {
        var client = CreateClient();
        var body = new ReverseClaimRequest
        {
            MemberId = "m1",
            SubscriberId = "sub-1",
            BenefitPlanId = Guid.Empty,
            ServiceDate = new DateOnly(2026, 5, 1),
            OriginalClaimId = "claim-99",
        };

        var response = await client.PostAsJsonAsync("/api/v1/adjudication/reverse-claim", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

/// <summary>
/// <c>POST /api/v1/adjudication/commit-accumulators</c>: claims-service
/// commits a claim's prepared accumulator write once it is finally
/// adjudicated. The controller forwards to
/// <see cref="IBenefitCalculationEngine.CommitAccumulatorsAsync"/> and returns
/// the outcome (string enum on the wire).
/// </summary>
public class AdjudicationControllerCommitAccumulatorsTests : IClassFixture<AdjudicationControllerTests.Factory>
{
    private static readonly Guid PlanId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private readonly AdjudicationControllerTests.Factory _factory;

    public AdjudicationControllerCommitAccumulatorsTests(AdjudicationControllerTests.Factory factory) => _factory = factory;

    /// <summary>claims-service's own service token (the only caller the endpoint accepts).</summary>
    private HttpClient CreateClient(string serviceClientId = "claims-service")
    {
        var c = _factory.CreateDefaultClient();
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(serviceClientId, "test-tenant-001"));
        c.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant-001");
        return c;
    }

    private static object Body(string commitId = "c-1", decimal amount = 60m) => new
    {
        commitId,
        claimId = "claim-7",
        memberId = "m1",
        subscriberId = "",
        benefitPlanId = PlanId,
        planYear = "2026",
        updates = new[]
        {
            new { type = "IndividualDeductible", scope = "Individual", networkTier = "InNetwork", amount, source = "Deductible", clampAtLimit = 500m },
        },
    };

    [Fact]
    public async Task Commit_ForwardsToTheEngine_AndReturnsTheOutcome()
    {
        _factory.BenefitEngine.CommitAccumulatorsAsync(Arg.Any<AccumulatorCommit>(), Arg.Any<CancellationToken>())
            .Returns(new AccumulatorCommitResult
            {
                Outcome = AccumulatorCommitOutcome.Committed,
                Clamped = [new AccumulatorClamp { Type = AccumulatorType.IndividualDeductible, Scope = AccumulatorScope.Individual,
                    NetworkTier = NetworkTier.InNetwork, Requested = 60m, Applied = 20m, Limit = 500m }],
            });

        var response = await CreateClient().PostAsJsonAsync("/api/v1/adjudication/commit-accumulators", Body());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"Committed\"", json);
        Assert.Contains("\"IndividualDeductible\"", json);
        Assert.Contains("\"applied\":20", json);
        await _factory.BenefitEngine.Received(1).CommitAccumulatorsAsync(
            Arg.Is<AccumulatorCommit>(c => c.CommitId == "c-1" && c.ClaimId == "claim-7"
                                           && c.SubscriberId == "m1" // defaults to the member
                                           && c.Updates.Single().Amount == 60m
                                           && c.Updates.Single().ClampAtLimit == 500m
                                           && c.Updates.Single().Type == AccumulatorType.IndividualDeductible),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A user — even an administrator — or another service cannot write accumulators directly.</summary>
    [Theory]
    [InlineData("user")]
    [InlineData("payment-service")]
    public async Task Commit_FromAnyoneButClaimsService_Is403_EngineNotCalled(string caller)
    {
        _factory.BenefitEngine.ClearReceivedCalls();
        HttpClient client;
        if (caller == "user")
        {
            client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler());
            client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant-001");
        }
        else
        {
            client = CreateClient(caller);
        }

        var response = await client.PostAsJsonAsync("/api/v1/adjudication/commit-accumulators", Body());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _factory.BenefitEngine.DidNotReceiveWithAnyArgs().CommitAccumulatorsAsync(default!, default);
    }

    [Fact]
    public async Task Commit_Unauthenticated_Is401()
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant-001");

        var response = await client.PostAsJsonAsync("/api/v1/adjudication/commit-accumulators", Body());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>A commit only adds; a negative amount (a reversal in disguise) is refused.</summary>
    [Fact]
    public async Task Commit_WithANegativeAmount_Is400_EngineNotCalled()
    {
        _factory.BenefitEngine.ClearReceivedCalls();

        var response = await CreateClient().PostAsJsonAsync("/api/v1/adjudication/commit-accumulators", Body(amount: -60m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _factory.BenefitEngine.DidNotReceiveWithAnyArgs().CommitAccumulatorsAsync(default!, default);
    }

    /// <summary>The post-rollout sweep's lookup: claims-service only, read-only, per-claim answer.</summary>
    [Fact]
    public async Task CommitLookup_AnswersPerClaim_ForClaimsServiceOnly()
    {
        var lookup = new[]
        {
            new { claimId = "claim-1", memberId = "m1", subscriberId = "", benefitPlanId = PlanId, planYear = "2026" },
            new { claimId = "claim-2", memberId = "m1", subscriberId = "s1", benefitPlanId = PlanId, planYear = "2026" },
        };
        _factory.Accumulators.HasClaimRecordAsync("m1", "m1", PlanId, "2026", "claim-1", Arg.Any<CancellationToken>()).Returns(true);
        _factory.Accumulators.HasClaimRecordAsync("m1", "s1", PlanId, "2026", "claim-2", Arg.Any<CancellationToken>()).Returns(false);

        var response = await CreateClient().PostAsJsonAsync("/api/v1/adjudication/accumulator-commits/lookup", lookup);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<List<BenefitPlanService.Controllers.AccumulatorCommitLookupResult>>();
        Assert.True(rows!.Single(r => r.ClaimId == "claim-1").Recorded);
        Assert.False(rows!.Single(r => r.ClaimId == "claim-2").Recorded);

        var user = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler());
        user.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant-001");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.PostAsJsonAsync("/api/v1/adjudication/accumulator-commits/lookup", lookup)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await CreateClient().PostAsJsonAsync("/api/v1/adjudication/accumulator-commits/lookup", Array.Empty<object>())).StatusCode);
    }

    [Fact]
    public async Task Commit_WithoutACommitId_Is400_EngineNotCalled()
    {
        _factory.BenefitEngine.ClearReceivedCalls();

        var response = await CreateClient().PostAsJsonAsync("/api/v1/adjudication/commit-accumulators", Body(commitId: ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _factory.BenefitEngine.DidNotReceiveWithAnyArgs().CommitAccumulatorsAsync(default!, default);
    }
    /// <summary>
    /// reverse-claim: terminal by default (void / replacement — older callers send no flag);
    /// terminal=false (a Pay claim moved to Deny before the payment cycle) reverses without
    /// fencing, so the claim can commit again.
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ReverseClaim_TerminalFlag_PicksTheEngineReversal(bool? terminal, bool expectTerminal)
    {
        _factory.BenefitEngine.ClearReceivedCalls();
        object body = terminal is null
            ? new { memberId = "m1", subscriberId = "m1", benefitPlanId = PlanId, serviceDate = "2026-04-15", originalClaimId = "claim-9" }
            : new { memberId = "m1", subscriberId = "m1", benefitPlanId = PlanId, serviceDate = "2026-04-15", originalClaimId = "claim-9", terminal, commitId = "commit-1" };

        _factory.BenefitEngine.ReverseClaimForReprocessingAsync(default!, default!, default, default, default!, default, default)
            .ReturnsForAnyArgs(AccumulatorReversalOutcome.Superseded);

        var response = await CreateClient().PostAsJsonAsync("/api/v1/adjudication/reverse-claim", body);

        Assert.Equal(expectTerminal ? HttpStatusCode.NoContent : HttpStatusCode.OK, response.StatusCode);
        if (!expectTerminal)
            Assert.Contains("Superseded", await response.Content.ReadAsStringAsync());
        if (expectTerminal)
        {
            await _factory.BenefitEngine.Received(1).ReverseClaimAsync("m1", "m1", PlanId, new DateOnly(2026, 4, 15), "claim-9", Arg.Any<CancellationToken>());
            await _factory.BenefitEngine.DidNotReceiveWithAnyArgs().ReverseClaimForReprocessingAsync(default!, default!, default, default, default!, default, default);
        }
        else
        {
            await _factory.BenefitEngine.Received(1).ReverseClaimForReprocessingAsync("m1", "m1", PlanId, new DateOnly(2026, 4, 15), "claim-9", "commit-1", Arg.Any<CancellationToken>());
            await _factory.BenefitEngine.DidNotReceiveWithAnyArgs().ReverseClaimAsync(default!, default!, default, default, default!, default);
        }
    }
}
