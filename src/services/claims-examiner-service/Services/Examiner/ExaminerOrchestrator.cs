using System.Net;
using System.Text.Json.Nodes;
using ClaimsExaminerService.Models;
using ClaimsExaminerService.Services.Anthropic;
using ClaimsExaminerService.Services.Events;
using CloudHealthOffice.Events;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace ClaimsExaminerService.Services.Examiner;

public interface IExaminerOrchestrator
{
    /// <summary>
    /// Process a single ClaimPendedEvent. Out-of-scope and malformed events
    /// return normally (the consumer commits past them). When claims-service
    /// cannot be reached, or does not take the write-back,
    /// <see cref="ExaminerTransientException"/> is thrown, and the consumer
    /// retries the message and dead-letters it after its last attempt instead
    /// of advancing past it. So it is when Anthropic stays rate limited (429)
    /// or overloaded (529) through the client's own retries, its circuit is
    /// open, or the call timed out. Any other Anthropic failure writes the
    /// EscalateToHuman fallback.
    /// </summary>
    Task ProcessAsync(ClaimPendedEvent evt, CancellationToken ct);
}

public class ExaminerOrchestrator : IExaminerOrchestrator
{
    private readonly IClaimsServiceClient _claimsClient;
    private readonly IAnthropicClient _anthropic;
    private readonly IExaminerPromptBuilder _promptBuilder;
    private readonly IProviderRfaiHistoryClient _rfaiHistory;
    private readonly IAiExaminationEventPublisher _eventPublisher;
    private readonly ILogger<ExaminerOrchestrator> _logger;

