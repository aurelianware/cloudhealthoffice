using System.Security.Cryptography;
using System.Text;
using RfaiService.Models;

namespace RfaiService.Services;

/// <summary>
/// The additional-information request as a caller asks for it. Everything the
/// case needs to be created, and nothing more.
/// </summary>
public sealed record RfaiCreationRequest
{
    public required string TenantId { get; init; }
    public required string AuthNumber { get; init; }
    public string? AuthorizationId { get; init; }

    /// <summary>
    /// The creating event's own identity — for an A4 decision, a digest of the
    /// decision, not a timestamp. Two workers handling the same decision derive
    /// the same key, so they derive the same document id and one insert wins.
    /// Required for idempotent creation.
    /// </summary>
    public string? CorrelationKey { get; init; }

    public string? MemberId { get; init; }
    public string? RequestingProviderNpi { get; init; }
    public string? ReviewDecision { get; init; }
    public string? ReasonCode { get; init; }
    public string? ReasonDescription { get; init; }
    public DateTime? DueDate { get; init; }
    public string? Notes { get; init; }
    public string? RequestedBy { get; init; }
    public string RequestSource { get; init; } = RfaiRequestSources.Unknown;
    public List<RequestedItem> RequestedItems { get; init; } = new();
}

/// <summary>Why a creation request was refused. Null message means "valid".</summary>
public sealed record RfaiValidation(string? Error)
{
    public bool IsValid => Error is null;
    public static readonly RfaiValidation Valid = new((string?)null);
}

/// <summary>
/// The descriptor of ONE artifact offered in response to a request. The bytes
/// are already stored by the time this reaches the case: what arrives here is
/// the pointer, the hash and the metadata.
/// </summary>
public sealed record RfaiResponseArtifact
{
    /// <summary>Stable identity of this submission. Required — it is what makes a replay a replay.</summary>
    public required string SubmissionId { get; init; }

    public string? AttachmentControlNumber { get; init; }
    public string? StorageProvider { get; init; }
    public string? StorageKey { get; init; }
    public string? FileHash { get; init; }
    public string? ContentType { get; init; }
    public long? SizeBytes { get; init; }
    public string? Title { get; init; }
    public string? DocumentTypeCode { get; init; }
    public string? DocumentTypeSystem { get; init; }
    public string? SubmittedBy { get; init; }
    public string? Channel { get; init; }
    public SourceTransaction? SourceTransaction { get; init; }
    public DateTime? ReceivedAt { get; init; }
}

/// <summary>What happened when a response was offered to a case.</summary>
public enum RfaiIntakeOutcome
{
    /// <summary>New artifacts were recorded and the case moved to DocsReceived.</summary>
    Accepted = 0,

    /// <summary>Every artifact offered was already on the case. Nothing changed.</summary>
    DuplicateIgnored = 1,

    /// <summary>The case is Closed or Cancelled: it can no longer take a response.</summary>
    CaseNotOpenForResponse = 2,

    /// <summary>Accepting would push the case past its artifact cap.</summary>
    TooManyArtifacts = 3,
}

/// <summary>The result of offering a response, including what to do next.</summary>
public sealed record RfaiIntakeResult
{
    public required RfaiIntakeOutcome Outcome { get; init; }
    public required RfaiCase Case { get; init; }

    /// <summary>Artifacts actually appended by THIS call (empty on a replay).</summary>
    public IReadOnlyList<ReceivedAttachment> Recorded { get; init; } = Array.Empty<ReceivedAttachment>();

    /// <summary>True when the case document must be written back.</summary>
    public bool RequiresPersist => Outcome == RfaiIntakeOutcome.Accepted;

    /// <summary>
    /// True when this call is the one that moved the case into DocsReceived —
    /// the single edge that should announce "the authorization can resume
    /// review". A replay never re-announces it.
    /// </summary>
    public bool TransitionedToDocsReceived { get; init; }

    public bool IsRefusal => Outcome is RfaiIntakeOutcome.CaseNotOpenForResponse
                                     or RfaiIntakeOutcome.TooManyArtifacts;
}

/// <summary>
/// The RFAI case rules, as pure functions over the aggregate.
///
/// Kept out of the controller and out of the repositories deliberately: the
/// identity of a case, the conditions under which a response is accepted, and
/// the one transition that resumes prior-authorization review are business
/// rules, and they are the same rules whether the call arrives over the internal
/// API, from the CDex surface in fhir-service, or from a 275 correlated by
/// attachment-service.
/// </summary>
public static class RfaiCaseLifecycle
{
    /// <summary>
    /// Upper bound on artifacts per case. A request for documentation is not an
    /// upload bucket; the cap is enforced by the aggregate itself so no intake
    /// path can bypass it.
    /// </summary>
    public const int MaxArtifactsPerCase = 25;

    /// <summary>Upper bound on distinct items one request may ask for.</summary>
    public const int MaxRequestedItems = 25;

    // ── Creation ─────────────────────────────────────────────────────────────

