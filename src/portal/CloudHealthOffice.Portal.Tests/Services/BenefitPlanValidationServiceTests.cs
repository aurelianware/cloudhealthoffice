using CloudHealthOffice.Portal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Portal.Tests.Services;

public class BenefitPlanValidationServiceTests
{
    [Fact]
    public async Task ValidateAsync_CompletePublishedPlan_ReturnsReadyResultAndMemberVersion()
    {
        var planService = new Mock<IBenefitPlanService>();
        planService.Setup(service => service.GetMemberViewAsync("PLAN-1", new DateTime(2026, 8, 1)))
            .ReturnsAsync(new MemberBenefitView
            {
                PlanId = "PLAN-1",
                PlanVersion = "v3",
                Categories = [new CategorizedBenefit { Category = "medical" }]
            });
        var sut = CreateService(planService.Object, Environments.Production);

        var result = await sut.ValidateAsync(CompletePlan(), new DateTime(2026, 8, 1));

        result.IsValid.Should().BeTrue();
        result.PlanVersion.Should().Be("v3");
        result.Checks.Should().Contain(check => check.Name == "Member view" && check.Severity == "Success");
        result.Checks.Should().Contain(check => check.Name == "Exclusions" && check.Severity == "Success");
    }

    [Fact]
    public async Task ValidateAsync_MissingRulesAndNetwork_ReturnsActionableErrorsAndExclusionWarning()
    {
        var planService = new Mock<IBenefitPlanService>();
        planService.Setup(service => service.GetMemberViewAsync("PLAN-1", It.IsAny<DateTime>()))
            .ReturnsAsync((MemberBenefitView?)null);
        var plan = CompletePlan();
        plan.Benefits.Clear();
        plan.Exclusions.Clear();
        plan.NetworkTiers.Clear();
        var sut = CreateService(planService.Object, Environments.Production);

        var result = await sut.ValidateAsync(plan, new DateTime(2026, 8, 1));

        result.IsValid.Should().BeFalse();
        result.Checks.Should().Contain(check => check.Name == "Covered benefits" && check.Severity == "Error");
        result.Checks.Should().Contain(check => check.Name == "Network tiers" && check.Severity == "Error");
        result.Checks.Should().Contain(check => check.Name == "Member view" && check.Severity == "Error");
        result.Checks.Should().Contain(check => check.Name == "Exclusions" && check.Severity == "Warning");
    }

    [Fact]
    public void SyntheticClaimsEnabled_IsDevelopmentOrExplicitDemoFeatureOnly()
    {
        var planService = Mock.Of<IBenefitPlanService>();

        CreateService(planService, Environments.Development).SyntheticClaimsEnabled.Should().BeTrue();
        CreateService(planService, Environments.Production).SyntheticClaimsEnabled.Should().BeFalse();
        CreateService(planService, Environments.Production,
            new Dictionary<string, string?> { ["Features:BenefitPlanSyntheticValidationEnabled"] = "true" })
            .SyntheticClaimsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task RunSynthetic837Async_WhenDisabled_StopsBeforeCreatingRecords()
    {
        var handler = new CountingHandler();
        var sut = CreateService(Mock.Of<IBenefitPlanService>(), Environments.Production, handler: handler);

        var action = () => sut.RunSynthetic837Async(CompletePlan(), new SyntheticClaimValidationRequest
        {
            ServiceDate = new DateTime(2026, 8, 1)
        });

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*disabled*");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task RunSynthetic837Async_WithInvalidNpi_StopsBeforeCreatingRecords()
    {
        var handler = new CountingHandler();
        var sut = CreateService(Mock.Of<IBenefitPlanService>(), Environments.Development, handler: handler);

        var action = () => sut.RunSynthetic837Async(CompletePlan(), new SyntheticClaimValidationRequest
        {
            ServiceDate = new DateTime(2026, 8, 1),
            ProviderNpi = "1234567890"
        });

        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*NPI Luhn check*");
        handler.RequestCount.Should().Be(0);
    }

    private static BenefitPlanValidationService CreateService(
        IBenefitPlanService benefitPlans,
        string environmentName,
        Dictionary<string, string?>? settings = null,
        HttpMessageHandler? handler = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Services:MemberService"] = "http://member-service/api/v1",
            ["Services:CoverageService"] = "http://coverage-service/api",
            ["Services:ClaimsService"] = "http://claims-service/api",
            ["Services:ProviderService"] = "http://provider-service/api"
        };
        if (settings != null)
            foreach (var pair in settings) values[pair.Key] = pair.Value;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(item => item.EnvironmentName).Returns(environmentName);
        var tenantContext = new Mock<ITenantContextService>();
        tenantContext.Setup(context => context.GetTenantIdAsync()).ReturnsAsync("tenant-test");
        return new BenefitPlanValidationService(
            new HttpClient(handler ?? new CountingHandler()),
            configuration,
            environment.Object,
            benefitPlans,
            Mock.Of<IClaimsService>(),
            tenantContext.Object,
            Mock.Of<ILogger<BenefitPlanValidationService>>());
    }

    private static BenefitPlanDetails CompletePlan() => new()
    {
        PlanId = "PLAN-1",
        PlanName = "Complete PPO",
        Status = "Active",
        VersionState = "Published",
        VersionNumber = 3,
        EffectiveDate = new DateTime(2026, 1, 1),
        Benefits = [new PlanBenefit { BenefitId = "BEN-1", IsCovered = true }],
        Exclusions = [new PlanBenefit { BenefitId = "EX-1", IsCovered = false }],
        NetworkTiers = [new PlanNetworkTier { TierName = "Preferred", NetworkId = "CHO-PREMIER" }]
    };

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
