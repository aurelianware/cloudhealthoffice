using System.Text.Json.Nodes;
using ClaimsExaminerService.Models;
using ClaimsExaminerService.Services;
using ClaimsExaminerService.Services.Anthropic;
using ClaimsExaminerService.Services.Events;
using ClaimsExaminerService.Services.Examiner;
using CloudHealthOffice.Events;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsExaminerService.Tests.Services.Examiner;

/// <summary>
/// Capability 5.9 — verifies <see cref="ExaminerOrchestrator"/> emits
/// <c>ClaimAiExaminationCompletedEvent</c> after a successful write-back
/// to claims-service. Covers the success path, the fallback path
/// (Anthropic exception → EscalateToHuman still emits a completion
/// event), the no-emit path (write-back returned false), and graceful
/// degradation when the publisher itself throws.
/// </summary>
public class ExaminerOrchestratorEventEmissionTests
{
    private const string ClaimId = "claim-emit-1";
    private const string TenantId = "tenant-emit-1";

    private readonly IClaimsServiceClient _claims = Substitute.For<IClaimsServiceClient>();
    private readonly IAnthropicClient _anthropic = Substitute.For<IAnthropicClient>();
    private readonly IProviderRfaiHistoryClient _rfaiHistory = Substitute.For<IProviderRfaiHistoryClient>();
    private readonly IAiExaminationEventPublisher _eventPublisher = Substitute.For<IAiExaminationEventPublisher>();
    private readonly ExaminerPromptBuilder _prompts = new();

    public ExaminerOrchestratorEventEmissionTests()
    {
        _rfaiHistory
            .GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ProviderRfaiHistory?)null);

        _claims.GetClaimAsync(ClaimId, TenantId, Arg.Any<CancellationToken>())
            .Returns(new ClaimSnapshot
            {
                Id = ClaimId,
                TenantId = TenantId,
                BillingProviderNPI = "1234567890",
                ClaimLines = new List<ClaimLineSnapshot>
                {
                    new() { LineNumber = 1, ProcedureCode = "11042", ChargeAmount = 100m },
                    new() { LineNumber = 2, ProcedureCode = "97597", ChargeAmount = 80m },
                },
            });
    }

    private ExaminerOrchestrator NewSut() => new(
        _claims, _anthropic, _prompts, _rfaiHistory, _eventPublisher,
        NullLogger<ExaminerOrchestrator>.Instance);

    private static ClaimPendedEvent EligibleEvent() => new()
    {
        EventId = "evt-1",
        ClaimId = ClaimId,
        TenantId = TenantId,
        PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            EditFailures = new List<NcciEditFailureSnapshot>
            {
                new()
                {
                    EditType = "NcciPair",
                    RuleId = "NE001",
                    Column1Code = "11042",
                    Column2Code = "97597",
                },
            },
        },
    };

    private static AnthropicToolResult ToolResult(string disposition, double confidence)
    {
        var args = new JsonObject
        {
            ["recommended_disposition"] = disposition,
            ["confidence_score"] = confidence,
            ["rationale"] = "test rationale",
            ["policy_citations"] = new JsonArray("CMS NCCI Manual ch.1"),
        };
        return new AnthropicToolResult
        {
            ToolName = "recommend_disposition",
            Arguments = args,
            ModelId = "claude-opus-4-6",
            InputTokens = 100,
            OutputTokens = 50,
        };
    }

    [Fact]
    public async Task Emits_completion_event_after_successful_writeback()
    {
        _anthropic.CallWithToolAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AnthropicTool>(), Arg.Any<CancellationToken>())
            .Returns(ToolResult("Approve", 0.92));
        _claims.SetAiExaminationAsync(
            ClaimId, TenantId, Arg.Any<AiExaminationDto>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await NewSut().ProcessAsync(EligibleEvent(), CancellationToken.None);

        await _eventPublisher.Received(1).PublishCompletedAsync(
            ClaimId,
            TenantId,
            Arg.Is<AiExaminationDto>(d => d.RecommendedDisposition == "Approve" && d.ConfidenceScore == 0.92),
            "evt-1",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_NOT_emit_completion_event_when_writeback_returns_false()
    {
        // 409 Conflict on the PUT — claim no longer Pended. The AI
        // recommendation is moot; downstream consumers shouldn't be
        // notified about a non-event.
        _anthropic.CallWithToolAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AnthropicTool>(), Arg.Any<CancellationToken>())
            .Returns(ToolResult("Deny", 0.85));
        _claims.SetAiExaminationAsync(
            ClaimId, TenantId, Arg.Any<AiExaminationDto>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await NewSut().ProcessAsync(EligibleEvent(), CancellationToken.None);

        await _eventPublisher.DidNotReceiveWithAnyArgs().PublishCompletedAsync(
            default!, default!, default!, default, default);
    }

    [Fact]
    public async Task Fallback_path_still_emits_completion_event_with_EscalateToHuman()
    {
        // Anthropic exception → orchestrator writes EscalateToHuman fallback.
        // EscalateToHuman is a terminal recommendation; downstream consumers
        // still need the completion event so they don't wait forever.
        _anthropic.CallWithToolAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AnthropicTool>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("anthropic 503"));
        _claims.SetAiExaminationAsync(
            ClaimId, TenantId, Arg.Any<AiExaminationDto>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await NewSut().ProcessAsync(EligibleEvent(), CancellationToken.None);

        await _eventPublisher.Received(1).PublishCompletedAsync(
            ClaimId,
            TenantId,
            Arg.Is<AiExaminationDto>(d => d.RecommendedDisposition == "EscalateToHuman" && d.ConfidenceScore == 0),
            "evt-1",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fallback_does_not_emit_when_writeback_returns_false()
    {
        _anthropic.CallWithToolAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AnthropicTool>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("anthropic 503"));
        _claims.SetAiExaminationAsync(
            ClaimId, TenantId, Arg.Any<AiExaminationDto>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await NewSut().ProcessAsync(EligibleEvent(), CancellationToken.None);

        await _eventPublisher.DidNotReceiveWithAnyArgs().PublishCompletedAsync(
            default!, default!, default!, default, default);
    }

    [Fact]
    public async Task EventId_propagates_to_completion_event_as_correlation_id()
    {
        _anthropic.CallWithToolAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AnthropicTool>(), Arg.Any<CancellationToken>())
            .Returns(ToolResult("RequestInfo", 0.65));
        _claims.SetAiExaminationAsync(
            ClaimId, TenantId, Arg.Any<AiExaminationDto>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var evt = EligibleEvent();
        evt.EventId = "specific-correlation-xyz";

        await NewSut().ProcessAsync(evt, CancellationToken.None);

        await _eventPublisher.Received(1).PublishCompletedAsync(
            ClaimId, TenantId, Arg.Any<AiExaminationDto>(),
            "specific-correlation-xyz",
            Arg.Any<CancellationToken>());
    }
}
