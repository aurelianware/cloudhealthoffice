using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using FluentAssertions;
using FhirService.Models;
using FhirService.Services;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace CloudHealthOffice.FhirService.Tests.Services;

public class PasAutoAdjudicatorTests
{
    private readonly PasAutoAdjudicationConfig _config;
    private readonly Mock<ILogger<PasAutoAdjudicator>> _loggerMock;
    private readonly Mock<HttpMessageHandler> _httpHandlerMock;

    public PasAutoAdjudicatorTests()
    {
        _config = new PasAutoAdjudicationConfig
        {
            Enabled = true,
            AutoApproveServiceTypes = new List<string> { "99213", "99214", "80053" },
            AutoDenyServiceTypes = new List<string> { "V2020", "S9970" },
            GoldCardThreshold = 0.95,
            DollarThreshold = 500.00m,
            MaxResponseMs = 12000,
        };
        _loggerMock = new Mock<ILogger<PasAutoAdjudicator>>();
        _httpHandlerMock = new Mock<HttpMessageHandler>();
    }

    [Fact]
    public async System.Threading.Tasks.Task TryDecide_AutoApproveListMatch_ReturnsApproved()
    {
        var adjudicator = CreateAdjudicator();
        var claim = CreateClaim("99213");

        var result = await adjudicator.TryDecideAsync(claim, new Bundle(), 12000);

        result.HasDecision.Should().BeTrue();
        result.Decision.Should().Be("approved");
        result.RuleName.Should().Be("AutoApproveList");
        result.AuthorizationNumber.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async System.Threading.Tasks.Task TryDecide_AutoDenyListMatch_ReturnsDenied()
    {
        var adjudicator = CreateAdjudicator();
        var claim = CreateClaim("V2020");

        var result = await adjudicator.TryDecideAsync(claim, new Bundle(), 12000);

        result.HasDecision.Should().BeTrue();
        result.Decision.Should().Be("denied");
        result.RuleName.Should().Be("AutoDenyList");
        result.DenialReasonCode.Should().Be("NOT_COVERED");
    }

    [Fact]
    public async System.Threading.Tasks.Task TryDecide_GoldCardProvider_ReturnsApproved()
    {
        // Setup HTTP mock to return high approval rate
        SetupAuthServiceResponse(new { TotalAuthorizations = 100, ApprovalRate = 98.0m });

        var adjudicator = CreateAdjudicator();
        var claim = CreateClaim("12345"); // not in approve/deny lists
        claim.Provider = new ResourceReference { Identifier = new Identifier { Value = "1234567890" } };

        var result = await adjudicator.TryDecideAsync(claim, new Bundle(), 12000);

        result.HasDecision.Should().BeTrue();
        result.Decision.Should().Be("approved");
        result.RuleName.Should().Be("GoldCardProvider");
    }

    [Fact]
    public async System.Threading.Tasks.Task TryDecide_BelowDollarThreshold_ReturnsApproved()
    {
        // Setup HTTP mock to return low approval rate (non gold-card)
        SetupAuthServiceResponse(new { TotalAuthorizations = 100, ApprovalRate = 50.0m });

        var adjudicator = CreateAdjudicator();
        var claim = CreateClaim("12345"); // not in approve/deny lists
        claim.Provider = new ResourceReference { Identifier = new Identifier { Value = "9999999999" } };
        claim.Total = new Money { Value = 200m, Currency = Money.Currencies.USD };

        var result = await adjudicator.TryDecideAsync(claim, new Bundle(), 12000);

        result.HasDecision.Should().BeTrue();
        result.Decision.Should().Be("approved");
        result.RuleName.Should().Be("DollarThreshold");
    }

    [Fact]
    public async System.Threading.Tasks.Task TryDecide_NoRuleMatch_ReturnsPended()
    {
        // Setup HTTP mock to return low approval rate
        SetupAuthServiceResponse(new { TotalAuthorizations = 100, ApprovalRate = 50.0m });

        var adjudicator = CreateAdjudicator();
        var claim = CreateClaim("12345"); // not in any list
        claim.Provider = new ResourceReference { Identifier = new Identifier { Value = "9999999999" } };
        claim.Total = new Money { Value = 10000m, Currency = Money.Currencies.USD }; // above threshold

        var result = await adjudicator.TryDecideAsync(claim, new Bundle(), 12000);

        result.HasDecision.Should().BeFalse();
        result.RuleName.Should().Be("NoRuleMatch");
    }

    [Fact]
    public async System.Threading.Tasks.Task TryDecide_TimeBudgetExceeded_ReturnsPended()
    {
        // Setup HTTP mock that delays response
        _httpHandlerMock
            .Protected()
            .Setup<System.Threading.Tasks.Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage _, CancellationToken ct) =>
            {
                await System.Threading.Tasks.Task.Delay(5000, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var adjudicator = CreateAdjudicator();
        var claim = CreateClaim("12345");
        claim.Provider = new ResourceReference { Identifier = new Identifier { Value = "1234567890" } };

        // Very short timeout — should cancel during gold-card HTTP call
        var result = await adjudicator.TryDecideAsync(claim, new Bundle(), 50);

        result.HasDecision.Should().BeFalse();
        result.RuleName.Should().Be("TimeBudgetExceeded");
    }

    [Fact]
    public async System.Threading.Tasks.Task TryDecide_DisabledConfig_AlwaysPends()
    {
        _config.Enabled = false;
        var adjudicator = CreateAdjudicator();
        var claim = CreateClaim("99213"); // would normally auto-approve

        var result = await adjudicator.TryDecideAsync(claim, new Bundle(), 12000);

        result.HasDecision.Should().BeFalse();
        result.RuleName.Should().Be("ConfigDisabled");
    }

    private PasAutoAdjudicator CreateAdjudicator()
    {
        var options = Options.Create(_config);
        var httpClient = new HttpClient(_httpHandlerMock.Object)
        {
            BaseAddress = new Uri("http://authorization-service.test/"),
        };
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("AuthorizationService")).Returns(httpClient);

        var enrollmentGateMock = new Mock<IEnrollmentDecisionGate>();
        enrollmentGateMock
            .Setup(g => g.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateOnly>(), It.IsAny<LineOfBusiness>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateResult.Pass());

        var ruleEngineMock = new Mock<IPriorAuthRuleEngine>();
        ruleEngineMock
            .Setup(r => r.EvaluateAsync(It.IsAny<PaRuleContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Pend,
                FiringRuleId = "NoRuleMatch",
                FiringRuleName = "NoRuleMatch",
                ResolvedRuleSetKey = "platform/TX/Medicaid/any"
            });

        return new PasAutoAdjudicator(options, factoryMock.Object, enrollmentGateMock.Object, ruleEngineMock.Object, _loggerMock.Object);
    }

    private void SetupAuthServiceResponse(object responseBody)
    {
        _httpHandlerMock
            .Protected()
            .Setup<System.Threading.Tasks.Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = JsonContent.Create(responseBody),
            });
    }

    private static Claim CreateClaim(string procedureCode) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Status = FinancialResourceStatusCodes.Active,
        Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
        Use = ClaimUseCode.Preauthorization,
        Patient = new ResourceReference("Patient/pat-001"),
        Created = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        Insurer = new ResourceReference("Organization/cho-payer"),
        Provider = new ResourceReference("Practitioner/1234567890"),
        Priority = new CodeableConcept("http://terminology.hl7.org/CodeSystem/processpriority", "normal"),
        Insurance = new List<Claim.InsuranceComponent>
        {
            new() { Sequence = 1, Focal = true, Coverage = new ResourceReference("Coverage/cov-001") }
        },
        Item = new List<Claim.ItemComponent>
        {
            new()
            {
                Sequence = 1,
                ProductOrService = new CodeableConcept(
                    "http://www.ama-assn.org/go/cpt", procedureCode),
                UnitPrice = new Money { Value = 150m, Currency = Money.Currencies.USD },
                Quantity = new Quantity { Value = 1 },
            }
        },
    };
}
