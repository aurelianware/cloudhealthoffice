using System.Diagnostics;
using ClaimsService.Adapters;
using ClaimsService.Models;
using ClaimsService.Models.Messaging;
using CloudHealthOffice.Infrastructure.Messaging;

namespace ClaimsService.Services;

/// <summary>
/// Orchestrates canonical claim submission for the v1 surface
/// (capability 5.3). Sits between the controller and the adapter:
/// validates the inbound <see cref="AdapterClaim"/>, resolves the
/// tenant-routed <see cref="IClaimAdapter"/>, calls
/// <c>SubmitClaimAsync</c>, and emits a <c>ClaimVersionSubmitted</c>
/// event via the existing 5.1a <see cref="IClaimVersionEventPublisher"/>
/// on success.
///
/// <para>
/// Both the canonical V1 endpoint and the legacy
/// <c>POST /api/claims</c> endpoint route through this single
/// orchestration path so the version-event chain has no gaps for
/// legacy-submitted claims while the legacy controller remains
/// operational (it is removed by capability 5.13).
/// </para>
/// </summary>
public interface IClaimSubmissionService
{
    /// <summary>
    /// Submit a claim through the tenant-routed adapter and emit a
    /// <c>ClaimVersionSubmitted</c> event on success. Returns a
    /// structured <see cref="ClaimSubmissionResult"/> rather than
    /// throwing on validation failure so the controller can map to
    /// HTTP status codes without catching exceptions on the happy
    /// path.
    /// </summary>
    /// <param name="claim">Canonical vendor-neutral claim payload.</param>
    /// <param name="tenantId">Tenant id resolved by the controller from
    /// <c>HttpContext.Items["TenantId"]</c>.</param>
    /// <param name="actorId">Caller identity (sub claim, X-User-Id
    /// header, or "system") — flows into the version event ActorId
    /// field for the audit chain.</param>
    /// <param name="correlationId">Correlation id from the request
    /// activity or X-Correlation-Id header — flows into the version
    /// event so downstream consumers can join.</param>
    Task<ClaimSubmissionResult> SubmitAsync(
        AdapterClaim claim,
        string tenantId,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default);
}

/// <summary>
/// Failure-mode discriminator on <see cref="ClaimSubmissionResult"/>.
/// Lets the controller map results to specific HTTP status codes
/// without re-deriving the disposition from error contents.
/// </summary>
public enum ClaimSubmissionFailureKind
{
    /// <summary>Structural validation failed; controller maps to 400.</summary>
    Validation = 1,

    /// <summary>Tenant routes to a stub vendor adapter that does not
    /// implement submission yet; controller maps to 501.</summary>
    NotImplemented = 2,
}

/// <summary>
/// Result envelope returned by <see cref="IClaimSubmissionService.SubmitAsync"/>.
/// On success, <see cref="Claim"/> carries the canonical post-create
/// claim version (with assigned ids and seeded version chain). On
/// failure, <see cref="FailureKind"/> + <see cref="Errors"/> carry
/// enough detail for the controller to produce a structured response.
/// </summary>
public class ClaimSubmissionResult
{
    public bool Success { get; set; }

    /// <summary>Created claim with assigned id, ClaimVersionId, VersionNumber=1, VersionState=Submitted. Null on failure.</summary>
    public AdapterClaim? Claim { get; set; }

    /// <summary>Discriminator for HTTP status mapping; null on success.</summary>
    public ClaimSubmissionFailureKind? FailureKind { get; set; }

    public IReadOnlyList<ValidationError> Errors { get; set; } = Array.Empty<ValidationError>();

    public static ClaimSubmissionResult Ok(AdapterClaim claim) =>
        new() { Success = true, Claim = claim };

    public static ClaimSubmissionResult ValidationFailed(IReadOnlyList<ValidationError> errors) =>
        new() { Success = false, FailureKind = ClaimSubmissionFailureKind.Validation, Errors = errors };

    public static ClaimSubmissionResult AdapterNotImplemented(string message) =>
        new()
        {
            Success = false,
            FailureKind = ClaimSubmissionFailureKind.NotImplemented,
            Errors = new[]
            {
                new ValidationError
                {
                    Field = string.Empty,
                    Code = "AdapterNotImplemented",
                    Message = message
                }
            }
        };
}

/// <summary>
/// Field-level validation error surfaced by the submission service.
/// Mirrored in controller responses so callers can highlight specific
/// inputs.
/// </summary>
public class ValidationError
{
    /// <summary>Property path that failed validation (e.g. "MemberId", "ClaimLines[0].ProcedureCode").</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Stable error code (e.g. "Required", "MinCount", "InvalidDateRange").</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable message.</summary>
    public string Message { get; set; } = string.Empty;
}

