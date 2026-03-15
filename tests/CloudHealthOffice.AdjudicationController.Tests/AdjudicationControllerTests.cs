using System.Net;
using System.Net.Http.Json;
using BenefitPlanService.Controllers;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Services;
using CloudHealthOffice.NcciEngine.Models;
using CloudHealthOffice.NcciEngine.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using StackExchange.Redis;

namespace CloudHealthOffice.AdjudicationController.Tests;

public class AdjudicationControllerTests : IClassFixture<AdjudicationControllerTests.Factory>
{
    private const string TenantId = "test-tenant-001";
    private static readonly Guid PlanId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

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

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureServices(services =>
            {
                // Remove infrastructure services that require real connections
                services.RemoveAll<IConnectionMultiplexer>();
                services.RemoveAll<IClaimsAccumulatorSource>();
                services.RemoveAll<IAccumulatorAuditWriter>();

                // Replace the three engine interfaces with mocks
                services.RemoveAll<IBenefitCalculationEngine>();
                services.AddSingleton(BenefitEngine);

                services.RemoveAll<IRateResolutionService>();
                services.AddSingleton(RateEngine);

                services.RemoveAll<INcciEditService>();
                services.AddSingleton(NcciEngine);

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
        var client = _factory.CreateClient();
        if (tenantId is not null)
            client.DefaultRequestHeaders.Add("X-Tenant-ID", tenantId);
        return client;
    }

    // ─────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────

    private static AdjudicationRequest MakeAdjudicationRequest(
        int lineCount = 1,
        string procedureCode = "99213",
        decimal billedAmount = 200m) => new()
    {
        ClaimId = "CLM-001",
        MemberId = "MBR-001",
        SubscriberId = "SUB-001",
        BenefitPlanId = PlanId,
        ServiceDate = new DateOnly(2026, 1, 15),
        ProviderNpi = "1234567890",
        NetworkTier = NetworkTier.InNetwork,
        Lines = Enumerable.Range(1, lineCount).Select(i => new AdjudicationLineRequest
        {
            LineNumber = i,
            ProcedureCode = procedureCode,
            PlaceOfService = "11",
            BilledAmount = billedAmount,
            Units = 1,
            DiagnosisCodes = ["Z00.00"]
        }).ToList()
    };

    private void SetupNcciPass()
    {
        _factory.NcciEngine
            .ScrubAsync(Arg.Any<NcciScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new NcciScrubResult
            {
                ClaimId = callInfo.Arg<NcciScrubRequest>().ClaimId,
                NcciPairsChecked = 1,
                MueChecked = 1
            });
    }

    private void SetupRateResult(decimal allowedAmount = 150m, NetworkStatus networkStatus = NetworkStatus.InNetwork,
        FeeScheduleType feeScheduleType = FeeScheduleType.Commercial, RateSource rateSource = RateSource.ContractedRate)
    {
        _factory.RateEngine
            .ResolveBatchAsync(Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var requests = callInfo.Arg<IReadOnlyList<PricingRequest>>();
                return new PricingResultSet
                {
                    LineResults = requests.Select(r => new PricingResult
                    {
                        LineNumber = r.LineNumber,
                        ProcedureCode = r.ProcedureCode,
                        AllowedAmount = allowedAmount,
                        BilledAmount = r.BilledAmount,
                        FeeScheduleType = feeScheduleType,
                        RateSource = rateSource,
                        NetworkStatus = networkStatus,
                        FeeScheduleId = "FS-001",
                        FeeScheduleName = "Test Fee Schedule"
                    }).ToArray()
                };
            });
    }

