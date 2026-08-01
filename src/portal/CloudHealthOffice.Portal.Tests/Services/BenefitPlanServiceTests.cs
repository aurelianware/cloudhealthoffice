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

    [Fact]
    public async Task AddBenefitAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.AddBenefitAsync("PLN-001", new UpsertPlanBenefitRequest()));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    [Fact]
    public async Task UpdateBenefitAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UpdateBenefitAsync("PLN-001", "BEN-1", new UpsertPlanBenefitRequest()));
        ex.ServiceName.Should().Be("Benefit Plan Service");
    }

    [Fact]
    public async Task ReplaceNetworkTiersAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            sut.ReplaceNetworkTiersAsync("PLN-001", Array.Empty<PlanNetworkTier>()));
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
            new
            {
                planId = "PLN-10", planName = "Platinum EPO", payer = "Acme Corp",
                planType = "EPO", metalLevel = "Platinum", isActive = true,
                versionState = "Published", effectiveDate = "2025-01-01",
                networkTiers = new[] { new { tierName = "Preferred", tierLevel = 1, networkId = "Tier1" } },
                benefits = Enumerable.Range(1, 12).Select(index => new
                {
                    id = $"BEN-{index}", benefitType = "medical",
                    serviceCategory = index.ToString(), description = $"Benefit {index}"
                }),
                costSharing = new { monthlyPremium = 475m }
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.SearchBenefitPlansAsync(sponsorId: "SP-1");

        result.Should().ContainSingle();
        result[0].ProductType.Should().Be("EPO");
        result[0].SponsorName.Should().Be("Acme Corp");
        result[0].Network.Should().Be("Tier1");
        result[0].AssignedBenefits.Should().Be(12);
        result[0].MonthlyPremium.Should().Be(475m);
        result[0].Status.Should().Be("Active");
    }

    // ── GetBenefitPlanByIdAsync ──

    [Fact]
    public async Task GetBenefitPlanByIdAsync_WhenApiReturns200_DeserializesBenefitPlanDetails()
    {
        var json = JsonSerializer.Serialize(new
        {
            planId = "PLN-100", planName = "Gold PPO", payer = "Acme",
            planType = "PPO", metalLevel = "Gold", isActive = true,
            versionState = "Published", effectiveDate = "2025-01-01",
            networkTiers = new[] { new { tierName = "Broad", tierLevel = 1, networkId = "BROAD-1" } },
            costSharing = new
            {
                individualDeductible = 750m, familyDeductible = 1500m,
                individualOutOfPocketMax = 6000m, familyOutOfPocketMax = 12000m,
                coinsurance = 20m, monthlyPremium = 450m
            },
            benefits = new object[]
            {
                new
                {
                    id = "BEN-1", benefitType = "medical", serviceCategory = "98",
                    description = "Office Visit", inNetworkCopay = 25m,
                    priorAuthRequired = false
                },
                new
                {
                    id = "EXC-1", benefitType = "medical", serviceCategory = "COSMETIC",
                    description = "Cosmetic Procedures", isCovered = false,
                    cptCodes = new[] { "15819", "15820" }
                }
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetBenefitPlanByIdAsync("PLN-100");

        result.Should().NotBeNull();
        result!.MetalTier.Should().Be("Gold");
        result.SponsorName.Should().Be("Acme");
        result.ProductType.Should().Be("PPO");
        result.Network.Should().Be("BROAD-1");
        result.NetworkTiers.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            TierName = "Broad", TierLevel = 1, NetworkId = "BROAD-1"
        }, options => options.ExcludingMissingMembers());
        result.IndividualDeductible.Should().Be(750m);
        result.FamilyOOPMax.Should().Be(12000m);
        result.MonthlyPremium.Should().Be(450m);
        result.Coinsurance.Should().Be(20m);
        result.PlanYear.Should().Be("2025");
        result.AssignedBenefits.Should().Be(1);
        result.Benefits.Should().ContainSingle()
            .Which.Copay.Should().Be(25m);
        result.Exclusions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                BenefitId = "EXC-1",
                ServiceCategory = "COSMETIC",
                Description = "Cosmetic Procedures",
                IsCovered = false,
                CptCodes = new[] { "15819", "15820" },
            }, options => options.ExcludingMissingMembers());
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

    [Fact]
    public async Task AddBenefitAsync_sends_version_safe_service_payload()
    {
        string? capturedBody = null;
        var handler = new FakeHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{}"),
            };
        });
        var sut = CreateService(new HttpClient(handler));

        await sut.AddBenefitAsync("PLN/100", new UpsertPlanBenefitRequest
        {
            BenefitType = "medical",
            ServiceCategory = "98",
            Description = "Professional Office Visit",
            CptCodes = ["99213", "99214"],
            InNetworkCopay = 30m,
            OutNetworkCoinsurancePercent = 40m,
            DeductibleApplies = false,
            OopApplies = true,
        });

        var captured = handler.CapturedRequests.Should().ContainSingle().Subject;
        captured.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsoluteUri.Should().Contain("/v1/plans/PLN%2F100/benefits");
        using var json = JsonDocument.Parse(capturedBody!);
        json.RootElement.GetProperty("inNetworkCopay").GetDecimal().Should().Be(30m);
        json.RootElement.GetProperty("outNetworkCoinsurance").GetDecimal().Should().Be(0.4m);
        json.RootElement.GetProperty("deductibleApplies").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("isCovered").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task UpdateBenefitAsync_sends_put_to_escaped_rule_url()
    {
        string? capturedBody = null;
        var handler = new FakeHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
        });
        var sut = CreateService(new HttpClient(handler));

        await sut.UpdateBenefitAsync("PLN-100", "BEN/1", new UpsertPlanBenefitRequest
        {
            ServiceCategory = "73",
            Description = "Diagnostic Lab",
            InNetworkCoinsurancePercent = 20m,
        });

        var captured = handler.CapturedRequests.Should().ContainSingle().Subject;
        captured.Method.Should().Be(HttpMethod.Put);
        captured.RequestUri!.AbsoluteUri.Should().EndWith("/v1/plans/PLN-100/benefits/BEN%2F1");
        using var json = JsonDocument.Parse(capturedBody!);
        json.RootElement.GetProperty("inNetworkCoinsurance").GetDecimal().Should().Be(0.2m);
    }

    [Fact]
    public async Task AddBenefitAsync_sends_explicit_exclusion_payload()
    {
        string? capturedBody = null;
        var handler = new FakeHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{}"),
            };
        });
        var sut = CreateService(new HttpClient(handler));

        await sut.AddBenefitAsync("PLN-100", new UpsertPlanBenefitRequest
        {
            BenefitType = "medical",
            ServiceCategory = "COSMETIC",
            Description = "Cosmetic Procedures",
            IsCovered = false,
            CptCodes = ["15819", "15820"],
        });

        using var json = JsonDocument.Parse(capturedBody!);
        json.RootElement.GetProperty("isCovered").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("serviceCategory").GetString().Should().Be("COSMETIC");
        json.RootElement.GetProperty("cptCodes").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task ReplaceNetworkTiersAsync_sends_complete_set_to_escaped_plan_url()
    {
        string? capturedBody = null;
        var handler = new FakeHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        });
        var sut = CreateService(new HttpClient(handler));

        await sut.ReplaceNetworkTiersAsync("PLN/100", new[]
        {
            new PlanNetworkTier { Id = "tier-1", TierName = " Preferred ", TierLevel = 1, NetworkId = " NET-A " },
            new PlanNetworkTier { Id = "tier-2", TierName = "Extended", TierLevel = 2, NetworkId = "NET-B" },
        });

        var request = handler.CapturedRequests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Put);
        request.RequestUri!.AbsoluteUri.Should().EndWith("/v1/plans/PLN%2F100/network-tiers");
        using var json = JsonDocument.Parse(capturedBody!);
        json.RootElement.GetArrayLength().Should().Be(2);
        json.RootElement[0].GetProperty("tierName").GetString().Should().Be("Preferred");
        json.RootElement[0].GetProperty("networkId").GetString().Should().Be("NET-A");
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
            planId = "PLN-200", planName = "Silver PPO", payer = "MegaCorp",
            planType = "PPO", metalLevel = "Silver", isActive = true,
            versionState = "Published", effectiveDate = "2026-01-01T00:00:00Z",
            networkTiers = new[] { new { tierName = "Narrow", tierLevel = 1, networkId = "NARROW-1" } },
            costSharing = new
            {
                individualDeductible = 2000m, familyDeductible = 4000m,
                individualOutOfPocketMax = 7000m, familyOutOfPocketMax = 14000m,
                coinsurance = 30m, monthlyPremium = 380m
            },
            benefits = new[]
            {
                new
                {
                    id = "BEN-FULL", benefitType = "medical", serviceCategory = "PT",
                    description = "Physical Therapy", inNetworkCopay = 40m,
                    inNetworkCoinsurance = 0.20m, visitLimit = 60,
                    priorAuthRequired = true
                }
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetBenefitPlanByIdAsync("PLN-200");

        result.Should().NotBeNull();
        var benefit = result!.Benefits.Should().ContainSingle().Subject;
        benefit.ServiceType.Should().Be("Physical Therapy");
        benefit.Category.Should().Be("Medical");
        benefit.CoinsurancePercent.Should().Be(20m);
        benefit.CoveragePercent.Should().Be(80m);
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
                    contentHashSha256 = "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=",
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
        view.Documents.Should().ContainSingle(d => d.DocType == "SBC" && d.ContentHashSha256 == "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=");

        handler.CapturedUrls[0].Should().Contain("/v1/benefit-plans/PLN-100/member-view");
        handler.CapturedUrls[0].Should().Contain("serviceDate=2026-04-18");
    }
}
