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
/// What CRD does when it cannot resolve the member — the other half of D-21's
/// second part.
///
/// <para>
/// The old behaviour was to call the rule engine anyway, with a hard-coded
/// <c>TX</c> / <c>Medicaid</c> / <c>STAR</c> context. That does not produce a
/// weaker determination; it produces a confident determination about somebody
/// else's benefits. So the engine is not called at all. What survives is an
/// administrator's explicit code classification, because listing a code as
/// requiring authorization is not a statement about any particular member.
/// </para>
/// </summary>
public class CrdUnresolvedMemberTests
{
    private const string Tenant = "tenant-crd-unresolved";
    private const string CptSystem = "http://www.ama-assn.org/go/cpt";

    private readonly Mock<IPriorAuthRuleEngine> _engine = new();

    private static readonly CrdConfig Config = new()
    {
        Enabled = true,
        AuthRequiredCodes = ["27447"],
        AutoApprovedCodes = ["99213"],
        DocumentationRequiredCodes = ["72148"],
    };

    public CrdUnresolvedMemberTests()
    {
        // An engine that would approve everything, so a test that sees an
        // approval has necessarily called it.
        _engine
            .Setup(e => e.EvaluateAsync(It.IsAny<PaRuleContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Approve,
                FiringRuleId = "WouldApprove",
                FiringRuleName = "WouldApprove",
                ResolvedRuleSetKey = "platform/TX/Medicaid/STAR",
            });
    }

