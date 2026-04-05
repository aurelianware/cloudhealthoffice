using System.Net;
using FhirService.Controllers;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using FluentAssertions;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

public class CrdControllerTests
{
    private readonly Mock<ICrdService> _crdServiceMock;
    private readonly Mock<ILogger<CrdController>> _loggerMock;
    private readonly CrdController _controller;

    public CrdControllerTests()
    {
        _crdServiceMock = new Mock<ICrdService>();
        _loggerMock = new Mock<ILogger<CrdController>>();

        var config = Options.Create(new CrdConfig
        {
            Enabled = true,
            AuthRequiredCodes = new List<string> { "27447" },
            AutoApprovedCodes = new List<string> { "99213" },
            DocumentationRequiredCodes = new List<string> { "72148" },
        });

        _controller = new CrdController(
            _crdServiceMock.Object,
            config,
            _loggerMock.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = "test-tenant";
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
    }

    // ── Discovery ────────────────────────────────────────────────────────────

    [Fact]
    public void Discovery_ReturnsServiceList()
    {
        var result = _controller.Discovery();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var discovery = okResult.Value.Should().BeOfType<CrdDiscoveryResponse>().Subject;
        discovery.Services.Should().HaveCount(2);
        discovery.Services.Should().Contain(s => s.Id == "cho-order-select" && s.Hook == "order-select");
        discovery.Services.Should().Contain(s => s.Id == "cho-order-sign" && s.Hook == "order-sign");
    }

    // ── Hook execution: valid requests ───────────────────────────────────────

    [Fact]
    public async Task OrderSelect_ValidRequest_ReturnsCards()
    {
        _crdServiceMock
            .Setup(s => s.EvaluateCoverageRequirementsAsync(
                It.IsAny<CrdHookRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CrdEvaluationResult
            {
                Cards = new List<CrdCard> { new() { Summary = "Test card", Indicator = "info" } },
                CodesEvaluated = 1,
            });

        var request = CreateValidHookRequest("order-select");
        var result = await _controller.ExecuteHook("cho-order-select", request, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var cardResponse = okResult.Value.Should().BeOfType<CrdCardResponse>().Subject;
        cardResponse.Cards.Should().HaveCount(1);
    }

    [Fact]
    public async Task OrderSign_ValidRequest_ReturnsCards()
    {
        _crdServiceMock
            .Setup(s => s.EvaluateCoverageRequirementsAsync(
                It.IsAny<CrdHookRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CrdEvaluationResult
            {
                Cards = new List<CrdCard> { new() { Summary = "Sign card", Indicator = "warning" } },
                CodesEvaluated = 1,
            });

        var request = CreateValidHookRequest("order-sign");
        var result = await _controller.ExecuteHook("cho-order-sign", request, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var cardResponse = okResult.Value.Should().BeOfType<CrdCardResponse>().Subject;
        cardResponse.Cards.Should().HaveCount(1);
    }

    // ── Validation errors ────────────────────────────────────────────────────

    [Fact]
    public async Task OrderSelect_MissingHookInstance_Returns400()
    {
        var request = CreateValidHookRequest("order-select");
        request.HookInstance = "";

        var result = await _controller.ExecuteHook("cho-order-select", request, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task OrderSelect_MismatchedHookType_Returns400()
    {
        var request = CreateValidHookRequest("order-sign"); // Hook type says order-sign
        var result = await _controller.ExecuteHook("cho-order-select", request, CancellationToken.None); // URL says order-select

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task UnknownHookId_Returns404()
    {
        var request = CreateValidHookRequest("unknown-hook");
        var result = await _controller.ExecuteHook("unknown-hook", request, CancellationToken.None);

        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.StatusCode.Should().Be(404);
    }

    // ── Terminology translation verification ─────────────────────────────────

    [Fact]
    public async Task OrderSelect_SnomedCode_CallsTerminologyService()
    {
        CrdHookRequest? capturedRequest = null;
        _crdServiceMock
            .Setup(s => s.EvaluateCoverageRequirementsAsync(
                It.IsAny<CrdHookRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<CrdHookRequest, string, CancellationToken>((req, _, _) => capturedRequest = req)
            .ReturnsAsync(new CrdEvaluationResult
            {
                Cards = new List<CrdCard> { new() { Summary = "Test", Indicator = "info" } },
                CodesEvaluated = 1,
                TranslationsPerformed = 1,
            });

        var request = CreateHookRequestWithCode("order-select", "http://snomed.info/sct", "73211009", "Diabetes mellitus");
        await _controller.ExecuteHook("cho-order-select", request, CancellationToken.None);

        _crdServiceMock.Verify(s => s.EvaluateCoverageRequirementsAsync(
            It.IsAny<CrdHookRequest>(), "test-tenant", It.IsAny<CancellationToken>()), Times.Once);
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Context!.DraftOrders!.Entry![0].Resource!.Code!.Coding![0].System
            .Should().Be("http://snomed.info/sct");
    }

    [Fact]
    public async Task OrderSelect_CptCode_SkipsTranslation()
    {
        _crdServiceMock
            .Setup(s => s.EvaluateCoverageRequirementsAsync(
                It.IsAny<CrdHookRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CrdEvaluationResult
            {
                Cards = new List<CrdCard> { new() { Summary = "Test", Indicator = "info" } },
                CodesEvaluated = 1,
                TranslationsPerformed = 0,
            });

        var request = CreateHookRequestWithCode("order-select", "http://www.ama-assn.org/go/cpt", "99213", "Office visit");
        var result = await _controller.ExecuteHook("cho-order-select", request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _crdServiceMock.Verify(s => s.EvaluateCoverageRequirementsAsync(
            It.IsAny<CrdHookRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Card indicator verification ──────────────────────────────────────────

    [Fact]
    public async Task OrderSelect_AuthRequired_ReturnsWarningCard()
    {
        _crdServiceMock
            .Setup(s => s.EvaluateCoverageRequirementsAsync(
                It.IsAny<CrdHookRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CrdEvaluationResult
            {
                Cards = new List<CrdCard>
                {
                    new() { Summary = "Prior authorization required", Indicator = "warning" },
                },
                CodesEvaluated = 1,
            });

        var request = CreateValidHookRequest("order-select");
        var result = await _controller.ExecuteHook("cho-order-select", request, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var cardResponse = okResult.Value.Should().BeOfType<CrdCardResponse>().Subject;
        cardResponse.Cards[0].Indicator.Should().Be("warning");
    }

    [Fact]
    public async Task OrderSelect_NoAuthRequired_ReturnsInfoCard()
    {
        _crdServiceMock
            .Setup(s => s.EvaluateCoverageRequirementsAsync(
                It.IsAny<CrdHookRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CrdEvaluationResult
            {
                Cards = new List<CrdCard>
                {
                    new() { Summary = "No auth required", Indicator = "info" },
                },
                CodesEvaluated = 1,
            });

        var request = CreateValidHookRequest("order-select");
        var result = await _controller.ExecuteHook("cho-order-select", request, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var cardResponse = okResult.Value.Should().BeOfType<CrdCardResponse>().Subject;
        cardResponse.Cards[0].Indicator.Should().Be("info");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CrdHookRequest CreateValidHookRequest(string hookType) =>
        CreateHookRequestWithCode(hookType, "http://www.ama-assn.org/go/cpt", "99213", "Office visit");

    private static CrdHookRequest CreateHookRequestWithCode(
        string hookType, string codeSystem, string code, string display) => new()
    {
        HookInstance = Guid.NewGuid().ToString(),
        Hook = hookType,
        FhirServer = "https://ehr.example.com/fhir",
        Context = new CrdHookContext
        {
            UserId = "Practitioner/123",
            PatientId = "456",
            DraftOrders = new CrdDraftOrders
            {
                ResourceType = "Bundle",
                Entry = new List<CrdDraftOrderEntry>
                {
                    new()
                    {
                        Resource = new CrdDraftOrderResource
                        {
                            ResourceType = "ServiceRequest",
                            Code = new CrdCodeableConcept
                            {
                                Coding = new List<CrdCoding>
                                {
                                    new() { System = codeSystem, Code = code, Display = display },
                                },
                            },
                        },
                    },
                },
            },
        },
    };
}