    public static RfaiValidation Validate(RfaiCreationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId))
            return new RfaiValidation("tenantId is required.");

        if (string.IsNullOrWhiteSpace(request.AuthNumber))
            return new RfaiValidation("authNumber is required.");

        if (!IsSafeIdentifier(request.AuthNumber))
            return new RfaiValidation("authNumber must be alphanumeric (hyphens allowed).");

        // A documentation request with nothing requested is not a request. This
        // is also what stops a generic pended state from being turned into an
        // RFAI: the caller has to say what is actually needed.
        if (request.RequestedItems.Count == 0)
            return new RfaiValidation("At least one requestedItem is required.");

        if (request.RequestedItems.Count > MaxRequestedItems)
            return new RfaiValidation($"A request may name at most {MaxRequestedItems} items.");

        if (request.RequestedItems.Any(i => string.IsNullOrWhiteSpace(i.Description)))
            return new RfaiValidation("Each requestedItem must have a non-empty description.");

        return RfaiValidation.Valid;
    }

    /// <summary>
    /// The document id for a request. Derived from tenant + authorization +
    /// correlation key so that the SAME creating event always addresses the SAME
    /// document, whichever worker processes it and however many times.
    ///
    /// Without a correlation key there is nothing to be idempotent against, so
    /// the caller gets a fresh id and is told (by <see cref="IsDeterministicId"/>)
    /// that replay protection does not apply.
    /// </summary>
    public static string DeterministicId(string tenantId, string authNumber, string? correlationKey)
    {
        // Marked as ad hoc rather than left silently indistinguishable: reading
        // the id back tells you replay protection does not apply to this
        // request, which is exactly the fact a bare length check would hide.
        if (string.IsNullOrWhiteSpace(correlationKey))
            return $"{AdHocIdPrefix}{Guid.NewGuid():N}";

        var digest = Sha256Hex($"{tenantId}|{authNumber}|{correlationKey}");
        return $"{IdPrefix}{digest[..32]}";
    }

    /// <summary>Every case id starts with this; the CDex Task projection keys off it.</summary>
    public const string IdPrefix = "rfai-";

    /// <summary>Ids created without a correlation key, and therefore without replay protection.</summary>
    public const string AdHocIdPrefix = "rfai-adhoc-";

    public static bool IsDeterministicId(string id)
        => id.StartsWith(IdPrefix, StringComparison.Ordinal)
           && !id.StartsWith(AdHocIdPrefix, StringComparison.Ordinal)
           && id.Length == IdPrefix.Length + 32;

    /// <summary>
    /// The provider-facing handle. RANDOM by design: it is one of the keys an
    /// intake must match, so deriving it from the authorization number would
    /// hand every caller who knows that number the ability to compute it.
    /// </summary>
    public static string NewTrackingId(DateTime nowUtc)
        => $"RFAI-{nowUtc:yyyyMMdd}-{RandomNumberGenerator.GetHexString(12, lowercase: false)}";

    /// <summary>Builds the case document for a validated request.</summary>
    public static RfaiCase Create(RfaiCreationRequest request, int sequence, DateTime nowUtc)
        => new()
        {
            Id = DeterministicId(request.TenantId, request.AuthNumber, request.CorrelationKey),
            TenantId = request.TenantId,
            AuthNumber = request.AuthNumber,
            AuthorizationId = request.AuthorizationId,
            TrackingId = NewTrackingId(nowUtc),
            CorrelationKey = request.CorrelationKey,
            Sequence = sequence < 1 ? 1 : sequence,
            Status = RfaiStatus.Open,
            RequestedItems = request.RequestedItems,
            DueDate = request.DueDate,
            Notes = request.Notes,
            MemberId = request.MemberId,
            RequestingProviderNpi = request.RequestingProviderNpi,
            ReviewDecision = request.ReviewDecision,
            ReasonCode = request.ReasonCode,
            ReasonDescription = request.ReasonDescription,
            RequestedBy = request.RequestedBy,
            RequestSource = string.IsNullOrWhiteSpace(request.RequestSource)
                ? RfaiRequestSources.Unknown
                : request.RequestSource,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        };

    /// <summary>
    /// The next cycle number for an authorization, given its existing cases.
    /// A later cycle is a NEW record: earlier request/response evidence is never
    /// overwritten.
    /// </summary>
    public static int NextSequence(IEnumerable<RfaiCase> existing)
        => existing.Select(c => c.Sequence).DefaultIfEmpty(0).Max() + 1;

    // ── Delivery (provenance) ────────────────────────────────────────────────

    /// <summary>
    /// Records that the request was handed to the provider/system. Returns true
    /// when the case changed and needs writing back.
    /// </summary>
    public static bool MarkDelivered(RfaiCase rfaiCase, DateTime nowUtc)
    {
        rfaiCase.FirstDeliveredAt ??= nowUtc;
        rfaiCase.LastDeliveredAt = nowUtc;
        rfaiCase.DeliveryCount++;
        rfaiCase.UpdatedAt = nowUtc;
        return true;
    }

    // ── Response intake ──────────────────────────────────────────────────────

    /// <summary>
    /// Offers artifacts to a case and reports what happened, MUTATING the case
    /// only when the outcome is <see cref="RfaiIntakeOutcome.Accepted"/>.
    ///
    /// Idempotency is by <see cref="RfaiResponseArtifact.SubmissionId"/>: an
    /// artifact whose submission id is already on the case is skipped. A retry
    /// of an identical submission therefore records nothing, transitions
    /// nothing, and announces nothing — while a genuinely NEW artifact under the
    /// same request is appended as an additional response rather than
    /// overwriting the first.
    /// </summary>
    public static RfaiIntakeResult OfferResponse(
        RfaiCase rfaiCase, IReadOnlyList<RfaiResponseArtifact> artifacts, DateTime nowUtc)
    {
        if (rfaiCase.Status is RfaiStatus.Closed or RfaiStatus.Cancelled)
        {
            return new RfaiIntakeResult
            {
                Outcome = RfaiIntakeOutcome.CaseNotOpenForResponse,
                Case = rfaiCase,
            };
        }

        var known = rfaiCase.ReceivedAttachments
            .Select(a => a.SubmissionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        var fresh = new List<RfaiResponseArtifact>();
        foreach (var artifact in artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.SubmissionId)) continue;
            if (!known.Add(artifact.SubmissionId)) continue;
            fresh.Add(artifact);
        }

        if (fresh.Count == 0)
        {
            return new RfaiIntakeResult
            {
                Outcome = RfaiIntakeOutcome.DuplicateIgnored,
                Case = rfaiCase,
            };
        }

        if (rfaiCase.ReceivedAttachments.Count + fresh.Count > MaxArtifactsPerCase)
        {
            return new RfaiIntakeResult
            {
                Outcome = RfaiIntakeOutcome.TooManyArtifacts,
                Case = rfaiCase,
            };
        }

        var recorded = fresh.Select(a => new ReceivedAttachment
        {
            SubmissionId = a.SubmissionId,
            ReceivedAt = a.ReceivedAt ?? nowUtc,
            AttachmentControlNumber = a.AttachmentControlNumber ?? rfaiCase.TrackingId,
            StorageProvider = a.StorageProvider,
            StorageKey = a.StorageKey,
            FileHash = a.FileHash,
            ContentType = a.ContentType,
            SizeBytes = a.SizeBytes,
            Title = a.Title,
            DocumentTypeCode = a.DocumentTypeCode,
            DocumentTypeSystem = a.DocumentTypeSystem,
            SubmittedBy = a.SubmittedBy,
            Channel = a.Channel,
            SourceTransaction = a.SourceTransaction,
        }).ToList();

        rfaiCase.ReceivedAttachments.AddRange(recorded);

        var transitioned = rfaiCase.Status == RfaiStatus.Open;
        if (transitioned)
            rfaiCase.Status = RfaiStatus.DocsReceived;

        rfaiCase.RespondedAt ??= nowUtc;
        rfaiCase.UpdatedAt = nowUtc;

        return new RfaiIntakeResult
        {
            Outcome = RfaiIntakeOutcome.Accepted,
            Case = rfaiCase,
            Recorded = recorded,
            TransitionedToDocsReceived = transitioned,
        };
    }

    /// <summary>
    /// Whether every REQUIRED item has been answered, as far as a count can tell.
    /// Deliberately a count and not a judgement: deciding that a document
    /// actually satisfies a clinical question is the reviewer's job, which is
    /// exactly why receiving documents returns an authorization to review rather
    /// than approving it.
    /// </summary>
    public static bool AllRequiredItemsAnswered(RfaiCase rfaiCase)
    {
        var required = rfaiCase.RequestedItems.Count(i => i.Required);
        return rfaiCase.ReceivedAttachments.Count >= Math.Max(required, 1);
    }

    // ── Closure ──────────────────────────────────────────────────────────────

    public static bool Close(RfaiCase rfaiCase, string? closedBy, string? reason, DateTime nowUtc)
    {
        if (rfaiCase.Status is RfaiStatus.Closed or RfaiStatus.Cancelled)
            return false;

        rfaiCase.Status = RfaiStatus.Closed;
        rfaiCase.ClosedBy = closedBy;
        rfaiCase.ClosureReason = reason;
        rfaiCase.ClosedAt = nowUtc;
        rfaiCase.UpdatedAt = nowUtc;
        return true;
    }

    public static bool Cancel(RfaiCase rfaiCase, string? cancelledBy, string? reason, DateTime nowUtc)
    {
        if (rfaiCase.Status is RfaiStatus.Closed or RfaiStatus.Cancelled)
            return false;

        rfaiCase.Status = RfaiStatus.Cancelled;
        rfaiCase.ClosedBy = cancelledBy;
        rfaiCase.ClosureReason = reason;
        rfaiCase.ClosedAt = nowUtc;
        rfaiCase.UpdatedAt = nowUtc;
        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    public static bool IsSafeIdentifier(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= 64
           && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