    [Theory]
    [InlineData(CrdContextFailure.MemberNotFound)]
    [InlineData(CrdContextFailure.NoActiveCoverage)]
    [InlineData(CrdContextFailure.ContextIncomplete)]
    [InlineData(CrdContextFailure.Unavailable)]
    public async Task The_rule_engine_is_never_called_without_a_resolved_member(CrdContextFailure failure)
    {
        await EvaluateAsync("27447", CrdContextResolverDouble.Unresolved(failure));

        _engine.Verify(
            e => e.EvaluateAsync(It.IsAny<PaRuleContext>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a context CHO could not read is not a context to evaluate rules against");
    }

    /// <summary>
    /// The classification is an administrator's statement about a code, not
    /// about a member, so it still applies. Losing it would be an over-correction
    /// that turned every unresolved member into "we have no idea" even where CHO
    /// plainly does.
    /// </summary>
    [Fact]
    public async Task An_explicit_classification_still_answers_for_an_unresolved_member()
    {
        var result = await EvaluateAsync(
            "27447", CrdContextResolverDouble.Unresolved(CrdContextFailure.ContextIncomplete));

        result.Cards.Should().ContainSingle()
            .Which.Summary.Should().Contain("Prior authorization required");
    }

    [Fact]
    public async Task An_auto_approved_code_still_answers_for_an_unresolved_member()
    {
        var result = await EvaluateAsync(
            "99213", CrdContextResolverDouble.Unresolved(CrdContextFailure.Unavailable));

        result.Cards.Should().ContainSingle()
            .Which.Summary.Should().Contain("No prior authorization needed");
    }

    /// <summary>
    /// Without a member, or without coverage the order could be under, nothing
    /// is covered, whatever the service's classification says: CRD 2.2.1
    /// answers not-covered with the reason, and no pa-needed (crd-ci-q2).
    /// </summary>
    [Theory]
    [InlineData(CrdContextFailure.MemberNotFound, "no-member-found")]
    [InlineData(CrdContextFailure.NoActiveCoverage, "no-active-coverage")]
    [InlineData(CrdContextFailure.CoverageNotFound, "coverage-not-found")]
    public async Task Without_coverage_to_answer_under_the_answer_is_not_covered(
        CrdContextFailure failure, string expectedIgCode)
    {
        var result = await EvaluateAsync("27447", CrdContextResolverDouble.Unresolved(failure));

        var parts = CoverageInformation(result);
        parts.Single(p => p.GetProperty("url").GetString() == "covered").GetProperty("valueCode").GetString()
            .Should().Be("not-covered");
        parts.Should().NotContain(p => p.GetProperty("url").GetString() == "pa-needed");
        Reason(result).GetProperty("coding")[0].GetProperty("code").GetString().Should().Be(expectedIgCode);
        result.Cards.Should().ContainSingle().Which.Summary.Should().Contain("No coverage");
    }

    /// <summary>
    /// A provider deciding what to do next needs to know which kind of
    /// not-knowing this is. "We cannot find your patient", "this member has no
    /// coverage with us" and "try again later" call for three different actions,
    /// and the IG has a code for each.
    /// </summary>
    [Theory]
    [InlineData(CrdContextFailure.MemberNotFound, "no-member-found")]
    [InlineData(CrdContextFailure.NoActiveCoverage, "no-active-coverage")]
    [InlineData(CrdContextFailure.ContextIncomplete, "coverage-not-found")]
    [InlineData(CrdContextFailure.Unavailable, "technical")]
    [InlineData(CrdContextFailure.CoverageNotFound, "coverage-not-found")]
    [InlineData(CrdContextFailure.EhrCoverageUnreadable, "technical")]
    public async Task The_extension_names_why_the_member_context_was_missing(
        CrdContextFailure failure, string expectedIgCode)
    {
        // An unclassified code, so the determination comes from the missing
        // context rather than from a classification.
        var result = await EvaluateAsync("99999", CrdContextResolverDouble.Unresolved(failure));

        var reason = Reason(result);
        reason.GetProperty("coding")[0].GetProperty("code").GetString().Should().Be(expectedIgCode);
        reason.GetProperty("text").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_resolved_member_reaches_the_engine_exactly_once_per_code()
    {
        var resolver = CrdContextResolverDouble.Resolved();

        await EvaluateAsync("27447", resolver);

        resolver.Calls.Should().Be(1,
            "the member is the same for every draft order in a hook, so reading it once is the point");
    }

    /// <summary>
    /// The engine is handed the one code being evaluated, not the whole order
    /// set — the resolved context carries every code, and a rule that keyed off
    /// the list would otherwise answer about a different service.
    /// </summary>
    [Fact]
    public async Task The_engine_is_asked_about_the_code_being_evaluated()
    {
        await EvaluateAsync("27447", CrdContextResolverDouble.Resolved());

        _engine.Verify(e => e.EvaluateAsync(
            It.Is<PaRuleContext>(c => c.ProcedureCodes.Count == 1 && c.ProcedureCodes[0] == "27447"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>CRD configuration option coverage-info=false: cards only, no coverage-information.</summary>
    [Fact]
    public async Task Coverage_info_switched_off_returns_no_coverage_information_cards_or_system_actions()
    {
        var hook = Hook("27447");
        hook.Extension = JsonDocument.Parse(
            """{"davinci-crd.configuration":{"coverage-info":false,"unknown-option":true}}""").RootElement.Clone();

        var result = await EvaluateAsync(hook, CrdContextResolverDouble.Unresolved(CrdContextFailure.ContextIncomplete));

        // Every CHO card is a coverage-information card (topic coverage-info),
        // so with coverage-info off there is nothing to return, and still a 200.
        result.Cards.Should().BeEmpty();
        result.SystemActions.Should().BeEmpty();
    }

    private Task<CrdEvaluationResult> EvaluateAsync(string code, ICrdContextResolver resolver)
        => EvaluateAsync(Hook(code), resolver);

    private async Task<CrdEvaluationResult> EvaluateAsync(CrdHookRequest hook, ICrdContextResolver resolver)
    {
        var service = new CrdService(
            new UnreachableTerminology(),
            _engine.Object,
            Options.Create(Config),
            new CrdClassificationStore(new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 })),
            resolver,
            NullLogger<CrdService>.Instance);

        return await service.EvaluateCoverageRequirementsAsync(hook, Tenant);
    }

    private static JsonElement Reason(CrdEvaluationResult result)
        => CoverageInformation(result)
            .Single(p => p.GetProperty("url").GetString() == "reason")
            .GetProperty("valueCodeableConcept");

    private static List<JsonElement> CoverageInformation(CrdEvaluationResult result)
        => result.SystemActions
            .SelectMany(a => a.Resource!.Value.GetProperty("extension").EnumerateArray())
            .Single(e => e.GetProperty("url").GetString() == CrdCoverageInformation.ExtensionUrl)
            .GetProperty("extension").EnumerateArray().ToList();

    private sealed class UnreachableTerminology : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new Handler()) { BaseAddress = new Uri("http://terminology.test/") };

        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => throw new HttpRequestException("terminology-service unavailable");
        }
    }

    private static CrdHookRequest Hook(string code) => new()
    {
        HookInstance = Guid.NewGuid().ToString(),
        Hook = "order-sign",
        Context = new CrdHookContext
        {
            UserId = "Practitioner/1234567893",
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
                            Code = new CrdCodeableConcept
                            {
                                Coding = [new() { System = CptSystem, Code = code, Display = code }],
                            },
                        },
                    },
                ],
            },
        },
        Prefetch = new Dictionary<string, JsonElement>
        {
            ["coverage"] = JsonDocument.Parse("""
                {
                  "resourceType": "Coverage",
                  "id": "cov-001",
                  "status": "active"
                }
                """).RootElement.Clone(),
        },
    };
}
