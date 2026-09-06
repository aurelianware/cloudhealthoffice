using System.Security.Cryptography;
using System.Text;
using AuthorizationService.Models;

namespace AuthorizationService.Services.Rfai;

/// <summary>
/// One item a reviewer is asking the provider for, as the 278 A4 decision states
/// it. Codes first: the description exists so a human can read the request, not
/// so a receiving system has to parse prose.
/// </summary>
public sealed record RequestedInformationItem
{
    /// <summary>X12 PWK attachment-type code (e.g. "AS" Admission Summary).</summary>
    public string? Code { get; init; }

    /// <summary>LOINC document-type code, for the FHIR/CDex representation.</summary>
    public string? LoincCode { get; init; }

    public string Description { get; init; } = string.Empty;

    public bool Required { get; init; } = true;

    /// <summary>The requested service line the question is about (HCPCS/CPT).</summary>
    public string? ServiceLineProcedureCode { get; init; }

    /// <summary>Diagnosis context for the question (ICD-10).</summary>
    public string? DiagnosisCode { get; init; }
}

/// <summary>
/// The request an A4 decision raises, as authorization-service asks rfai-service
/// to record it.
/// </summary>
public sealed record RfaiRequestCommand
{
    public required string TenantId { get; init; }
    public required string AuthNumber { get; init; }
    public string? AuthorizationId { get; init; }

    /// <summary>Identity of the DECISION, so a redelivery is recognised as one.</summary>
    public required string CorrelationKey { get; init; }

    public string? MemberId { get; init; }
    public string? RequestingProviderNpi { get; init; }
    public string? ReviewDecision { get; init; }
    public string? ReasonCode { get; init; }
    public string? ReasonDescription { get; init; }
    public string? RequestedBy { get; init; }
    public string RequestSource { get; init; } = "review-decision-a4";
    public DateTime? DueDate { get; init; }
    public string? Notes { get; init; }
    public List<RequestedInformationItem> RequestedItems { get; init; } = new();
}

/// <summary>What rfai-service recorded.</summary>
public sealed record RfaiRequestHandle
{
    public required string Id { get; init; }

    /// <summary>The provider-facing tracking id (attachment control number).</summary>
    public required string TrackingId { get; init; }

    /// <summary>False when the call replayed onto an existing request.</summary>
    public bool Created { get; init; }
}

/// <summary>
/// The seam onto rfai-service, which owns the additional-information record.
/// authorization-service does NOT keep a second copy of the request: it keeps
/// only the handle (<see cref="Authorization.RFAIReference"/>) that points at it.
/// </summary>
public interface IRfaiRequestGateway
{
    /// <summary>
    /// Creates the request, or returns the existing one. Idempotent by
    /// correlation key, and safe for two workers to call at once.
    /// Returns null when rfai-service could not be reached — see the
    /// coordinator's failure note.
    /// </summary>
    Task<RfaiRequestHandle?> EnsureRequestAsync(RfaiRequestCommand command, CancellationToken ct = default);
}

