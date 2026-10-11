using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using FhirService.Models;
using FhirService.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// D-21 (first part) / G-05. CRD must fail closed: "no prior authorization
/// needed" is returned only on an explicit, positive determination — today,
/// the tenant's own classification listing the code as auto-approved. An
/// unknown code, an unavailable / no-op / failing / timed-out rule engine, and
/// an engine "Approve" reached under CRD's incomplete context all produce an
/// "unknown — contact the payer or submit for review" card instead.
/// </summary>
public class CrdFailClosedTests
{
    private const string Tenant = "tenant-crd-fail-closed";
    private const string CptSystem = "http://www.ama-assn.org/go/cpt";
    private const string NoAuthSummary = "No prior authorization needed";

    private readonly Mock<IPriorAuthRuleEngine> _engine = new();

    private static readonly CrdConfig Config = new()
    {
        Enabled = true,
        AuthRequiredCodes = ["27447"],
        AutoApprovedCodes = ["99213"],
        DocumentationRequiredCodes = ["72148"],
    };

    private CrdService Service(IHttpClientFactory? http = null) => new(
        http ?? new ThrowingTerminology(),
        _engine.Object,
        Options.Create(Config),
        new CrdClassificationStore(new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 })),
        NullLogger<CrdService>.Instance);

    private void EngineReturns(PaDecisionOutcome outcome, string ruleId) => _engine
        .Setup(e => e.EvaluateAsync(It.IsAny<PaRuleContext>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new PaRuleDecision
        {
            Outcome = outcome,
            FiringRuleId = ruleId,
            FiringRuleName = ruleId,
            ResolvedRuleSetKey = "platform/TX/Medicaid/STAR",
        });

    private static CrdHookRequest Hook(string system, string code) => new()
    {
        HookInstance = Guid.NewGuid().ToString(),
        Hook = "order-select",
        Context = new CrdHookContext
        {
            UserId = "Practitioner/123",
            PatientId = "pat-001",
            DraftOrders = new CrdDraftOrders
            {
                Entry =
                [
                    new()
                    {
                        Resource = new CrdDraftOrderResource
                        {
                            ResourceType = "ServiceRequest",
                            Code = new CrdCodeableConcept { Coding = [new() { System = system, Code = code, Display = code }] },
                        },
                    },
                ],
            },
        },
    };

    private async Task<CrdCard> OnlyCardAsync(string system, string code, IHttpClientFactory? http = null)
    {
        var result = await Service(http).EvaluateCoverageRequirementsAsync(Hook(system, code), Tenant);
        return result.Cards.Should().ContainSingle().Subject;
    }

    private static void ShouldBeUnknown(CrdCard card)
    {
        card.Summary.Should().NotContain(NoAuthSummary);
        card.Source!.Topic!.Code.Should().NotBe("no-auth");
        card.Summary.Should().Contain("could not be determined");
        card.Detail.Should().Contain("Contact the payer");
        card.Indicator.Should().Be("warning");
    }

    // ── Unknown code ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownCode_EngineFindsNoRule_IsUnknownNotNoAuth()
    {
        EngineReturns(PaDecisionOutcome.Pend, "NoRuleMatch");
        ShouldBeUnknown(await OnlyCardAsync(CptSystem, "99999"));
    }

    [Fact]
    public async Task UnknownCode_NoRulesConfigured_IsUnknownNotNoAuth()
    {
        EngineReturns(PaDecisionOutcome.Pend, "NoRulesConfigured");
        ShouldBeUnknown(await OnlyCardAsync(CptSystem, "99999"));
    }

    // ── Engine unavailable ────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownCode_NoOpEngine_IsUnknownNotNoAuth()
    {
        // The engine registered when there is no database (NoOpPriorAuthRuleEngine).
        EngineReturns(PaDecisionOutcome.Pend, "NoOp");
        ShouldBeUnknown(await OnlyCardAsync(CptSystem, "99999"));
    }

    [Fact]
    public async Task EngineThrows_IsUnknownNotNoAuth()
    {
        _engine.Setup(e => e.EvaluateAsync(It.IsAny<PaRuleContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("rule store unreachable"));
        ShouldBeUnknown(await OnlyCardAsync(CptSystem, "99999"));
    }

    [Fact]
    public async Task EngineTimesOut_IsUnknownNotNoAuth()
    {
        _engine.Setup(e => e.EvaluateAsync(It.IsAny<PaRuleContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("rule engine timed out"));
        ShouldBeUnknown(await OnlyCardAsync(CptSystem, "99999"));
    }

    [Fact]
    public async Task EngineRuleError_IsUnknownNotNoAuth()
    {
        EngineReturns(PaDecisionOutcome.Pend, "RuleError:TX-STAR-001");
        ShouldBeUnknown(await OnlyCardAsync(CptSystem, "99999"));
    }

    // ── Engine "Approve" under CRD's context ──────────────────────────────────

    [Fact]
    public async Task EngineApprove_ForAnUnclassifiedCode_IsUnknownNotNoAuth()
    {
        // CRD evaluates with a fixed TX/Medicaid/STAR context and NO diagnoses,
        // so e.g. DiagnosisRequired "approves" every procedure it scopes. That
        // is not a determination that PA is not required.
        EngineReturns(PaDecisionOutcome.Approve, "TX-STAR-DX-001");
        ShouldBeUnknown(await OnlyCardAsync(CptSystem, "E0601"));
    }

    [Fact]
    public async Task UntranslatedSnomedCode_IsUnknownNotNoAuth()
    {
        // Terminology service down: the raw SNOMED code matches nothing.
        EngineReturns(PaDecisionOutcome.Pend, "NoRuleMatch");
        ShouldBeUnknown(await OnlyCardAsync("http://snomed.info/sct", "73211009"));
    }

    // ── Explicit determinations still answer ──────────────────────────────────

    [Fact]
    public async Task CodeTheTenantClassifiesAsAutoApproved_IsNoAuth()
    {
        EngineReturns(PaDecisionOutcome.Pend, "NoRuleMatch");
        var card = await OnlyCardAsync(CptSystem, "99213");
        card.Summary.Should().Contain(NoAuthSummary);
        card.Indicator.Should().Be("info");
    }

    [Fact]
    public async Task CodeTheTenantClassifiesAsAuthRequired_IsAuthRequired_EvenIfTheEngineApproves()
    {
        EngineReturns(PaDecisionOutcome.Approve, "TX-STAR-DX-001");
        var card = await OnlyCardAsync(CptSystem, "27447");
        card.Summary.Should().Contain("Prior authorization required");
    }

    [Fact]
    public async Task EngineRequiresAuth_IsAuthRequired()
    {
        EngineReturns(PaDecisionOutcome.Pend, "TX-STAR-PA-001");
        var card = await OnlyCardAsync(CptSystem, "99999");
        card.Summary.Should().Contain("Prior authorization required");
    }

    private sealed class ThrowingTerminology : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler()) { BaseAddress = new Uri("http://terminology.test/") };

        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => throw new HttpRequestException("terminology-service unavailable");
        }
    }
}

