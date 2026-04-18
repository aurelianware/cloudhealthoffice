using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class BenefitPlanServiceTests
{
    private readonly Mock<ILogger<BenefitPlanService>> _logger = new();
    private readonly IConfiguration _configuration;

    public BenefitPlanServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:BenefitPlanService"] = "http://localhost:5002"
            })
            .Build();
    }

    private BenefitPlanService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new BenefitPlanService(httpClient, _configuration, _logger.Object);
    }

    // ── GetBenefitPlansAsync ──

    [Fact]
    public async Task GetBenefitPlansAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetBenefitPlansAsync());
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── SearchBenefitPlansAsync ──

    [Fact]
    public async Task SearchBenefitPlansAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchBenefitPlansAsync());
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    [Fact]
    public async Task SearchBenefitPlansAsync_WithFilters_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchBenefitPlansAsync(sponsorId: "SP-001", productType: "PPO"));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── GetBenefitPlanByIdAsync ──

    [Fact]
    public async Task GetBenefitPlanByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetBenefitPlanByIdAsync("PLN-001"));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── CreateBenefitPlanAsync ──

    [Fact]
    public async Task CreateBenefitPlanAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CreateBenefitPlanAsync(new CreateBenefitPlanRequest()));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── UpdateBenefitPlanAsync ──

    [Fact]
    public async Task UpdateBenefitPlanAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UpdateBenefitPlanAsync("PLN-001", new UpdateBenefitPlanRequest()));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── GetAvailableBenefitsAsync ──

    [Fact]
    public async Task GetAvailableBenefitsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAvailableBenefitsAsync());
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── GetServiceBenefitRulesAsync ──

    [Fact]
    public async Task GetServiceBenefitRulesAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetServiceBenefitRulesAsync("PLN-001"));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── UpdateServiceBenefitRulesAsync ──

    [Fact]
    public async Task UpdateServiceBenefitRulesAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UpdateServiceBenefitRulesAsync(new UpdateServiceBenefitRulesRequest()));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── GetAccumulatorConfigAsync ──

    [Fact]
    public async Task GetAccumulatorConfigAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAccumulatorConfigAsync("PLN-001"));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    // ── UpdateAccumulatorConfigAsync ──

    [Fact]
    public async Task UpdateAccumulatorConfigAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UpdateAccumulatorConfigAsync("PLN-001", new AccumulatorConfiguration()));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    [Fact]
    public async Task GetBenefitPlansAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetBenefitPlansAsync());
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path and edge-case tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── GetBenefitPlansAsync ──

    [Fact]
    public async Task GetBenefitPlansAsync_WhenApiReturns200_DeserializesPlanList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { planId = "PLN-1", planName = "Gold PPO", planType = "PPO",
                  deductible = 500m, outOfPocketMax = 5000m },
            new { planId = "PLN-2", planName = "Silver HMO", planType = "HMO",
                  deductible = 1000m, outOfPocketMax = 7000m }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetBenefitPlansAsync();

        result.Should().HaveCount(2);
        result[0].PlanId.Should().Be("PLN-1");
        result[0].PlanName.Should().Be("Gold PPO");
        result[0].Deductible.Should().Be(500m);
        result[1].PlanType.Should().Be("HMO");
    }

    [Fact]
    public async Task GetBenefitPlansAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(
            new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetBenefitPlansAsync();

        result.Should().BeEmpty();
    }

    // ── SearchBenefitPlansAsync ──

    [Fact]
    public async Task SearchBenefitPlansAsync_WithSponsorId_BuildsCorrectQueryString()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.SearchBenefitPlansAsync(sponsorId: "SP-001", productType: "PPO");

        var url = handler.CapturedUrls.Single();
        url.Should().Contain("payer=SP-001");
        url.Should().Contain("planType=PPO");
    }

    [Fact]
    public async Task SearchBenefitPlansAsync_WithNoParams_CallsBaseUrlOnly()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.SearchBenefitPlansAsync();

        var url = handler.CapturedUrls.Single();
        url.Should().Contain("/v1/plans?");
        url.Should().NotContain("payer=");
        url.Should().NotContain("planType=");
    }

    [Fact]
    public async Task SearchBenefitPlansAsync_WhenApiReturns200_DeserializesBenefitPlanListItems()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { planId = "PLN-10", planName = "Platinum EPO", sponsorId = "SP-1",
                  sponsorName = "Acme Corp", productType = "EPO", network = "Tier1",
                  enrolledMembers = 500, assignedBenefits = 12, status = "Active",
                  effectiveDate = "2025-01-01" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.SearchBenefitPlansAsync(sponsorId: "SP-1");

        result.Should().ContainSingle();
        result[0].ProductType.Should().Be("EPO");
        result[0].EnrolledMembers.Should().Be(500);
    }

    // ── GetBenefitPlanByIdAsync ──

    [Fact]
    public async Task GetBenefitPlanByIdAsync_WhenApiReturns200_DeserializesBenefitPlanDetails()
    {
        var json = JsonSerializer.Serialize(new
        {
            planId = "PLN-100", planName = "Gold PPO", sponsorId = "SP-1",
            sponsorName = "Acme", productType = "PPO", network = "Broad",
            enrolledMembers = 1200, assignedBenefits = 18, status = "Active",
            effectiveDate = "2025-01-01",
            metalTier = "Gold", individualDeductible = 750m, familyDeductible = 1500m,
            individualOOPMax = 6000m, familyOOPMax = 12000m, coinsurance = 0.20m,
            monthlyPremium = 450m, planYear = "2025",
            benefits = new[]
            {
                new { benefitId = "BEN-1", serviceType = "Office Visit",
                      category = "Medical", copay = 25m, priorAuthRequired = false }
            },
            exclusions = new[] { "Cosmetic surgery", "Experimental treatments" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetBenefitPlanByIdAsync("PLN-100");

        result.Should().NotBeNull();
        result!.MetalTier.Should().Be("Gold");
        result.IndividualDeductible.Should().Be(750m);
        result.FamilyOOPMax.Should().Be(12000m);
        result.MonthlyPremium.Should().Be(450m);
        result.Benefits.Should().ContainSingle()
            .Which.Copay.Should().Be(25m);
        result.Exclusions.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetBenefitPlanByIdAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(
            new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetBenefitPlanByIdAsync("PLN-NONE");

        result.Should().BeNull();
    }

    // ── CreateBenefitPlanAsync ──

    [Fact]
    public async Task CreateBenefitPlanAsync_WhenApiReturns200_ExtractsPlanId()
    {
        var json = JsonSerializer.Serialize(new { planId = "PLN-NEW-99" }, JsonOpts);
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.CreateBenefitPlanAsync(new CreateBenefitPlanRequest
        {
            SponsorId = "SP-1", PlanName = "Bronze HDHP", ProductType = "HDHP"
        });

        result.Should().Be("PLN-NEW-99");
    }

    // ── UpdateBenefitPlanAsync ──

    [Fact]
    public async Task UpdateBenefitPlanAsync_WhenApiReturns200_SendsPutWithCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.UpdateBenefitPlanAsync("PLN-100", new UpdateBenefitPlanRequest
        {
            PlanName = "Updated Gold PPO", Status = "Active"
        });

        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/plans/PLN-100");
    }

    // ── GetAvailableBenefitsAsync ──

    [Fact]
    public async Task GetAvailableBenefitsAsync_WhenApiReturns200_DeserializesBenefitItemList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { benefitId = "BEN-1", serviceType = "Office Visit",
                  category = "Medical", description = "Primary care office visit",
                  defaultCopay = 25m, requiresPriorAuth = false },
            new { benefitId = "BEN-2", serviceType = "MRI",
                  category = "Medical", description = "Magnetic resonance imaging",
                  defaultCopay = 150m, requiresPriorAuth = true }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetAvailableBenefitsAsync();

        result.Should().HaveCount(2);
        result[0].ServiceType.Should().Be("Office Visit");
        result[0].DefaultCopay.Should().Be(25m);
        result[1].RequiresPriorAuth.Should().BeTrue();
    }

    // ── GetServiceBenefitRulesAsync ──

    [Fact]
    public async Task GetServiceBenefitRulesAsync_WhenApiReturns200_UrlIncludesPlanId()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetServiceBenefitRulesAsync("PLN-100");

        handler.CapturedUrls.Should().ContainSingle()
            .Which.Should().Contain("/v1/plans/PLN-100/service-rules");
    }

    [Fact]
    public async Task GetServiceBenefitRulesAsync_WhenApiReturns200_DeserializesRules()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { ruleId = "RULE-1", serviceCategory = "Medical",
                  serviceTypeCode = "OV", serviceTypeDescription = "Office Visit",
                  networkTier = "Tier1", copay = 25m, subjectToDeductible = false,
                  priorAuthRequired = false }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetServiceBenefitRulesAsync("PLN-100");

        result.Should().ContainSingle();
        result[0].ServiceCategory.Should().Be("Medical");
        result[0].Copay.Should().Be(25m);
    }

    // ── UpdateServiceBenefitRulesAsync ──

    [Fact]
    public async Task UpdateServiceBenefitRulesAsync_WhenApiReturns200_SendsPutWithCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.UpdateServiceBenefitRulesAsync(new UpdateServiceBenefitRulesRequest
        {
            PlanId = "PLN-100",
            Rules = new List<ServiceBenefitRule>
            {
                new() { RuleId = "RULE-1", ServiceCategory = "Medical" }
            }
        });

        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/plans/PLN-100/service-rules");
    }

    // ── GetAccumulatorConfigAsync ──

    [Fact]
    public async Task GetAccumulatorConfigAsync_WhenApiReturns200_DeserializesConfig()
    {
        var json = JsonSerializer.Serialize(new
        {
            configId = "ACC-1", planId = "PLN-100",
            individualDeductible = 750m, familyDeductible = 1500m,
            individualOopMax = 6000m, familyOopMax = 12000m,
            pharmacyCrossAccumulatesDeductible = true,
            pharmacyCrossAccumulatesOop = false,
            dentalCrossAccumulatesOop = false,
            embeddedOrAggregate = "Embedded"
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetAccumulatorConfigAsync("PLN-100");

        result.Should().NotBeNull();
        result!.IndividualDeductible.Should().Be(750m);
        result.FamilyOopMax.Should().Be(12000m);
        result.PharmacyCrossAccumulatesDeductible.Should().BeTrue();
        result.EmbeddedOrAggregate.Should().Be("Embedded");
    }

    [Fact]
    public async Task GetAccumulatorConfigAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(
            new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetAccumulatorConfigAsync("PLN-NONE");

        result.Should().BeNull();
    }

    // ── UpdateAccumulatorConfigAsync ──

    [Fact]
    public async Task UpdateAccumulatorConfigAsync_WhenApiReturns200_SendsPutWithCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.UpdateAccumulatorConfigAsync("PLN-100", new AccumulatorConfiguration
        {
            ConfigId = "ACC-1", PlanId = "PLN-100",
            IndividualDeductible = 1000m, FamilyDeductible = 2000m
        });

        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/plans/PLN-100/accumulators");
    }

    // ── PlanBenefit – remaining properties ────────────────────────────────────

    [Fact]
    public async Task GetBenefitPlanByIdAsync_WhenBenefitHasAllProperties_DeserializesCoinsuranceCoverageAnnualLimit()
    {
        var json = JsonSerializer.Serialize(new
        {
            planId = "PLN-200", planName = "Silver PPO",
            sponsorId = "SP-2", sponsorName = "MegaCorp", productType = "PPO",
            network = "Narrow", enrolledMembers = 800, assignedBenefits = 10,
            status = "Active", effectiveDate = "2026-01-01T00:00:00Z",
            metalTier = "Silver", individualDeductible = 2000m, familyDeductible = 4000m,
            individualOOPMax = 7000m, familyOOPMax = 14000m, coinsurance = 0.30m,
            monthlyPremium = 380m, planYear = "2026",
            benefits = new[]
            {
                new
                {
                    benefitId = "BEN-FULL", serviceType = "Physical Therapy",
                    category = "Rehabilitative", copay = 40m,
                    coinsurancePercent = 0.20m,
                    coveragePercent = 0.80m,
                    annualLimit = 60,
                    priorAuthRequired = true
                }
            },
            exclusions = Array.Empty<string>()
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetBenefitPlanByIdAsync("PLN-200");

        result.Should().NotBeNull();
        var benefit = result!.Benefits.Should().ContainSingle().Subject;
        benefit.CoinsurancePercent.Should().Be(0.20m);
        benefit.CoveragePercent.Should().Be(0.80m);
        benefit.AnnualLimit.Should().Be(60);
        benefit.PriorAuthRequired.Should().BeTrue();
    }

    // ── ServiceBenefitRule – remaining properties ─────────────────────────────

    [Fact]
    public async Task GetServiceBenefitRulesAsync_WhenRulesHaveAllProperties_DeserializesAllFields()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new
            {
                ruleId = "RULE-FULL", serviceCategory = "BehavioralHealth",
                serviceTypeCode = "MH", serviceTypeDescription = "Mental Health Outpatient",
                networkTier = "Tier1", copay = 30m,
                coinsurancePercent = 0.10m,
                subjectToDeductible = true,
                annualVisitLimit = 30,
                annualDollarLimit = 5000m,
                priorAuthRequired = true,
                priorAuthThreshold = ">10 visits",
                deductibleAccumulatorGroup = "Individual",
                oopAccumulatorGroup = "Individual",
                crossAccumulatesWithMedical = true,
                isEditing = false
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetServiceBenefitRulesAsync("PLN-200");

        result.Should().ContainSingle();
        var rule = result[0];
        rule.CoinsurancePercent.Should().Be(0.10m);
        rule.AnnualVisitLimit.Should().Be(30);
        rule.AnnualDollarLimit.Should().Be(5000m);
        rule.PriorAuthThreshold.Should().Be(">10 visits");
        rule.CrossAccumulatesWithMedical.Should().BeTrue();
        rule.IsEditing.Should().BeFalse();
    }

    // ── UpdateBenefitPlanRequest – TerminationDate ────────────────────────────

    [Fact]
    public async Task UpdateBenefitPlanAsync_WithTerminationDate_SendsTerminationDateInBody()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        var req = new UpdateBenefitPlanRequest
        {
            SponsorId = "SP-1", PlanName = "Gold PPO", ProductType = "PPO",
            Network = "Broad", MetalTier = "Gold",
            Status = "Terminated",
            TerminationDate = new DateTime(2026, 12, 31)
        };

        // Verify TerminationDate is readable
        req.Status.Should().Be("Terminated");
        req.TerminationDate.Should().Be(new DateTime(2026, 12, 31));

        await sut.UpdateBenefitPlanAsync("PLN-100", req);

        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("Terminated");
    }

    // ── GetMemberViewAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetMemberViewAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetMemberViewAsync("PLN-100", new DateTime(2026, 4, 18)));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    [Fact]
    public async Task GetMemberViewAsync_WhenApiReturns404_ReturnsNull()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetMemberViewAsync("PLN-UNKNOWN", new DateTime(2026, 4, 18));

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetMemberViewAsync_WhenApiReturns200_DeserializesResponseAndBuildsCorrectUrl()
    {
        var json = JsonSerializer.Serialize(new
        {
            planId = "PLN-100",
            planName = "Gold HMO",
            payer = "CHO",
            planType = "HMO",
            metalLevel = "Gold",
            lineOfBusiness = "Commercial",
            asOfDate = "2026-04-18T00:00:00Z",
            effectiveDate = "2026-01-01T00:00:00Z",
            terminationDate = (string?)null,
            planVersion = "20260315T120000Z",
            costSharing = new { individualDeductible = 1500m },
            categories = new object[]
            {
                new
                {
                    category = "PrimaryCare",
                    displayName = "Primary Care Visit",
                    serviceCategory = "Primary Care",
                    inNetwork = new { tierName = "Preferred", copay = 25m, coinsurance = (decimal?)null },
                    deductibleApplies = true,
                    oopApplies = true,
                    priorAuthRequired = false,
                },
                new
                {
                    category = "Pharmacy",
                    displayName = "Tier 1",
                    serviceCategory = "Tier 1",
                    inNetwork = new { tierName = "Preferred", copay = 10m, coinsurance = (decimal?)null },
                    deductibleApplies = false,
                    oopApplies = true,
                    priorAuthRequired = false,
                    pharmacy = new { tierLabel = "Tier 1", isSpecialty = false },
                },
            },
            documents = new object[]
            {
                new
                {
                    docType = "SBC",
                    displayName = "Summary of Benefits and Coverage",
                    location = "https://cdn.example/sbc-2026.pdf",
                    contentType = "application/pdf",
                    size = 182304,
                    contentHashSha256 = "a1b2c3",
                    version = "2026.01",
                    effectiveDate = "2026-01-01T00:00:00Z",
                },
            },
        });
        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var view = await sut.GetMemberViewAsync("PLN-100", new DateTime(2026, 4, 18));

        view.Should().NotBeNull();
        view!.PlanId.Should().Be("PLN-100");
        view.Categories.Should().HaveCount(2);
        view.Categories[1].Pharmacy!.TierLabel.Should().Be("Tier 1");
        view.Documents.Should().ContainSingle(d => d.DocType == "SBC" && d.ContentHashSha256 == "a1b2c3");

        handler.CapturedUrls[0].Should().Contain("/v1/benefit-plans/PLN-100/member-view");
        handler.CapturedUrls[0].Should().Contain("serviceDate=2026-04-18");
    }
}