    public ExaminerOrchestrator(
        IClaimsServiceClient claimsClient,
        IAnthropicClient anthropic,
        IExaminerPromptBuilder promptBuilder,
        IProviderRfaiHistoryClient rfaiHistory,
        IAiExaminationEventPublisher eventPublisher,
        ILogger<ExaminerOrchestrator> logger)
    {
        _claimsClient = claimsClient;
        _anthropic = anthropic;
        _promptBuilder = promptBuilder;
        _rfaiHistory = rfaiHistory;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task ProcessAsync(ClaimPendedEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(evt.ClaimId) || string.IsNullOrEmpty(evt.TenantId))
        {
            _logger.LogWarning("Dropping ClaimPendedEvent with missing claimId/tenantId");
            return;
        }

        // ── v1 scope filter: only NCCI bundling pends with at least one ──
        // modifier-addressable edit failure are eligible for AI examination.
        // Other pend codes (AUTH, COB, MEDREVIEW, etc.) are out of scope.
        if (!string.Equals(evt.PendDetails?.PendCode, ExaminerPendCodes.Ncci, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Skipping claim {ClaimId}: pend code {PendCode} is out of scope for v1",
                evt.ClaimId, evt.PendDetails?.PendCode);
            return;
        }

        var addressableEdit = SelectAddressableEdit(evt.PendDetails);
        if (addressableEdit is null)
        {
            _logger.LogInformation(
                "Skipping claim {ClaimId}: no modifier-addressable NCCI edit failures found",
                evt.ClaimId);
            return;
        }

        // Fetch the full claim. The event payload deliberately carries only a
        // header so the producer side stays cheap; the examiner pulls the full
        // record on demand. A 404 here is benign — the claim may have been
        // voided between pend and examination.
        ClaimSnapshot? claim;
        try
        {
            claim = await _claimsClient.GetClaimAsync(evt.ClaimId, evt.TenantId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogError(ex, "Transport error fetching claim {ClaimId}; the event will be retried", evt.ClaimId);
            throw new ExaminerTransientException($"claims-service unreachable fetching claim {evt.ClaimId}", ex);
        }

        if (claim is null) return;

        // Optional enrichment: provider's historical RFAI behavior for this edit type.
        // Default v1 implementation (NoOpProviderRfaiHistoryClient) returns null and
        // the prompt builder simply omits the history section. A real implementation
        // wires to rfai-service when that aggregate endpoint exists. The fetch is
        // best-effort — never blocks the recommendation.
        ProviderRfaiHistory? rfaiHistory = null;
        try
        {
            rfaiHistory = await _rfaiHistory.GetAsync(
                claim.BillingProviderNPI,
                addressableEdit.RuleId,
                evt.TenantId,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Provider RFAI history lookup failed for {Npi} on {RuleId}; continuing without enrichment",
                claim.BillingProviderNPI, addressableEdit.RuleId);
        }

        // Build the prompt deterministically and call Claude with forced tool use.
        var systemPrompt = _promptBuilder.BuildSystemPrompt();
        var userMessage = _promptBuilder.BuildUserMessage(claim, addressableEdit, rfaiHistory);
        var tool = _promptBuilder.BuildRecommendationTool();

        AnthropicToolResult? result;
        try
        {
            result = await _anthropic.CallWithToolAsync(systemPrompt, userMessage, tool, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the host is stopping: neither an outage nor a reason to escalate
        }
        catch (Exception ex) when (IsTransientAnthropicFailure(ex))
        {
            // Rate limited, overloaded, circuit open or timed out after the client's own
            // retries. An EscalateToHuman written now would route the claim to a person
            // for good because of a burst; the consumer retries the message instead and
            // dead-letters it after its last attempt.
            _logger.LogWarning(ex, "Anthropic unavailable for claim {ClaimId}; the event will be retried", evt.ClaimId);
            throw new ExaminerTransientException($"Anthropic unavailable examining claim {evt.ClaimId}", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Anthropic call failed for claim {ClaimId}; writing EscalateToHuman fallback",
                evt.ClaimId);
            await WriteFallbackAsync(evt, ct, $"AI examination unavailable: {ex.GetType().Name}");
            return;
        }

        if (result is null)
        {
            _logger.LogWarning(
                "Model declined to call recommend_disposition tool for claim {ClaimId}; escalating",
                evt.ClaimId);
            await WriteFallbackAsync(evt, ct, "Model declined to produce a structured recommendation");
            return;
        }

        var examination = ProjectToDto(result, _promptBuilder.PromptVersion);

        var written = await WriteBackAsync(evt, examination, ct);
        if (written)
        {
            // Capability 5.9 — terminal completion event for downstream
            // consumers (5.10 remittance). Only emit when the write-back
            // actually landed; a 409 means a human already acted on the
            // claim and the AI advisory is moot.
            await _eventPublisher.PublishCompletedAsync(
                evt.ClaimId, evt.TenantId, examination, evt.EventId, ct);
        }
    }

    /// <summary>
    /// An Anthropic failure that a later attempt may not repeat: 429 (rate limited),
    /// 529 (overloaded), an open circuit (<see cref="BrokenCircuitException"/>, which
    /// the shared client resilience surfaces as a 503 <see cref="HttpRequestException"/>
    /// around it) or a timed-out attempt (<see cref="TimeoutRejectedException"/>, which
    /// it surfaces as a <see cref="TaskCanceledException"/> around a <see cref="TimeoutException"/>).
    /// Host shutdown is handled before this is asked.
    /// </summary>
    internal static bool IsTransientAnthropicFailure(Exception ex) => ex switch
    {
        BrokenCircuitException or TimeoutRejectedException => true,
        HttpRequestException { InnerException: BrokenCircuitException } => true,
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or (HttpStatusCode)529 } => true,
        TaskCanceledException { InnerException: TimeoutException or TimeoutRejectedException } => true,
        _ => false,
    };

    /// <summary>
    /// Pick the first NCCI pair edit (NE001) from the pend details. V1 only handles
    /// modifier-addressable bundling edits — MUE and other rule types are skipped.
    /// If a claim has multiple addressable edits, the orchestrator handles them
    /// one at a time (the first one) — multi-edit recommendations are phase 2.
    /// </summary>
    public static NcciEditFailureSnapshot? SelectAddressableEdit(PendDetails? details)
    {
        if (details is null || details.EditFailures.Count == 0) return null;
        return details.EditFailures.FirstOrDefault(e => e.IsModifierAddressable());
    }

    private static AiExaminationDto ProjectToDto(AnthropicToolResult result, string promptVersion)
    {
        var args = result.Arguments;

        // A value outside the tool's enum is never passed on: it escalates to a human.
        var disposition = AiDispositions.Normalize(args["recommended_disposition"]?.GetValue<string>());
        var confidence = args["confidence_score"]?.GetValue<double>() ?? 0.0;
        var rationale = args["rationale"]?.GetValue<string>();

        var citations = new List<string>();
        if (args["policy_citations"] is JsonArray citationsArray)
        {
            foreach (var c in citationsArray)
            {
                var citation = c?.GetValue<string>();
                if (!string.IsNullOrEmpty(citation)) citations.Add(citation);
            }
        }

        // Defensive clamp: model schema enforces 0–1 but we don't trust the wire.
        if (confidence < 0) confidence = 0;
        if (confidence > 1) confidence = 1;

        return new AiExaminationDto
        {
            RecommendedDisposition = disposition,
            ConfidenceScore = confidence,
            Rationale = rationale,
            PolicyCitations = citations,
            ModelId = result.ModelId,
            PromptVersion = promptVersion,
            GeneratedAt = DateTime.UtcNow
        };
    }

    /// <summary>The write-back to claims-service; a transport failure is transient (retried by the consumer).</summary>
    private async Task<bool> WriteBackAsync(ClaimPendedEvent evt, AiExaminationDto examination, CancellationToken ct)
    {
        try
        {
            return await _claimsClient.SetAiExaminationAsync(evt.ClaimId, evt.TenantId, examination, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogError(ex, "Transport error writing the AI examination for claim {ClaimId}; the event will be retried", evt.ClaimId);
            throw new ExaminerTransientException($"claims-service unreachable writing the AI examination for claim {evt.ClaimId}", ex);
        }
    }

    private async Task WriteFallbackAsync(ClaimPendedEvent evt, CancellationToken ct, string reason)
    {
        var fallback = new AiExaminationDto
        {
            RecommendedDisposition = AiDispositions.EscalateToHuman,
            ConfidenceScore = 0,
            Rationale = reason,
            PolicyCitations = new List<string>(),
            ModelId = null,
            PromptVersion = _promptBuilder.PromptVersion,
            GeneratedAt = DateTime.UtcNow
        };

        var written = await WriteBackAsync(evt, fallback, ct);
        if (written)
        {
            // EscalateToHuman is still a terminal AI recommendation —
            // downstream consumers (5.10 remittance) decide what to do
            // with it. Emit the completion event so they're not left
            // waiting for one that never comes.
            await _eventPublisher.PublishCompletedAsync(
                evt.ClaimId, evt.TenantId, fallback, evt.EventId, ct);
        }
    }
}