public class ClaimSubmissionService : IClaimSubmissionService
{
    private readonly ClaimAdapterFactory _adapterFactory;
    private readonly IClaimVersionEventPublisher _eventPublisher;
    private readonly IMessageBus _messageBus;
    private readonly ILogger<ClaimSubmissionService> _logger;

    public ClaimSubmissionService(
        ClaimAdapterFactory adapterFactory,
        IClaimVersionEventPublisher eventPublisher,
        IMessageBus messageBus,
        ILogger<ClaimSubmissionService> logger)
    {
        _adapterFactory = adapterFactory;
        _eventPublisher = eventPublisher;
        _messageBus = messageBus;
        _logger = logger;
    }

    public async Task<ClaimSubmissionResult> SubmitAsync(
        AdapterClaim claim,
        string tenantId,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (string.IsNullOrEmpty(tenantId))
            throw new ArgumentException("tenantId is required", nameof(tenantId));

        // Force the tenantId onto the claim so adapter implementations and
        // the repository see a consistent value regardless of what the
        // caller supplied in the body. The HttpContext-resolved tenant is
        // authoritative.
        claim.TenantId = tenantId;

        // Compute total charge from the lines BEFORE validating, so the
        // caller-supplied total doesn't influence the validation outcome
        // (legacy POST has always recomputed; preserve that semantic).
        if (claim.ClaimLines is { Count: > 0 })
        {
            claim.TotalChargeAmount = claim.ClaimLines.Sum(l => l.ChargeAmount * l.Units);
        }

        var validationErrors = Validate(claim);
        if (validationErrors.Count > 0)
        {
            _logger.LogInformation(
                "Claim submission rejected: {ErrorCount} validation error(s) for member {Member}",
                validationErrors.Count, SanitizeForLog(claim.MemberId));
            return ClaimSubmissionResult.ValidationFailed(validationErrors);
        }

        // Per-hop timing for the Submit chain -- see ClaimVersionEventPublisher
        // for the matching breakdown of the three hops inside PublishVersionSubmittedAsync.
        // This is what found MongoDB's CPU limit as the real bottleneck behind
        // the "five sequential I/O hops" cost Part 10/11 disclosed but never
        // measured: claimInsert/eventPublish showed the same low-median,
        // huge-P95 shape as MongoDB's own cgroup throttling stats, not a cost
        // inherent to any single hop.
        var profileSw = Stopwatch.StartNew();
        var adapter = await _adapterFactory.GetAdapterAsync(tenantId, ct);
        var adapterResolveMs = profileSw.Elapsed.TotalMilliseconds;

        ClaimAdapterResponse adapterResponse;
        try
        {
            profileSw.Restart();
            adapterResponse = await adapter.SubmitClaimAsync(
                new ClaimSubmissionAdapterRequest
                {
                    TenantId = tenantId,
                    Claim = claim,
                    CorrelationId = correlationId,
                },
                ct);
        }
        catch (NotImplementedException ex)
        {
            // Vendor stub adapters (qnxt/facets/healthedge) signal "not
            // wired up yet" via NotImplementedException. Surface as 501
            // with the adapter's own message so operators know which
            // tenant configuration is the gap.
            _logger.LogWarning(ex,
                "Adapter for tenant {TenantId} does not implement claim submission",
                SanitizeForLog(tenantId));
            return ClaimSubmissionResult.AdapterNotImplemented(ex.Message);
        }

        var claimInsertMs = profileSw.Elapsed.TotalMilliseconds;

        var created = adapterResponse.Claim
            ?? throw new InvalidOperationException(
                $"Adapter '{adapterResponse.Platform}' returned a null claim from SubmitClaimAsync.");

        // Emit the ClaimVersionSubmitted audit event. The Mongo append-only
        // stream is the system-of-record for the version chain; this is
        // the first production consumer of IClaimVersionEventPublisher
        // (5.1a shipped the publisher with no callers).
        //
        // Degraded-mode posture: the claim row in the main store is the
        // source of truth. If event emission fails (Mongo outage, etc.)
        // we log loudly but DO NOT fail the submission — same posture as
        // the Kafka IClaimEventPublisher. The audit chain may have a gap
        // for the affected claim that operators can backfill from logs.
        profileSw.Restart();
        try
        {
            var domainClaim = created.ToClaim();
            await _eventPublisher.PublishVersionSubmittedAsync(domainClaim, actorId, correlationId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ClaimVersionSubmitted event emission failed for claim {ClaimId} (chain {ClaimVersionId}); " +
                "submission persisted, audit chain has a gap",
                SanitizeForLog(created.Id), SanitizeForLog(created.ClaimVersionId));
        }
        var eventPublishMs = profileSw.Elapsed.TotalMilliseconds;

        // 5.5 dual-emit — Service Bus topic notification triggers the
        // adjudication orchestrator. Mongo append-only above is the
        // system-of-record audit chain; this is the trigger transport.
        // Same degraded-mode posture: failure here logs but does not
        // fail the submission. Operators replay missed messages from
        // the audit chain.
        profileSw.Restart();
        try
        {
            var sbMessage = new ClaimVersionSubmittedMessage
            {
                TenantId = tenantId,
                ClaimId = created.Id,
                ClaimVersionId = created.ClaimVersionId,
                VersionNumber = created.VersionNumber,
                ActorId = actorId,
                CorrelationId = correlationId,
            };
            var sendOptions = new SendOptions(
                MessageId: $"submitted:{created.ClaimVersionId}",
                CorrelationId: correlationId,
                Properties: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ClaimVersionEventTopics.MessageTypeProperty] = ClaimVersionMessageTypes.Submitted,
                });

