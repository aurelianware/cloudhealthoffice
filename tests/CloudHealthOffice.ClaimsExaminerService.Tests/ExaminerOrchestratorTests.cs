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

namespace CloudHealthOffice.ClaimsExaminerService.Tests;

public class ExaminerOrchestratorTests
{
    private readonly IClaimsServiceClient _claims = Substitute.For<IClaimsServiceClient>();
    private readonly IAnthropicClient _anthropic = Substitute.For<IAnthropicClient>();
    private readonly IProviderRfaiHistoryClient _rfaiHistory = Substitute.For<IProviderRfaiHistoryClient>();
    private readonly IAiExaminationEventPublisher _eventPublisher = Substitute.For<IAiExaminationEventPublisher>();
    private readonly ExaminerPromptBuilder _prompts = new();

    public ExaminerOrchestratorTests()
    {
        // Default the RFAI history client to "no data" so unrelated tests
        // don't need to set it up. Tests that exercise the enrichment path
        // override this on a per-test basis.
        _rfaiHistory
            .GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ProviderRfaiHistory?)null);
    }

    private ExaminerOrchestrator NewSut() => new(
        _claims, _anthropic, _prompts, _rfaiHistory, _eventPublisher, NullLogger<ExaminerOrchestrator>.Instance);

    [Fact]
    public async Task Skips_Event_With_Non_NCCI_Pend_Code()
    {
        var evt = new ClaimPendedEvent
        {
            ClaimId = "claim-1",
            TenantId = "tenant-a",
            PendDetails = new PendDetails { PendCode = "AUTH" }
        };

        await NewSut().ProcessAsync(evt, CancellationToken.None);

        await _claims.DidNotReceiveWithAnyArgs().GetClaimAsync(default!, default!, default);
        await _anthropic.DidNotReceiveWithAnyArgs().CallWithToolAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Skips_Event_With_No_Modifier_Addressable_Edits()
    {
        var evt = new ClaimPendedEvent
        {
            ClaimId = "claim-2",
            TenantId = "tenant-a",
            PendDetails = new PendDetails
            {
                PendCode = "NCCI",
                EditFailures = new List<NcciEditFailureSnapshot>
                {
                    new() { EditType = "Mue", RuleId = "NE002" }
                }
            }
        };

        await NewSut().ProcessAsync(evt, CancellationToken.None);

        await _claims.DidNotReceiveWithAnyArgs().GetClaimAsync(default!, default!, default);
        await _anthropic.DidNotReceiveWithAnyArgs().CallWithToolAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Skips_Event_With_Missing_Identifiers()
    {
        var evt = new ClaimPendedEvent { ClaimId = "", TenantId = "" };
        await NewSut().ProcessAsync(evt, CancellationToken.None);
        await _claims.DidNotReceiveWithAnyArgs().GetClaimAsync(default!, default!, default);
    }

    [Fact]
    public async Task Skips_When_Claim_Lookup_Returns_Null()
    {
        var evt = MakeAddressableEvent();
        _claims.GetClaimAsync(evt.ClaimId, evt.TenantId, Arg.Any<CancellationToken>())
            .Returns((ClaimSnapshot?)null);

        await NewSut().ProcessAsync(evt, CancellationToken.None);

        await _anthropic.DidNotReceiveWithAnyArgs().CallWithToolAsync(default!, default!, default!, default);
        await _claims.DidNotReceiveWithAnyArgs().SetAiExaminationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Happy_Path_Writes_Examination_With_Parsed_Tool_Result()
    {
        var evt = MakeAddressableEvent();
        _claims.GetClaimAsync(evt.ClaimId, evt.TenantId, Arg.Any<CancellationToken>())
            .Returns(MakeClaimSnapshot(evt.ClaimId));

        var toolResult = new AnthropicToolResult
        {
            ToolName = "recommend_disposition",
            ModelId = "claude-opus-4-6",
            InputTokens = 800,
            OutputTokens = 240,
            Arguments = new JsonObject
            {
                ["recommended_disposition"] = "RequestInfo",
                ["confidence_score"] = 0.74,
                ["rationale"] = "Modifier -59 is absent from line 2 and the diagnosis pointers are identical to line 1; insufficient documentation to override the bundle.",
                ["policy_citations"] = new JsonArray("NCCI Policy Manual Ch.1 §F.3", "CMS PTP Edits 2025Q1: 27447/27486 mod indicator=1")
            }
        };

        _anthropic.CallWithToolAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AnthropicTool>(), Arg.Any<CancellationToken>())
            .Returns(toolResult);

        AiExaminationDto? captured = null;
        _claims.SetAiExaminationAsync(
                evt.ClaimId, evt.TenantId, Arg.Do<AiExaminationDto>(dto => captured = dto), Arg.Any<CancellationToken>())
            .Returns(true);

        await NewSut().ProcessAsync(evt, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("RequestInfo", captured!.RecommendedDisposition);
        Assert.Equal(0.74, captured.ConfidenceScore);
        Assert.Contains("Modifier -59", captured.Rationale);
        Assert.Equal(2, captured.PolicyCitations.Count);
        Assert.Equal("claude-opus-4-6", captured.ModelId);
        Assert.Equal("ncci-pend-v1", captured.PromptVersion);
    }

    [Fact]
    public async Task Anthropic_Failure_Writes_EscalateToHuman_Fallback()
    {
        var evt = MakeAddressableEvent();
        _claims.GetClaimAsync(evt.ClaimId, evt.TenantId, Arg.Any<CancellationToken>())
            .Returns(MakeClaimSnapshot(evt.ClaimId));

        _anthropic.CallWithToolAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AnthropicTool>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("network down"));

        AiExaminationDto? captured = null;
        _claims.SetAiExaminationAsync(
                evt.ClaimId, evt.TenantId, Arg.Do<AiExaminationDto>(dto => captured = dto), Arg.Any<CancellationToken>())
            .Returns(true);

        await NewSut().ProcessAsync(evt, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("EscalateToHuman", captured!.RecommendedDisposition);
        Assert.Equal(0, captured.ConfidenceScore);
        Assert.Contains("AI examination unavailable", captured.Rationale);
    }

    [Fact]
    public async Task Model_Returning_Null_ToolResult_Writes_EscalateToHuman_Fallback()
    {
        var evt = MakeAddressableEvent();
        _claims.GetClaimAsync(evt.ClaimId, evt.TenantId, Arg.Any<CancellationToken>())
            .Returns(MakeClaimSnapshot(evt.ClaimId));

        _anthropic.CallWithToolAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AnthropicTool>(), Arg.Any<CancellationToken>())
            .Returns((AnthropicToolResult?)null);

        AiExaminationDto? captured = null;
        _claims.SetAiExaminationAsync(
                evt.ClaimId, evt.TenantId, Arg.Do<AiExaminationDto>(dto => captured = dto), Arg.Any<CancellationToken>())
            .Returns(true);

        await NewSut().ProcessAsync(evt, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("EscalateToHuman", captured!.RecommendedDisposition);
        Assert.Contains("declined", captured.Rationale);
    }

    [Fact]
    public async Task Confidence_Score_Out_Of_Range_Is_Clamped()
    {
        var evt = MakeAddressableEvent();
        _claims.GetClaimAsync(evt.ClaimId, evt.TenantId, Arg.Any<CancellationToken>())
            .Returns(MakeClaimSnapshot(evt.ClaimId));

        _anthropic.CallWithToolAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AnthropicTool>(), Arg.Any<CancellationToken>())
            .Returns(new AnthropicToolResult
            {
                ModelId = "claude-opus-4-6",
                Arguments = new JsonObject
                {
                    ["recommended_disposition"] = "Approve",
                    ["confidence_score"] = 1.5,
                    ["rationale"] = "test",
                    ["policy_citations"] = new JsonArray()
                }
            });

        AiExaminationDto? captured = null;
        _claims.SetAiExaminationAsync(
                evt.ClaimId, evt.TenantId, Arg.Do<AiExaminationDto>(dto => captured = dto), Arg.Any<CancellationToken>())
            .Returns(true);

        await NewSut().ProcessAsync(evt, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(1.0, captured!.ConfidenceScore);
    }

    [Fact]
    public async Task Rfai_History_Client_Failure_Does_Not_Block_Recommendation()
    {
        var evt = MakeAddressableEvent();
        _claims.GetClaimAsync(evt.ClaimId, evt.TenantId, Arg.Any<CancellationToken>())
            .Returns(MakeClaimSnapshot(evt.ClaimId));

        // RFAI history client throws — orchestrator must continue and call Claude anyway.
        _rfaiHistory
            .GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("rfai-service down"));

        _anthropic.CallWithToolAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AnthropicTool>(), Arg.Any<CancellationToken>())
            .Returns(new AnthropicToolResult
            {
                ModelId = "claude-opus-4-6",
                Arguments = new JsonObject
                {
                    ["recommended_disposition"] = "EscalateToHuman",
                    ["confidence_score"] = 0.5,
                    ["rationale"] = "ok",
                    ["policy_citations"] = new JsonArray()
                }
            });

        _claims.SetAiExaminationAsync(
                evt.ClaimId, evt.TenantId, Arg.Any<AiExaminationDto>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await NewSut().ProcessAsync(evt, CancellationToken.None);

        await _anthropic.Received(1).CallWithToolAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AnthropicTool>(), Arg.Any<CancellationToken>());
        await _claims.Received(1).SetAiExaminationAsync(
            evt.ClaimId, evt.TenantId, Arg.Any<AiExaminationDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rfai_History_Is_Fetched_With_Provider_NPI_And_RuleId()
    {
        var evt = MakeAddressableEvent();
        var snapshot = MakeClaimSnapshot(evt.ClaimId);
        _claims.GetClaimAsync(evt.ClaimId, evt.TenantId, Arg.Any<CancellationToken>())
            .Returns(snapshot);

        _anthropic.CallWithToolAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AnthropicTool>(), Arg.Any<CancellationToken>())
            .Returns(new AnthropicToolResult
            {
                ModelId = "claude-opus-4-6",
                Arguments = new JsonObject
                {
                    ["recommended_disposition"] = "EscalateToHuman",
                    ["confidence_score"] = 0.5,
                    ["rationale"] = "ok",
                    ["policy_citations"] = new JsonArray()
                }
            });
        _claims.SetAiExaminationAsync(
                evt.ClaimId, evt.TenantId, Arg.Any<AiExaminationDto>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await NewSut().ProcessAsync(evt, CancellationToken.None);

        // Lookup must be scoped to billing provider + the rule id of the addressable edit,
        // not arbitrary identifiers — otherwise the enrichment is meaningless.
        await _rfaiHistory.Received(1).GetAsync(
            snapshot.BillingProviderNPI,
            "NE001",
            evt.TenantId,
            Arg.Any<CancellationToken>());
    }

    private static ClaimPendedEvent MakeAddressableEvent() => new()
    {
        ClaimId = "claim-99",
        TenantId = "tenant-a",
        ClaimNumber = "CLM-99",
        MemberId = "MBR-1",
        BillingProviderNPI = "1234567890",
        PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            PendReason = "NCCI bundling edit",
            EditFailures = new List<NcciEditFailureSnapshot>
            {
                new()
                {
                    EditType = "NcciPair",
                    RuleId = "NE001",
                    Column1Code = "27447",
                    Column2Code = "27486",
                    AffectedLineNumbers = new List<int> { 2 },
                    ModifierOverridePresent = false,
                    SuggestedCarc = "97"
                }
            }
        }
    };

    private static ClaimSnapshot MakeClaimSnapshot(string id) => new()
    {
        Id = id,
        TenantId = "tenant-a",
        ClaimNumber = "CLM-99",
        MemberId = "MBR-1",
        BillingProviderNPI = "1234567890",
        PlaceOfServiceCode = "11",
        ServiceDateFrom = new DateTime(2026, 3, 1),
        ServiceDateTo = new DateTime(2026, 3, 1),
        TotalChargeAmount = 1500m,
        Status = 4, // Pended
        DiagnosisCodes = new List<DiagnosisSnapshot>
        {
            new() { Code = "M17.11", PointerNumber = 1, Description = "Unilateral primary osteoarthritis, right knee" }
        },
        ClaimLines = new List<ClaimLineSnapshot>
        {
            new()
            {
                LineNumber = 1,
                ProcedureCode = "27447",
                Modifiers = new(),
                DiagnosisPointers = new() { 1 },
                Units = 1,
                ChargeAmount = 1200m,
                ServiceDateFrom = new DateTime(2026, 3, 1),
                ServiceDateTo = new DateTime(2026, 3, 1)
            },
            new()
            {
                LineNumber = 2,
                ProcedureCode = "27486",
                Modifiers = new(),
                DiagnosisPointers = new() { 1 },
                Units = 1,
                ChargeAmount = 300m,
                ServiceDateFrom = new DateTime(2026, 3, 1),
                ServiceDateTo = new DateTime(2026, 3, 1)
            }
        },
        PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            EditFailures = new()
        }
    };
}
