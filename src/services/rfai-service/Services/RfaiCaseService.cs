using RfaiService.Models;
using RfaiService.Repositories;

namespace RfaiService.Services;

/// <summary>Outcome of an idempotent request-creation call.</summary>
public sealed record RfaiEnsureResult
{
    public required RfaiCase Case { get; init; }

    /// <summary>True when THIS call created the case; false when it replayed onto an existing one.</summary>
    public required bool Created { get; init; }

    /// <summary>
    /// True when creation was skipped because a cycle for this authorization was
    /// already open. The caller gets that cycle rather than a second one.
    /// </summary>
    public bool ReusedOpenCycle { get; init; }
}

/// <summary>
/// Orchestrates the RFAI case aggregate: repository reads/writes, the pure rules
/// in <see cref="RfaiCaseLifecycle"/>, and the one downstream announcement that
/// lets a prior authorization resume review.
///
/// Every intake path — the internal API, the CDex surface in fhir-service, and a
/// 275 correlated by attachment-service — funnels through here, so the
/// invariants (one open cycle per authorization, idempotency by submission id,
/// artifact cap, exactly one resume announcement per cycle) hold whichever door
/// the response came in.
/// </summary>
public interface IRfaiCaseService
{
    /// <summary>
    /// Creates the additional-information request for a review decision, or
    /// returns the existing one. Safe to call repeatedly for the same decision
    /// and safe for two workers to call concurrently.
    /// </summary>
    Task<RfaiEnsureResult> EnsureRequestAsync(RfaiCreationRequest request, CancellationToken ct = default);