/// <summary>
/// Raises the additional-information request that a pended-for-information
/// decision implies, and stamps the authorization with the handle.
///
/// WHEN A REQUEST IS RAISED. Only for a decision that actually indicates one:
/// review decision A4 (pended, additional information required) AND a statement
/// of what is needed. A generic pended status is NOT enough — a decision that
/// says "pended" without naming any documentation has not asked the provider
/// for anything, and manufacturing a documentation request from it would put a
/// question to the provider that no reviewer posed. That case is recorded in the
/// audit trail and the authorization stays pended exactly as before.
///
/// IDEMPOTENCY. The correlation key is derived from the DECISION, preferring the
/// 278 response's own control number and falling back to a digest of the
/// decision's content. Two workers handling the same A4 event derive the same
/// key, address the same document in rfai-service, and exactly one insert wins;
/// a redelivered event replays onto the request the first delivery created,
/// whatever status it has since reached.
///
/// FAILURE. There is no outbox in this repository and the two stores cannot
/// participate in one transaction, so this does not pretend to atomicity. The
/// authorization's own decision is persisted by its caller regardless; if
/// raising the request fails, the authorization stays <c>Pended</c> with
/// <see cref="Authorization.RFAIIssued"/> false, which is the recoverable state:
/// replaying the decision — or an ordinary status update that lands on the same
/// pended-with-no-request condition — retries with the SAME correlation key, so
/// the retry cannot produce a second request. Nothing is lost and nothing is
/// duplicated; the cost of a failure is delay.
/// </summary>
public interface IPendedAuthorizationRfaiCoordinator
{
    /// <summary>
    /// Ensures the request exists for this authorization's current decision and
    /// stamps the handle on it. Returns true when the authorization was changed
    /// and must be written back.
    /// </summary>
    Task<bool> EnsureRequestForDecisionAsync(
        Authorization authorization,
        IReadOnlyList<RequestedInformationItem> requestedItems,
        DateTime? dueDate,
        string? decisionControlNumber,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class PendedAuthorizationRfaiCoordinator : IPendedAuthorizationRfaiCoordinator
{
    /// <summary>The X12 278 review decision that means "pended — send documentation".</summary>
    public const string AdditionalInformationRequiredDecision = "A4";

    private readonly IRfaiRequestGateway _gateway;
    private readonly ILogger<PendedAuthorizationRfaiCoordinator> _logger;
    private readonly TimeProvider _clock;

    public PendedAuthorizationRfaiCoordinator(
        IRfaiRequestGateway gateway,
        ILogger<PendedAuthorizationRfaiCoordinator> logger,
        TimeProvider? clock = null)
    {
        _gateway = gateway;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Whether this decision asks the provider for documentation. Both halves
    /// are required: the A4 decision code, and at least one thing being asked
    /// for.
    ///
    /// EVERY named item must be usable, not merely one of them. rfai-service
    /// refuses a request in which ANY item lacks a description, so a predicate
    /// that accepted a mixed list here would call a decision eligible and then
    /// have the request rejected downstream — a documentation request the
    /// reviewer believes they raised and the provider never receives. The two
    /// rules are the same rule, and a mixed list is a malformed decision, not a
    /// decision that asked for nothing.
    /// </summary>
    public static bool IndicatesAdditionalInformationRequest(
        Authorization authorization, IReadOnlyList<RequestedInformationItem> requestedItems)
        => authorization.Status == AuthorizationStatus.Pended
           && string.Equals(
               authorization.ReviewDecision?.Trim(),
               AdditionalInformationRequiredDecision,
               StringComparison.OrdinalIgnoreCase)
           && requestedItems.Count > 0
           && requestedItems.All(i => !string.IsNullOrWhiteSpace(i.Description));

    /// <inheritdoc />
    public async Task<bool> EnsureRequestForDecisionAsync(
        Authorization authorization,
        IReadOnlyList<RequestedInformationItem> requestedItems,
        DateTime? dueDate,
        string? decisionControlNumber,
        CancellationToken ct = default)
    {
        if (!IndicatesAdditionalInformationRequest(authorization, requestedItems))
        {
            if (authorization.Status == AuthorizationStatus.Pended)
            {
                if (requestedItems.Count == 0)
                {
                    _logger.LogInformation(
                        "Authorization {AuthNumber} pended with decision {Decision} but named no "
                        + "requested documentation — no additional-information request raised.",
                        Sanitize(authorization.AuthorizationNumber),
                        Sanitize(authorization.ReviewDecision));
                }
                else if (requestedItems.Any(i => string.IsNullOrWhiteSpace(i.Description)))
                {
                    // Distinct from "asked for nothing": the reviewer DID ask,
                    // and the decision is malformed. Reported as a warning so it
                    // is not mistaken for the ordinary no-documentation pend.
                    _logger.LogWarning(
                        "Authorization {AuthNumber} pended with decision {Decision} naming "
                        + "{Count} item(s), at least one without a description — no "
                        + "additional-information request raised.",
                        Sanitize(authorization.AuthorizationNumber),
                        Sanitize(authorization.ReviewDecision),
                        requestedItems.Count);
                }
            }

            return false;
        }

        var command = new RfaiRequestCommand
        {
            TenantId = authorization.TenantId,
            AuthNumber = authorization.AuthorizationNumber,
            AuthorizationId = authorization.Id,
            CorrelationKey = CorrelationKeyFor(authorization, decisionControlNumber, requestedItems),
            MemberId = authorization.MemberId,
            RequestingProviderNpi = authorization.RequestingProviderNPI,
            ReviewDecision = authorization.ReviewDecision,
            ReasonCode = authorization.DenialReasonCode,
            ReasonDescription = authorization.PendReason,
            RequestedBy = authorization.ReviewerName ?? authorization.LastUpdatedBy,
            DueDate = dueDate,
            // Free text SUPPLEMENTS the coded items above; it never replaces them.
            Notes = authorization.FollowUpAction,
            RequestedItems = requestedItems.ToList(),
        };

        RfaiRequestHandle? handle;
        try
        {
            handle = await _gateway.EnsureRequestAsync(command, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Category only — never the command, which carries the member and the
            // reviewer's wording.
            _logger.LogError(
                "Raising the additional-information request for authorization {AuthNumber} "
                + "failed ({Fault}). The decision stands; the request is retried on the next "
                + "delivery of this decision and cannot be duplicated by the retry.",
                Sanitize(authorization.AuthorizationNumber), ex.GetType().Name);
            return false;
        }

        if (handle is null)
        {
            _logger.LogWarning(
                "The additional-information request for authorization {AuthNumber} was not "
                + "recorded. The decision stands and the retry is idempotent.",
                Sanitize(authorization.AuthorizationNumber));
            return false;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var changed = !string.Equals(authorization.RFAIReference, handle.TrackingId, StringComparison.Ordinal)
                      || !authorization.RFAIIssued;

        authorization.RFAIReference = handle.TrackingId;
        authorization.RFAIIssued = true;
        // The FIRST issue date is the one that matters for a response deadline;
        // a replay must not push it forward.
        authorization.RFAIIssuedDate ??= now;

        if (changed)
            authorization.LastUpdatedDate = now;

        _logger.LogInformation(
            "Authorization {AuthNumber}: additional-information request {Request} "
            + "{Verb} (tracking {Tracking})",
            Sanitize(authorization.AuthorizationNumber), Sanitize(handle.Id),
            handle.Created ? "raised" : "already existed", Sanitize(handle.TrackingId));

        return changed;
    }

    /// <summary>
    /// The decision's identity.
    ///
    /// The 278 response control number is preferred: it is the decision's own
    /// identifier, so two deliveries of one decision share it and two genuinely
    /// different decisions do not. Without one, a digest of what the decision
    /// SAYS is used — the same reviewer conclusion asking for the same documents
    /// is treated as the same request, which is the safe direction to err: a
    /// duplicate request would leave the provider guessing which one to answer.
    /// </summary>
    internal static string CorrelationKeyFor(
        Authorization authorization,
        string? decisionControlNumber,
        IReadOnlyList<RequestedInformationItem> requestedItems)
    {
        if (!string.IsNullOrWhiteSpace(decisionControlNumber))
        {
            return Sha256Hex(
                $"{authorization.TenantId}|{authorization.AuthorizationNumber}|"
                + $"{AdditionalInformationRequiredDecision}|ctrl:{decisionControlNumber.Trim()}");
        }

        var content = string.Join("|", requestedItems.Select(i =>
            $"{i.Code}/{i.LoincCode}/{i.Description}/{i.Required}/"
            + $"{i.ServiceLineProcedureCode}/{i.DiagnosisCode}"));

        return Sha256Hex(
            $"{authorization.TenantId}|{authorization.AuthorizationNumber}|"
            + $"{AdditionalInformationRequiredDecision}|{authorization.PendReason}|{content}");
    }

    private static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);
}
