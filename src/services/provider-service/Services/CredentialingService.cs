using System.Text.Json;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Json;
using ProviderService.Models;
using ProviderService.Models.CredentialingPayloads;
using ProviderService.Repositories;

namespace ProviderService.Services;

/// <summary>
/// Workflow orchestration for the credentialing event chain (capability
/// 5.6). Each method follows the canonical write order:
/// <list type="number">
///   <item>Read the chain ascending by Version.</item>
///   <item>Project the pre-state.</item>
///   <item>Validate the request against the pre-state.</item>
///   <item>Build a typed event with deterministic EventId.</item>
///   <item>Publish via <see cref="ICredentialingEventPublisher"/> (idempotent).</item>
///   <item>For status-changing events: re-project including the new event,
///         then patch the flat-field projection on <see cref="Provider"/>
///         via <see cref="IProviderRepository.UpdateCredentialingProjectionAsync"/>.</item>
/// </list>
/// The publisher is the system-of-record; the projection patch is a
/// best-effort denormalization for fast reads. If the patch fails after
/// the event lands, the event remains and the next transition recovers.
/// </summary>
public interface ICredentialingService
{
    Task<CredentialingEvent> SubmitApplicationAsync(
        string tenantId, string providerId,
        SubmitApplicationRequest request,
        string? actorId, string? correlationId, CancellationToken ct = default);

    Task<CredentialingEvent> RecordPrimarySourceVerificationAsync(
        string tenantId, string providerId,
        RecordPrimarySourceVerificationRequest request,
        string? actorId, string? correlationId, CancellationToken ct = default);

    Task<CredentialingEvent> ScheduleCommitteeReviewAsync(
        string tenantId, string providerId,
        ScheduleCommitteeReviewRequest request,
        string? actorId, string? correlationId, CancellationToken ct = default);

    Task<CredentialingEvent> RecordDecisionAsync(
        string tenantId, string providerId,
        RecordDecisionRequest request,
        string? actorId, string? correlationId, CancellationToken ct = default);

    Task<CredentialingEvent> TriggerRecredentialingAsync(
        string tenantId, string providerId,
        TriggerRecredentialingRequest request,
        string? actorId, string? correlationId, CancellationToken ct = default);

    Task<CredentialingEvent> WithdrawApplicationAsync(
        string tenantId, string providerId,
        string applicationEventId,
        WithdrawApplicationRequest request,
        string? actorId, string? correlationId, CancellationToken ct = default);

    Task<CredentialingProjectionResult> GetCurrentStatusAsync(
        string tenantId, string providerId, CancellationToken ct = default);

    /// <summary>
    /// Project the credentialing chain as of <paramref name="asOfDate"/>.
    /// Capability 5.6 enforcement consumer: claims-service calls this with
    /// the claim's service date so a provider credentialed AFTER the
    /// service date doesn't auto-pay an earlier-dated claim.
    /// </summary>
    /// <remarks>
    /// Trivial sibling of <see cref="GetCurrentStatusAsync"/>; the
    /// projector already accepts arbitrary <c>asOf</c> values. No I/O
    /// difference between the two paths beyond the date passed through.
    /// </remarks>
    Task<CredentialingProjectionResult> GetStatusAsOfAsync(
        string tenantId, string providerId, DateTimeOffset asOfDate, CancellationToken ct = default);

    Task<CredentialingHistoryPage> GetHistoryAsync(
        string tenantId, string providerId,
        string? continuationToken, int limit, CancellationToken ct = default);
}

/// <summary>
/// Thrown when a credentialing-workflow operation is invalid for the
/// current chain state (e.g. recording a decision without an open
/// application, triggering re-credentialing without a prior approval).
/// Mapped to HTTP 400 by the controller.
/// </summary>
public sealed class CredentialingValidationException : InvalidOperationException
{
    public CredentialingValidationException(string message) : base(message) { }
}

/// <summary>
/// Thrown when a credentialing-workflow operation targets a provider
/// that does not exist in the tenant. Mapped to HTTP 404 by the
/// controller. Distinct from
/// <see cref="CredentialingValidationException"/> so callers can tell
/// "wrong state" apart from "wrong target."
/// </summary>
public sealed class CredentialingNotFoundException : InvalidOperationException
{
    public CredentialingNotFoundException(string message) : base(message) { }
}