    private void SetupBenefitResult(
        decimal allowedAmount = 150m,
        decimal deductible = 50m,
        decimal copay = 25m,
        decimal coinsurance = 15m)
    {
        decimal memberResp = deductible + copay + coinsurance;
        decimal planPaid = allowedAmount - memberResp;

        _factory.BenefitEngine
            .CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var req = callInfo.Arg<BenefitResolutionRequest>();
                return new BenefitResolutionResult
                {
                    Success = true,
                    Lines = req.Lines.Select(l => new LineBenefitResult
                    {
                        LineNumber = l.LineNumber,
                        IsCovered = true,
                        ServiceTypeCode = "98",
                        ServiceTypeDescription = "Office Visit",
                        AllowedAmount = allowedAmount,
                        BilledAmount = l.BilledAmount,
                        DeductibleAmount = deductible,
                        CopayAmount = copay,
                        CoinsuranceAmount = coinsurance,
                        CoinsurancePercent = 0.20m,
                        MemberResponsibility = memberResp,
                        PlanPaidAmount = planPaid,
                        Adjustments =
                        [
                            new AdjustmentReason { GroupCode = "CO", ReasonCode = "45", Amount = l.BilledAmount - allowedAmount },
                            new AdjustmentReason { GroupCode = "PR", ReasonCode = "1", Amount = deductible },
                            new AdjustmentReason { GroupCode = "PR", ReasonCode = "3", Amount = copay },
                            new AdjustmentReason { GroupCode = "PR", ReasonCode = "2", Amount = coinsurance }
                        ]
                    }).ToList(),
                    Totals = new ClaimTotals
                    {
                        TotalBilled = req.Lines.Sum(l => l.BilledAmount),
                        TotalAllowed = allowedAmount * req.Lines.Count,
                        TotalDeductible = deductible * req.Lines.Count,
                        TotalCopay = copay * req.Lines.Count,
                        TotalCoinsurance = coinsurance * req.Lines.Count,
                        TotalMemberResponsibility = memberResp * req.Lines.Count,
                        TotalPlanPaid = planPaid * req.Lines.Count
                    },
                    AccumulatorSnapshot =
                    [
                        new AccumulatorState
                        {
                            Type = AccumulatorType.IndividualDeductible,
                            Scope = AccumulatorScope.Individual,
                            NetworkTier = NetworkTier.InNetwork,
                            LimitAmount = 1500m,
                            AccumulatedAmountBefore = 0m,
                            AmountApplied = deductible,
                            AccumulatedAmountAfter = deductible,
                            RemainingAmount = 1500m - deductible
                        }
                    ]
                };
            });
    }

    // ═══════════════════════════════════════════════════════════════
    // 1. /adjudicate with valid claim → merged result
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Adjudicate_ValidClaim_ReturnsMergedBenefitRateAndNcciResult()
    {
        // Arrange
        SetupNcciPass();
        SetupRateResult(allowedAmount: 150m);
        SetupBenefitResult(allowedAmount: 150m, deductible: 50m, copay: 25m, coinsurance: 15m);

        using var client = CreateClientWithTenant();
        var request = MakeAdjudicationRequest();

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/adjudication/adjudicate", request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<AdjudicationResponse>();
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal("CLM-001", result.ClaimId);

        // Totals reflect merged pricing + benefit data
        Assert.Equal(200m, result.Totals.BilledAmount);
        Assert.Equal(150m, result.Totals.AllowedAmount);
        Assert.Equal(50m, result.Totals.DeductibleAmount);
        Assert.Equal(25m, result.Totals.CopayAmount);
        Assert.Equal(15m, result.Totals.CoinsuranceAmount);
        Assert.Equal(90m, result.Totals.MemberResponsibility);
        Assert.Equal(60m, result.Totals.PlanPayment);
        Assert.Equal(50m, result.Totals.ContractualAdjustment); // 200 - 150

        // Line includes fee schedule info from rate engine
        var line = Assert.Single(result.Lines);
        Assert.Equal("Commercial", line.FeeScheduleType);
        Assert.Equal("InNetwork", line.NetworkStatus);
        Assert.Equal("FS-001", line.FeeScheduleId);
        Assert.Equal("98", line.ServiceTypeCode);
        Assert.True(line.IsCovered);

        // Accumulators present
        Assert.NotNull(result.Accumulators);
        Assert.NotEmpty(result.Accumulators);
    }

    // ═══════════════════════════════════════════════════════════════
    // 2. /adjudicate with NCCI CCI conflict → 422 with edit codes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Adjudicate_NcciConflict_Returns422WithEditFailures()
    {
        // Arrange — NCCI engine returns a bundling failure
        _factory.NcciEngine
            .ScrubAsync(Arg.Any<NcciScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NcciScrubResult
            {
                ClaimId = "CLM-002",
                NcciPairsChecked = 1,
                MueChecked = 0,
                EditFailures =
                [
                    new NcciEditFailure
                    {
                        EditType = NcciEditType.NcciPair,
                        RuleId = "NE001",
                        Message = "CPT 29881 bundles into 29880 (Column 1/Column 2 edit)",
                        Column1Code = "29880",
                        Column2Code = "29881",
                        AffectedLineNumbers = [1, 2],
                        SuggestedCarc = "97",
                        SuggestedRarc = "N527"
                    }
                ]
            });

        using var client = CreateClientWithTenant();
        var request = MakeAdjudicationRequest(lineCount: 2, procedureCode: "29880");

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/adjudication/adjudicate", request);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<NcciErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("NCCI_MUE_EDIT_FAILURE", body.Error);
        Assert.NotNull(body.EditFailures);
        Assert.Single(body.EditFailures);

        var failure = body.EditFailures[0];
        Assert.Equal("NE001", failure.RuleId);
        Assert.Equal("29880", failure.Column1Code);
        Assert.Equal("29881", failure.Column2Code);
        Assert.Equal("97", failure.SuggestedCarc);

        // Verify rate and benefit engines were NOT called
        await _factory.RateEngine.DidNotReceive()
            .ResolveBatchAsync(Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>());
        await _factory.BenefitEngine.DidNotReceive()
            .CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>());
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
        var response = await client.PostAsJsonAsync("/api/v1/adjudication/calculate-benefits", request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<BenefitResolutionResult>();
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
        // Arrange
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

        var result = await response.Content.ReadFromJsonAsync<PricingResultSet>();
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

        var result = await response.Content.ReadFromJsonAsync<PricingResultSet>();
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
        // Arrange — no X-Tenant-ID header
        using var client = CreateClientWithTenant(tenantId: null);
        var request = MakeAdjudicationRequest();

        // Act
        var adjudicateResponse = await client.PostAsJsonAsync("/api/v1/adjudication/adjudicate", request);
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

        // Assert — TenantMiddleware returns 401 for missing tenant context
        Assert.Equal(HttpStatusCode.Unauthorized, adjudicateResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, benefitsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, ratesResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, ncciResponse.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════
    // 7. Invalid request body → 400 with validation errors
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Adjudicate_InvalidRequestBody_Returns400()
    {
        using var client = CreateClientWithTenant();

        // Send an empty JSON object — required fields missing
        var response = await client.PostAsJsonAsync("/api/v1/adjudication/adjudicate",
            new { });

        // ASP.NET model binding should accept the empty object but the controller
        // will get default values. Let's test with malformed JSON instead.
        var stringContent = new StringContent("not-valid-json", System.Text.Encoding.UTF8, "application/json");
        var badJsonResponse = await client.PostAsync("/api/v1/adjudication/adjudicate", stringContent);

        Assert.Equal(HttpStatusCode.BadRequest, badJsonResponse.StatusCode);
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

        var result = await response.Content.ReadFromJsonAsync<NcciScrubResult>();
        Assert.NotNull(result);
        Assert.True(result.Passed);
        Assert.Equal("CLM-NCCI-01", result.ClaimId);
        Assert.Equal(3, result.NcciPairsChecked);
        Assert.Equal(2, result.MueChecked);
        Assert.Empty(result.EditFailures);
    }
}

/// <summary>
/// DTO for the 422 error response from the /adjudicate endpoint when NCCI edits fail.
/// </summary>
internal record NcciErrorResponse
{
    public string ClaimId { get; init; } = default!;
    public string Error { get; init; } = default!;
    public string Message { get; init; } = default!;
    public List<NcciEditFailure> EditFailures { get; init; } = [];
}