/// <summary>
/// D-21 over HTTP: the real host as deployed without a rules database (the
/// NoOp engine) and with no CRD classification configured. Every code is
/// unknown, so no card may say "no prior authorization needed".
/// </summary>
public class CrdFailClosedHttpTests : IClassFixture<FhirTestWebAppFactory>
{
    private readonly FhirTestWebAppFactory _factory;

    public CrdFailClosedHttpTests(FhirTestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task OrderSign_NoRulesNoClassification_DoesNotSayNoAuthNeeded()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/cds-services/cho-order-sign")
        {
            Content = new StringContent(
                "{\"hookInstance\":\"h-d21\",\"hook\":\"order-sign\",\"context\":{\"userId\":\"Practitioner/p1\","
                + "\"patientId\":\"pat-001\",\"draftOrders\":{\"resourceType\":\"Bundle\",\"entry\":[{\"resource\":"
                + "{\"resourceType\":\"ServiceRequest\",\"code\":{\"coding\":[{\"system\":\"http://www.ama-assn.org/go/cpt\","
                + "\"code\":\"70553\",\"display\":\"MRI brain\"}]}}}]}}}",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _factory.IssueToken("user/Coverage.read"));

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var cards = doc.RootElement.GetProperty("cards").EnumerateArray().ToList();
        cards.Should().ContainSingle();
        body.Should().NotContain("No prior authorization needed");
        cards[0].GetProperty("summary").GetString().Should().Contain("could not be determined");
    }
}