    /// <summary>Records that the request was handed to the provider/system.</summary>
    Task<RfaiCase?> MarkDeliveredAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>
    /// Accepts (or recognises as a replay) a response against a case. Publishes
    /// the resume-review announcement only on the transition into DocsReceived.
    /// </summary>
    Task<RfaiIntakeResult?> RecordResponseAsync(
        string tenantId, string id, IReadOnlyList<RfaiResponseArtifact> artifacts,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class RfaiCaseService : IRfaiCaseService
{
    private readonly IRfaiRepository _repository;
    private readonly IKafkaProducerService? _kafka;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RfaiCaseService> _logger;
    private readonly TimeProvider _clock;

    public RfaiCaseService(
        IRfaiRepository repository,
        IConfiguration configuration,
        ILogger<RfaiCaseService> logger,
        IKafkaProducerService? kafka = null,
        TimeProvider? clock = null)
    {
        _repository = repository;
        _configuration = configuration;
        _logger = logger;
        _kafka = kafka;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public async Task<RfaiEnsureResult> EnsureRequestAsync(
        RfaiCreationRequest request, CancellationToken ct = default)
    {
        var validation = RfaiCaseLifecycle.Validate(request);
        if (!validation.IsValid)
            throw new ArgumentException(validation.Error, nameof(request));

        var existingCases = await _repository.GetByAuthNumberAsync(request.TenantId, request.AuthNumber);

        // 1. Exact replay of the same creating event, in ANY status. Recognising a
        //    replay against a closed cycle matters as much as against an open one:
        //    a redelivered A4 event must not open a second cycle just because the
        //    first one has since been answered and closed.
        if (!string.IsNullOrWhiteSpace(request.CorrelationKey))
        {
            var replayed = existingCases.FirstOrDefault(c =>
                string.Equals(c.CorrelationKey, request.CorrelationKey, StringComparison.Ordinal));

            if (replayed is not null)
                return new RfaiEnsureResult { Case = replayed, Created = false };
        }

        // 2. A different decision, but a cycle is still open. One open request per
        //    authorization: two concurrent requests would leave the provider
        //    guessing which one their documents answer.
        var open = existingCases.FirstOrDefault(c => c.IsOpen);
        if (open is not null)
        {
            _logger.LogInformation(
                "RFAI request for auth {AuthNumber} reused open case {Id} (sequence {Sequence})",
                Sanitize(request.AuthNumber), Sanitize(open.Id), open.Sequence);

            return new RfaiEnsureResult { Case = open, Created = false, ReusedOpenCycle = true };
        }

        // 3. A new cycle. Its sequence continues the history rather than replacing it.
        var candidate = RfaiCaseLifecycle.Create(
            request, RfaiCaseLifecycle.NextSequence(existingCases), UtcNow);

        var (stored, created) = await _repository.CreateIfAbsentAsync(candidate);

        _logger.LogInformation(
            "RFAI case {Id} {Verb} for auth {AuthNumber} (tenant {TenantId}, sequence {Sequence}, source {Source})",
            Sanitize(stored.Id), created ? "created" : "already existed",
            Sanitize(stored.AuthNumber), Sanitize(stored.TenantId),
            stored.Sequence, Sanitize(stored.RequestSource));

        return new RfaiEnsureResult { Case = stored, Created = created };
    }

    /// <inheritdoc />
    public async Task<RfaiCase?> MarkDeliveredAsync(
        string tenantId, string id, CancellationToken ct = default)
    {
        var rfaiCase = await _repository.GetByIdAsync(tenantId, id);
        if (rfaiCase is null) return null;

        RfaiCaseLifecycle.MarkDelivered(rfaiCase, UtcNow);
        var updated = await _repository.UpdateAsync(rfaiCase);

        _logger.LogInformation(
            "RFAI case {Id} delivered to requester (delivery {Count})",
            Sanitize(id), updated.DeliveryCount);

        return updated;
    }

    /// <inheritdoc />
    public async Task<RfaiIntakeResult?> RecordResponseAsync(
        string tenantId, string id, IReadOnlyList<RfaiResponseArtifact> artifacts,
        CancellationToken ct = default)
    {
        var rfaiCase = await _repository.GetByIdAsync(tenantId, id);
        if (rfaiCase is null) return null;

        var result = RfaiCaseLifecycle.OfferResponse(rfaiCase, artifacts, UtcNow);

        if (!result.RequiresPersist)
        {
            _logger.LogInformation(
                "RFAI case {Id} response not recorded: {Outcome}",
                Sanitize(id), result.Outcome);
            return result;
        }

        await _repository.UpdateAsync(rfaiCase);

        _logger.LogInformation(
            "RFAI case {Id} recorded {Count} artifact(s); status={Status}",
            Sanitize(id), result.Recorded.Count, rfaiCase.Status);

        // The announcement is published ONLY on the edge into DocsReceived, so a
        // replayed or supplementary submission cannot restart the review clock a
        // second time. Publishing after the write means a failure here leaves a
        // durable case whose response is recorded and can be re-announced; the
        // consumer's own update is idempotent.
        if (result.TransitionedToDocsReceived)
            await PublishDocsReceivedAsync(rfaiCase, result);

        return result;
    }

    private async Task PublishDocsReceivedAsync(RfaiCase rfaiCase, RfaiIntakeResult result)
    {
        if (_kafka is null) return;

        var message = new
        {
            tenantId = rfaiCase.TenantId,
            rfaiCaseId = rfaiCase.Id,
            authNumber = rfaiCase.AuthNumber,
            trackingId = rfaiCase.TrackingId,
            receivedAt = rfaiCase.RespondedAt ?? UtcNow,
            // Control numbers and submission ids only — never content, never a
            // filename, never a clinical code the artifact happens to carry.
            attachmentIds = rfaiCase.ReceivedAttachments
                .Select(a => a.AttachmentControlNumber ?? a.SubmissionId ?? string.Empty)
                .ToList(),
            allRequestedItemsReceived = RfaiCaseLifecycle.AllRequiredItemsAnswered(rfaiCase),
        };

        var topic = _configuration["Kafka:RfaiDocsReceivedTopic"] ?? "rfai-docs-received";

        try
        {
            await _kafka.SendAsync(topic, rfaiCase.AuthNumber, message);
        }
        catch (Exception ex)
        {
            // The response is already durable. Losing the announcement delays the
            // authorization returning to review; it does not lose the documents,
            // and re-announcing is safe because the consumer's update is
            // idempotent. Failing the caller here would invite a retry that
            // re-uploads content already stored.
            _logger.LogError(ex,
                "RFAI case {Id} recorded a response but the resume-review announcement failed",
                Sanitize(rfaiCase.Id));
        }
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);
}