public sealed class CredentialingService : ICredentialingService
{
    private readonly ICredentialingEventRepository _eventRepository;
    private readonly ICredentialingEventPublisher _eventPublisher;
    private readonly IProviderRepository _providerRepository;
    private readonly CredentialingProjector _projector;
    private readonly ILogger<CredentialingService> _logger;

    public CredentialingService(
        ICredentialingEventRepository eventRepository,
        ICredentialingEventPublisher eventPublisher,
        IProviderRepository providerRepository,
        CredentialingProjector projector,
        ILogger<CredentialingService> logger)
    {
        _eventRepository = eventRepository;
        _eventPublisher = eventPublisher;
        _providerRepository = providerRepository;
        _projector = projector;
        _logger = logger;
    }

    public async Task<CredentialingEvent> SubmitApplicationAsync(
        string tenantId, string providerId,
        SubmitApplicationRequest request,
        string? actorId, string? correlationId, CancellationToken ct = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        await EnsureProviderExistsAsync(providerId);

        var chain = await _eventRepository.ListAscendingAsync(tenantId, providerId, ct);
        var preState = _projector.Project(chain, DateTimeOffset.UtcNow);
        if (preState.CurrentApplicationEventId != null)
        {
            throw new CredentialingValidationException(
                $"Provider {providerId} already has an open credentialing application " +
                $"(EventId={preState.CurrentApplicationEventId}). Withdraw it before submitting a new one.");
        }

        var submittedAt = (request.SubmittedAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var payload = new ApplicationSubmittedPayload(
            SubmittedAt: submittedAt,
            ApplicationSource: request.ApplicationSource,
            SupportingDocuments: request.SupportingDocuments);

        var evt = new CredentialingEvent
        {
            TenantId = tenantId,
            ProviderId = providerId,
            EventId = CredentialingEvent.BuildApplicationSubmittedEventId(providerId, submittedAt),
            EventType = CredentialingEventType.ApplicationSubmitted,
            OccurredAt = submittedAt.UtcDateTime,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = SerializePayload(payload),
        };

        var published = await _eventPublisher.PublishAsync(evt, ct);
        await PatchProjectionAsync(tenantId, providerId, chain, published, ct);
        return published;
    }

    public async Task<CredentialingEvent> RecordPrimarySourceVerificationAsync(
        string tenantId, string providerId,
        RecordPrimarySourceVerificationRequest request,
        string? actorId, string? correlationId, CancellationToken ct = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        await EnsureProviderExistsAsync(providerId);

        var chain = await _eventRepository.ListAscendingAsync(tenantId, providerId, ct);
        var preState = _projector.Project(chain, DateTimeOffset.UtcNow);
        if (preState.CurrentApplicationEventId == null)
        {
            throw new CredentialingValidationException(
                $"Provider {providerId} has no open credentialing application; " +
                "cannot record primary-source verification.");
        }

        var verifiedAt = (request.VerifiedAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var payload = new PrimarySourceVerificationPayload(
            VerifiedAt: verifiedAt,
            VerificationVendor: request.VerificationVendor,
            VerifiedItems: request.VerifiedItems,
            Evidence: request.Evidence);

        var evt = new CredentialingEvent
        {
            TenantId = tenantId,
            ProviderId = providerId,
            EventId = CredentialingEvent.BuildPrimarySourceVerificationEventId(
                providerId, preState.CurrentApplicationEventId, verifiedAt),
            EventType = CredentialingEventType.PrimarySourceVerificationCompleted,
            OccurredAt = verifiedAt.UtcDateTime,
            ApplicationEventId = preState.CurrentApplicationEventId,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = SerializePayload(payload),
        };

        // Status-neutral event — no projection patch.
        return await _eventPublisher.PublishAsync(evt, ct);
    }

    public async Task<CredentialingEvent> ScheduleCommitteeReviewAsync(
        string tenantId, string providerId,
        ScheduleCommitteeReviewRequest request,
        string? actorId, string? correlationId, CancellationToken ct = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        await EnsureProviderExistsAsync(providerId);

        var chain = await _eventRepository.ListAscendingAsync(tenantId, providerId, ct);
        var preState = _projector.Project(chain, DateTimeOffset.UtcNow);
        if (preState.CurrentApplicationEventId == null)
        {
            throw new CredentialingValidationException(
                $"Provider {providerId} has no open credentialing application; " +
                "cannot schedule a committee review.");
        }

        var scheduledFor = request.ScheduledFor.ToUniversalTime();
        var payload = new CommitteeReviewScheduledPayload(
            ScheduledFor: scheduledFor,
            CommitteeId: request.CommitteeId,
            AgendaReference: request.AgendaReference);

        var evt = new CredentialingEvent
        {
            TenantId = tenantId,
            ProviderId = providerId,
            EventId = CredentialingEvent.BuildCommitteeReviewScheduledEventId(
                providerId, preState.CurrentApplicationEventId, scheduledFor),
            EventType = CredentialingEventType.CommitteeReviewScheduled,
            OccurredAt = DateTime.UtcNow,
            ApplicationEventId = preState.CurrentApplicationEventId,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = SerializePayload(payload),
        };

        return await _eventPublisher.PublishAsync(evt, ct);
    }

    public async Task<CredentialingEvent> RecordDecisionAsync(
        string tenantId, string providerId,
        RecordDecisionRequest request,
        string? actorId, string? correlationId, CancellationToken ct = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.Decision != CredentialingDecision.Approved && request.Decision != CredentialingDecision.Denied)
        {
            throw new CredentialingValidationException(
                "Decision must be Approved or Denied.");
        }
        if (string.IsNullOrEmpty(request.DecisionAuthorityId))
        {
            throw new CredentialingValidationException(
                "DecisionAuthorityId is required.");
        }
        // Committee decisions are audit-grade — minutes and member roster
        // must be captured at write time. The other authority types are
        // single-actor paths where these fields don't apply.
        if (request.DecisionAuthorityType == DecisionAuthorityType.CredentialingCommittee)
        {
            if (request.CommitteeMembers == null || request.CommitteeMembers.Count == 0)
            {
                throw new CredentialingValidationException(
                    "CommitteeMembers is required for CredentialingCommittee decisions.");
            }
            if (string.IsNullOrEmpty(request.DecisionMinuteReference))
            {
                throw new CredentialingValidationException(
                    "DecisionMinuteReference is required for CredentialingCommittee decisions.");
            }
        }
        await EnsureProviderExistsAsync(providerId);

        var chain = await _eventRepository.ListAscendingAsync(tenantId, providerId, ct);
        var preState = _projector.Project(chain, DateTimeOffset.UtcNow);

        var decidedAt = (request.DecidedAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var applicationEventId = preState.CurrentApplicationEventId;

        // The legacy PUT /credentialing path uses DelegatedAuthority to
        // skip the "open application required" guard. When no chain is
        // open, synthesize a paired ApplicationSubmitted event with a
        // deterministic EventId so retries collapse, then proceed with
        // the decision. The synthesized application is filtered out by
        // the projector (defense in depth) — it never appears as an
        // open chain even if a future bug orphans one.
        if (applicationEventId == null)
        {
            if (request.DecisionAuthorityType != DecisionAuthorityType.DelegatedAuthority)
            {
                throw new CredentialingValidationException(
                    $"Provider {providerId} has no open credentialing application; " +
                    "submit an application before recording a decision.");
            }

            var decisionEventId = CredentialingEvent.BuildDecisionRecordedEventId(
                providerId, applicationEventId: "synthesized", decidedAt);
            var synthesizedEventId = CredentialingEvent.BuildSynthesizedApplicationSubmittedEventId(
                providerId, decisionEventId);

            var synthesizedPayload = new ApplicationSubmittedPayload(
                SubmittedAt: decidedAt,
                ApplicationSource: "DelegatedAuthority",
                SupportingDocuments: null,
                SynthesizedForDelegatedAuthority: true);

            var synthesized = new CredentialingEvent
            {
                TenantId = tenantId,
                ProviderId = providerId,
                EventId = synthesizedEventId,
                EventType = CredentialingEventType.ApplicationSubmitted,
                OccurredAt = decidedAt.UtcDateTime,
                ActorId = actorId,
                CorrelationId = correlationId,
                Payload = SerializePayload(synthesizedPayload),
            };

            await _eventPublisher.PublishAsync(synthesized, ct);
            applicationEventId = synthesizedEventId;
        }

        var payload = new DecisionRecordedPayload(
            Decision: request.Decision,
            DecidedAt: decidedAt,
            CredentialingDate: request.CredentialingDate
                ?? (request.Decision == CredentialingDecision.Approved ? decidedAt.UtcDateTime : null),
            RecredentialingDueDate: request.RecredentialingDueDate,
            DecisionAuthorityType: request.DecisionAuthorityType,
            DecisionAuthorityId: request.DecisionAuthorityId,
            CommitteeMembers: request.CommitteeMembers,
            DecisionMinuteReference: request.DecisionMinuteReference,
            DenialReason: request.DenialReason);

        var evt = new CredentialingEvent
        {
            TenantId = tenantId,
            ProviderId = providerId,
            EventId = CredentialingEvent.BuildDecisionRecordedEventId(
                providerId, applicationEventId, decidedAt),
            EventType = CredentialingEventType.DecisionRecorded,
            OccurredAt = decidedAt.UtcDateTime,
            ApplicationEventId = applicationEventId,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = SerializePayload(payload),
        };

        var published = await _eventPublisher.PublishAsync(evt, ct);
        await PatchProjectionAsync(tenantId, providerId, chain, published, ct);
        return published;
    }

    public async Task<CredentialingEvent> TriggerRecredentialingAsync(
        string tenantId, string providerId,
        TriggerRecredentialingRequest request,
        string? actorId, string? correlationId, CancellationToken ct = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        await EnsureProviderExistsAsync(providerId);

        var chain = await _eventRepository.ListAscendingAsync(tenantId, providerId, ct);
        var preState = _projector.Project(chain, DateTimeOffset.UtcNow);

        if (preState.CurrentApplicationEventId != null)
        {
            throw new CredentialingValidationException(
                $"Provider {providerId} already has an open credentialing application " +
                "or pending re-credentialing chain.");
        }
        if (preState.LastDecidedAt == null
            || preState.LastDecisionAuthorityType == null)
        {
            throw new CredentialingValidationException(
                $"Provider {providerId} has no prior decision; " +
                "submit an initial application instead of triggering re-credentialing.");
        }

        var triggeredAt = (request.TriggeredAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var predecessorEventId = chain
            .LastOrDefault(e => e.EventType == CredentialingEventType.DecisionRecorded)
            ?.EventId;

        var payload = new RecredentialingTriggeredPayload(
            TriggeredAt: triggeredAt,
            Reason: request.Reason);

        var evt = new CredentialingEvent
        {
            TenantId = tenantId,
            ProviderId = providerId,
            EventId = CredentialingEvent.BuildRecredentialingTriggeredEventId(providerId, triggeredAt),
            EventType = CredentialingEventType.RecredentialingTriggered,
            OccurredAt = triggeredAt.UtcDateTime,
            PredecessorEventId = predecessorEventId,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = SerializePayload(payload),
        };

        var published = await _eventPublisher.PublishAsync(evt, ct);
        await PatchProjectionAsync(tenantId, providerId, chain, published, ct);
        return published;
    }

    public async Task<CredentialingEvent> WithdrawApplicationAsync(
        string tenantId, string providerId,
        string applicationEventId,
        WithdrawApplicationRequest request,
        string? actorId, string? correlationId, CancellationToken ct = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrEmpty(applicationEventId))
            throw new ArgumentException("applicationEventId is required.", nameof(applicationEventId));
        await EnsureProviderExistsAsync(providerId);

        var chain = await _eventRepository.ListAscendingAsync(tenantId, providerId, ct);
        var preState = _projector.Project(chain, DateTimeOffset.UtcNow);

        if (preState.CurrentApplicationEventId == null)
        {
            throw new CredentialingValidationException(
                $"Provider {providerId} has no open credentialing application to withdraw.");
        }
        if (!string.Equals(preState.CurrentApplicationEventId, applicationEventId, StringComparison.Ordinal))
        {
            throw new CredentialingValidationException(
                $"applicationEventId {applicationEventId} does not match the open application " +
                $"({preState.CurrentApplicationEventId}).");
        }

        var withdrawnAt = (request.WithdrawnAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var payload = new ApplicationWithdrawnPayload(
            WithdrawnAt: withdrawnAt,
            Reason: request.Reason);

        var evt = new CredentialingEvent
        {
            TenantId = tenantId,
            ProviderId = providerId,
            EventId = CredentialingEvent.BuildApplicationWithdrawnEventId(
                providerId, applicationEventId, withdrawnAt),
            EventType = CredentialingEventType.ApplicationWithdrawn,
            OccurredAt = withdrawnAt.UtcDateTime,
            ApplicationEventId = applicationEventId,
            ActorId = actorId,
            CorrelationId = correlationId,
            Payload = SerializePayload(payload),
        };

        var published = await _eventPublisher.PublishAsync(evt, ct);
        await PatchProjectionAsync(tenantId, providerId, chain, published, ct);
        return published;
    }

    public async Task<CredentialingProjectionResult> GetCurrentStatusAsync(
        string tenantId, string providerId, CancellationToken ct = default)
    {
        var chain = await _eventRepository.ListAscendingAsync(tenantId, providerId, ct);
        return _projector.Project(chain, DateTimeOffset.UtcNow);
    }

    public async Task<CredentialingProjectionResult> GetStatusAsOfAsync(
        string tenantId, string providerId, DateTimeOffset asOfDate, CancellationToken ct = default)
    {
        var chain = await _eventRepository.ListAscendingAsync(tenantId, providerId, ct);
        return _projector.Project(chain, asOfDate);
    }

    public Task<CredentialingHistoryPage> GetHistoryAsync(
        string tenantId, string providerId,
        string? continuationToken, int limit, CancellationToken ct = default)
        => _eventRepository.ListHistoryDescendingAsync(tenantId, providerId, continuationToken, limit, ct);

    private async Task EnsureProviderExistsAsync(string providerId)
    {
        // Status-changing operations must target a real provider —
        // otherwise the chain accumulates events for an entity that
        // can never present a flat-field projection (the patch will
        // return false). Reads (GetCurrentStatusAsync, GetHistoryAsync)
        // intentionally do NOT enforce this so admins can inspect a
        // chain even after the underlying provider row has been
        // deleted.
        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider == null)
        {
            throw new CredentialingNotFoundException(
                $"Provider {providerId} not found.");
        }
    }

    private async Task PatchProjectionAsync(
        string tenantId,
        string providerId,
        IReadOnlyList<CredentialingEvent> existingChain,
        CredentialingEvent published,
        CancellationToken ct)
    {
        // Re-project including the just-published event and patch the
        // flat-field projection on Provider. The projection is
        // best-effort — if the patch fails (e.g. no Active head exists
        // because the provider is still a Draft chain), log a warning
        // and continue. The event is the system-of-record; the next
        // transition will reconcile.
        var withNew = existingChain
            .Where(e => e.EventId != published.EventId)
            .Append(published)
            .OrderBy(e => e.Version)
            .ToList();
        var projection = _projector.Project(withNew, DateTimeOffset.UtcNow);

        try
        {
            var patched = await _providerRepository.UpdateCredentialingProjectionAsync(
                tenantId,
                providerId,
                projection.Status,
                projection.CredentialingDate,
                projection.RecredentialingDueDate,
                ct);
            if (!patched)
            {
                _logger.LogWarning(
                    "CredentialingProjection patch returned false for {Tenant}:{Provider}; " +
                    "no Active head row found. Event {EventId} ({EventType}) was still appended.",
                    Sanitize(tenantId), Sanitize(providerId), Sanitize(published.EventId), published.EventType);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "CredentialingProjection patch failed for {Tenant}:{Provider}; " +
                "event {EventId} ({EventType}) is appended but the flat projection is stale.",
                Sanitize(tenantId), Sanitize(providerId), Sanitize(published.EventId), published.EventType);
        }
    }

    private static JsonObject? SerializePayload<T>(T payload) where T : class
    {
        if (payload == null) return null;
        var json = JsonSerializer.Serialize(payload, CloudHealthOfficeJsonOptions.DefaultOptions);
        return JsonNode.Parse(json) as JsonObject;
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