            await _messageBus.SendAsync(
                ClaimVersionEventTopics.TopicName, sbMessage, sendOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ClaimVersionSubmitted Service Bus emission failed for claim {ClaimId} (chain {ClaimVersionId}); " +
                "submission persisted, adjudication will not auto-trigger",
                SanitizeForLog(created.Id), SanitizeForLog(created.ClaimVersionId));
        }
        var sbSendMs = profileSw.Elapsed.TotalMilliseconds;

        _logger.LogDebug(
            "SubmitProfile.Submit adapterResolveMs={AdapterResolveMs} claimInsertMs={ClaimInsertMs} eventPublishMs={EventPublishMs} serviceBusSendMs={ServiceBusSendMs}",
            adapterResolveMs, claimInsertMs, eventPublishMs, sbSendMs);

        _logger.LogInformation(
            "Claim {ClaimId} submitted via adapter {Platform} (chain {ClaimVersionId} v{VersionNumber}) for tenant {TenantId}",
            SanitizeForLog(created.Id), SanitizeForLog(adapterResponse.Platform),
            SanitizeForLog(created.ClaimVersionId), created.VersionNumber, SanitizeForLog(tenantId));

        return ClaimSubmissionResult.Ok(created);
    }

    /// <summary>
    /// Structural validation only — field presence, basic shape,
    /// service-date sanity. Eligibility checks, code coherence, and
    /// member/plan validity are 5.4 (Pre-Adjudication Scrubbing) scope
    /// and not duplicated here.
    /// </summary>
    private static List<ValidationError> Validate(AdapterClaim claim)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(claim.MemberId))
        {
            errors.Add(new ValidationError
            {
                Field = nameof(AdapterClaim.MemberId),
                Code = "Required",
                Message = "MemberId is required"
            });
        }

        if (string.IsNullOrWhiteSpace(claim.BillingProviderNPI))
        {
            errors.Add(new ValidationError
            {
                Field = nameof(AdapterClaim.BillingProviderNPI),
                Code = "Required",
                Message = "BillingProviderNPI is required"
            });
        }

        if (claim.ServiceDateFrom != default &&
            claim.ServiceDateTo != default &&
            claim.ServiceDateFrom > claim.ServiceDateTo)
        {
            errors.Add(new ValidationError
            {
                Field = nameof(AdapterClaim.ServiceDateFrom),
                Code = "InvalidDateRange",
                Message = "ServiceDateFrom must be on or before ServiceDateTo"
            });
        }

        if (claim.ClaimLines is null || claim.ClaimLines.Count == 0)
        {
            errors.Add(new ValidationError
            {
                Field = nameof(AdapterClaim.ClaimLines),
                Code = "MinCount",
                Message = "Claim must have at least one service line"
            });
        }
        else
        {
            for (var i = 0; i < claim.ClaimLines.Count; i++)
            {
                var line = claim.ClaimLines[i];
                if (string.IsNullOrWhiteSpace(line.ProcedureCode))
                {
                    errors.Add(new ValidationError
                    {
                        Field = $"{nameof(AdapterClaim.ClaimLines)}[{i}].{nameof(AdapterClaimLine.ProcedureCode)}",
                        Code = "Required",
                        Message = "ProcedureCode is required on every service line"
                    });
                }
            }
        }

        return errors;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
