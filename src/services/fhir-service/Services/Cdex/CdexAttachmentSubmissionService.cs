using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Hl7.Fhir.Model;

namespace FhirService.Services.Cdex;

/// <summary>
/// Why a <c>$submit-attachment</c> call ended the way it did.
///
/// The set is deliberately fine-grained for AUDIT. What the caller is told is
/// much coarser: see <see cref="CdexSubmissionResult.Disclosure"/>.
/// </summary>
public enum CdexSubmissionOutcome
{
    /// <summary>Artifacts were stored and recorded against the request.</summary>
    Accepted = 0,

    /// <summary>Every artifact offered was already on the request. Nothing changed.</summary>
    DuplicateReplay = 1,

    // ── Request-shape defects: the caller's to fix, and safe to describe ─────
    MissingTrackingId = 10,
    MissingAttachTo = 11,
    NoAttachments = 12,
    TooManyAttachments = 13,
    MissingContentType = 14,
    UnsupportedContentType = 15,
    MissingContent = 16,
    ExternalContentRejected = 17,
    AttachmentTooLarge = 18,
    PayloadTooLarge = 19,
    ContentRejected = 20,

    // ── Refusals about a RECORD: one uniform answer ─────────────────────────
    /// <summary>No request bears that tracking id.</summary>
    NotFound = 30,
    /// <summary>The request belongs to another tenant.</summary>
    TenantMismatch = 31,
    /// <summary>The <c>AttachTo</c> identifier is not the authorization this request is about.</summary>
    AuthorizationMismatch = 32,
    /// <summary>The submitter is not the provider the request was addressed to.</summary>
    ProviderMismatch = 33,

    // ── Fully correlated, but not answerable ────────────────────────────────
    /// <summary>The request is closed or cancelled and can no longer take a response.</summary>
    RequestNotOpen = 40,
    /// <summary>Accepting would push the request past its artifact cap.</summary>
    RequestAtCapacity = 41,

    /// <summary>Storage or the record write failed. Nothing was recorded.</summary>
    StorageFailure = 50,
}

/// <summary>How an outcome may be reported to the caller.</summary>
public enum CdexSubmissionDisclosure
{
    /// <summary>Accepted (or accepted-as-replay).</summary>
    Success = 0,

    /// <summary>A defect in the request itself. Safe to describe — 400.</summary>
    BadRequest = 1,

    /// <summary>A payload CHO will not store. Safe to describe — 422.</summary>
    UnprocessableContent = 2,

    /// <summary>
    /// Anything about a RECORD the caller has not proven is theirs. ONE answer —
    /// 404 — for unknown, wrong tenant, wrong authorization and wrong provider
    /// alike, because telling them apart turns a tracking id into a probe.
    /// </summary>
    Unavailable = 3,

    /// <summary>
    /// Fully correlated but not answerable. Safe to describe — 409 — because the
    /// caller has already proven the request is theirs.
    /// </summary>
    Conflict = 4,

    /// <summary>CHO could not complete the submission. 503; the caller should retry.</summary>
    Unavailable5xx = 5,
}

/// <summary>The result of a submission, plus the audit category behind it.</summary>
public sealed record CdexSubmissionResult
{
    public required CdexSubmissionOutcome Outcome { get; init; }

    /// <summary>The tracking id that was quoted, for the audit line.</summary>
    public string? TrackingId { get; init; }

    /// <summary>The request the submission correlated to, once it did.</summary>
    public string? RequestId { get; init; }

    public string? AuthorizationNumber { get; init; }

    /// <summary>Artifacts stored by THIS call. Zero on a replay.</summary>
    public int Recorded { get; init; }

    /// <summary>
    /// True when this call is the one that returned the authorization to review.
    /// Never true on a replay, and never an approval — see the service summary.
    /// </summary>
    public bool ResumedReview { get; init; }

    /// <summary>Detail for a BadRequest/UnprocessableContent answer. Never record detail.</summary>
    public string? Detail { get; init; }

    public bool Succeeded => Outcome is CdexSubmissionOutcome.Accepted
                                     or CdexSubmissionOutcome.DuplicateReplay;

    public CdexSubmissionDisclosure Disclosure => Outcome switch
    {
        CdexSubmissionOutcome.Accepted or CdexSubmissionOutcome.DuplicateReplay
            => CdexSubmissionDisclosure.Success,

        CdexSubmissionOutcome.MissingTrackingId
            or CdexSubmissionOutcome.MissingAttachTo
            or CdexSubmissionOutcome.NoAttachments
            or CdexSubmissionOutcome.TooManyAttachments
            => CdexSubmissionDisclosure.BadRequest,

        CdexSubmissionOutcome.MissingContentType
            or CdexSubmissionOutcome.UnsupportedContentType
            or CdexSubmissionOutcome.MissingContent
            or CdexSubmissionOutcome.ExternalContentRejected
            or CdexSubmissionOutcome.AttachmentTooLarge
            or CdexSubmissionOutcome.PayloadTooLarge
            or CdexSubmissionOutcome.ContentRejected
            => CdexSubmissionDisclosure.UnprocessableContent,

        CdexSubmissionOutcome.NotFound
            or CdexSubmissionOutcome.TenantMismatch
            or CdexSubmissionOutcome.AuthorizationMismatch
            or CdexSubmissionOutcome.ProviderMismatch
            => CdexSubmissionDisclosure.Unavailable,

        CdexSubmissionOutcome.RequestNotOpen or CdexSubmissionOutcome.RequestAtCapacity
            => CdexSubmissionDisclosure.Conflict,

        _ => CdexSubmissionDisclosure.Unavailable5xx,
    };
}

/// <summary>
/// The Da Vinci CDex <c>$submit-attachment</c> operation: a provider sending the
/// documentation a payer asked for on a pended prior authorization.
///
/// CORRELATION. A submission is bound to its request by three things that must
/// ALL agree: the tenant (from the authenticated context, never the payload),
/// the tracking id the payer issued, and the authorization named in
/// <c>AttachTo</c>. Where the request records the provider it was addressed to,
/// the submitter's NPI must match that too. Knowing an authorization number is
/// not enough to attach anything to it — that is the whole point of the tracking
/// id.
///
/// WHAT IT DOES NOT DO. Receiving documents never approves an authorization.
/// The most this path can do is return the authorization to review: the
/// documents arrive, the request records them, and a reviewer decides. Nothing
/// here reads the content, judges whether it answers the question, or touches
/// the decision.
///
/// CALLER BINDING — the same documented limitation as <c>Claim/$inquire</c>. PAS
/// and CDex are system-to-system surfaces here and this repository has no
/// mapping from a token subject to a provider NPI, so the submitter is bound by
/// the tracking id and the corroborating provider identifier rather than by the
/// caller's own identity. The caller is recorded in the audit trail instead.
/// Provider Access consent is deliberately NOT applied: it governs a provider
/// reading a member's clinical record, not a provider answering a payer's
/// question about the provider's own prior-authorization request.
/// </summary>
public interface ICdexAttachmentSubmissionService
{
    /// <param name="verifiedProviderNpi">
    /// The caller's provider NPI when — and only when — a trusted issuer
    /// asserted it (see <c>SmartAuth:TrustedIssuers[].Claims.ProviderNpiClaim</c>).
    /// Null means no issuer CHO trusts has vouched for the caller's identity,
    /// and the corroborating-key rule applies unchanged.
    /// </param>
    Task<CdexSubmissionResult> SubmitAsync(
        Parameters parameters, string tenantId, string? callerId,
        string? verifiedProviderNpi = null, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class CdexAttachmentSubmissionService : ICdexAttachmentSubmissionService
{
    private readonly ICdexAdditionalInformationStore _store;
    private readonly IClaimAttachmentContentStore _content;
    private readonly IAttachmentContentScanner _scanner;
    private readonly ILogger<CdexAttachmentSubmissionService> _logger;

    /// <param name="content">
    /// The platform's existing secure attachment-content store. Reused rather
    /// than replaced: it already owns checksumming, content-type normalisation,
    /// storage-key derivation from server-side values, and file-name
    /// sanitisation, so prior-auth documentation does not get a second, weaker
    /// storage story of its own.
    /// </param>
    public CdexAttachmentSubmissionService(
        ICdexAdditionalInformationStore store,
        IClaimAttachmentContentStore content,
        IAttachmentContentScanner scanner,
        ILogger<CdexAttachmentSubmissionService> logger)
    {
        _store = store;
        _content = content;
        _scanner = scanner;
        _logger = logger;
    }

    public async Task<CdexSubmissionResult> SubmitAsync(
        Parameters parameters, string tenantId, string? callerId,
        string? verifiedProviderNpi = null, CancellationToken ct = default)
    {
        // ── 1. Read the operation parameters ────────────────────────────────
        var trackingId = CdexSubmitAttachmentParameters.TrackingId(parameters);
        if (string.IsNullOrWhiteSpace(trackingId))
        {
            return new CdexSubmissionResult
            {
                Outcome = CdexSubmissionOutcome.MissingTrackingId,
                Detail = "TrackingId is required: it names the request being answered.",
            };
        }

        var attachTo = CdexSubmitAttachmentParameters.AttachTo(parameters);
        if (string.IsNullOrWhiteSpace(attachTo))
        {
            return new CdexSubmissionResult
            {
                Outcome = CdexSubmissionOutcome.MissingAttachTo,
                TrackingId = trackingId,
                Detail = "AttachTo is required: it names the prior authorization.",
            };
        }

        var offered = CdexSubmitAttachmentParameters.Attachments(parameters);
        if (offered.Count == 0)
        {
            return new CdexSubmissionResult
            {
                Outcome = CdexSubmissionOutcome.NoAttachments,
                TrackingId = trackingId,
                Detail = "At least one Attachment is required.",
            };
        }

        if (offered.Count > CdexAttachmentPolicy.MaxAttachmentsPerSubmission)
        {
            return new CdexSubmissionResult
            {
                Outcome = CdexSubmissionOutcome.TooManyAttachments,
                TrackingId = trackingId,
                Detail =
                    $"At most {CdexAttachmentPolicy.MaxAttachmentsPerSubmission} attachments "
                    + "may be submitted in one call.",
            };
        }

        // ── 2. Correlate, tenant-scoped ─────────────────────────────────────
        // The lookup itself names the tenant, and the record's own tenant is
        // re-checked below: the isolation holds even if the lookup's scoping is
        // ever lost.
        var request = await _store.GetByTrackingIdAsync(tenantId, trackingId, ct);

        if (request is null)
            return Refused(CdexSubmissionOutcome.NotFound, trackingId);

        if (!string.Equals(request.TenantId, tenantId, StringComparison.Ordinal))
            return Refused(CdexSubmissionOutcome.TenantMismatch, trackingId);

        if (!string.Equals(NormalizeIdentifier(attachTo), NormalizeIdentifier(request.AuthNumber),
                StringComparison.OrdinalIgnoreCase))
        {
            // A caller with a valid tracking id naming a DIFFERENT authorization
            // is either confused or probing. Either way this is the check that
            // stops documents being attached to an arbitrary authorization.
            return Refused(CdexSubmissionOutcome.AuthorizationMismatch, trackingId);
        }

        if (!SubmitterMatchesRequestedProvider(parameters, request, verifiedProviderNpi))
            return Refused(CdexSubmissionOutcome.ProviderMismatch, trackingId);

        // ── 3. Fully correlated. From here, refusals may say why. ───────────
        // A request that has already been answered still accepts more: a
        // supplementary document is legitimate, and a retry must reach the
        // duplicate check to be recognised as a replay. Only Closed and
        // Cancelled end it.
        if (!request.AcceptsResponse)
        {
            return new CdexSubmissionResult
            {
                Outcome = CdexSubmissionOutcome.RequestNotOpen,
                TrackingId = trackingId,
                RequestId = request.Id,
                AuthorizationNumber = request.AuthNumber,
            };
        }

        // ── 4. Validate every artifact BEFORE storing any ───────────────────
        // All-or-nothing on the payload: a call whose third attachment is
        // oversized must not leave the first two stored and half-recorded.
        var validated = new List<ValidatedArtifact>(offered.Count);
        long total = 0;

        foreach (var candidate in offered)
        {
            var (artifact, failure) = Validate(candidate, request, trackingId, tenantId);
            if (failure is not null) return failure;

            total += artifact!.Content.Length;
            if (total > CdexAttachmentPolicy.MaxTotalBytes)
            {
                return new CdexSubmissionResult
                {
                    Outcome = CdexSubmissionOutcome.PayloadTooLarge,
                    TrackingId = trackingId,
                    RequestId = request.Id,
                    Detail =
                        $"The submission exceeds the {CdexAttachmentPolicy.MaxTotalBytes} byte "
                        + "total limit.",
                };
            }

            var scan = await _scanner.ScanAsync(artifact.Content, artifact.ContentType, ct);
            if (!scan.Clean)
            {
                _logger.LogWarning(
                    "CDex $submit-attachment content rejected by scanner for request {Request}",
                    Sanitize(request.Id));

                return new CdexSubmissionResult
                {
                    Outcome = CdexSubmissionOutcome.ContentRejected,
                    TrackingId = trackingId,
                    RequestId = request.Id,
                    Detail = "Submitted content was rejected by content screening.",
                };
            }

            // "Not scanned" is recorded as Unknown, never Safe. Nothing
            // downstream may read the absence of a scanner as a clean verdict.
            validated.Add(artifact with
            {
                ScanStatus = scan.Scanned
                    ? ClaimAttachmentScanStatus.Safe
                    : ClaimAttachmentScanStatus.Unknown,
            });
        }

        // ── 5. Store the bytes, then record the pointers ────────────────────
        // Ordering is deliberate. Storage first means a failure between the two
        // leaves an orphan blob and NO record — the submission simply did not
        // happen, and a retry recomputes the same submission id, writes the same
        // key (overwriting the orphan) and records once. The reverse order would
        // leave a recorded response whose content is missing, which is the
        // failure mode that actually loses information.
        var stored = new List<CdexResponseArtifact>(validated.Count);
        try
        {
            foreach (var artifact in validated)
                stored.Add(await StoreAsync(artifact, callerId, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "CDex $submit-attachment could not store content for request {Request}",
                Sanitize(request.Id));

            return new CdexSubmissionResult
            {
                Outcome = CdexSubmissionOutcome.StorageFailure,
                TrackingId = trackingId,
                RequestId = request.Id,
            };
        }

        CdexResponseRecordResult? recorded;
        try
        {
            recorded = await _store.RecordResponseAsync(tenantId, request.Id, stored, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The bytes are stored under a deterministic key; nothing is recorded.
            // A retry is safe and idempotent, so ask for one rather than
            // pretending the submission landed.
            _logger.LogError(ex,
                "CDex $submit-attachment stored content but could not record it against request {Request}",
                Sanitize(request.Id));

            return new CdexSubmissionResult
            {
                Outcome = CdexSubmissionOutcome.StorageFailure,
                TrackingId = trackingId,
                RequestId = request.Id,
            };
        }

        if (recorded is null)
            return Refused(CdexSubmissionOutcome.NotFound, trackingId);

        if (string.Equals(recorded.Outcome, "CaseNotOpenForResponse", StringComparison.Ordinal))
        {
            // The request closed between the correlation check and the write.
            return new CdexSubmissionResult
            {
                Outcome = CdexSubmissionOutcome.RequestNotOpen,
                TrackingId = trackingId,
                RequestId = request.Id,
                AuthorizationNumber = request.AuthNumber,
            };
        }

        if (string.Equals(recorded.Outcome, "TooManyArtifacts", StringComparison.Ordinal))
        {
            return new CdexSubmissionResult
            {
                Outcome = CdexSubmissionOutcome.RequestAtCapacity,
                TrackingId = trackingId,
                RequestId = request.Id,
                AuthorizationNumber = request.AuthNumber,
            };
        }

        return new CdexSubmissionResult
        {
            Outcome = recorded.Duplicate
                ? CdexSubmissionOutcome.DuplicateReplay
                : CdexSubmissionOutcome.Accepted,
            TrackingId = trackingId,
            RequestId = request.Id,
            AuthorizationNumber = request.AuthNumber,
            Recorded = recorded.Recorded,
            ResumedReview = recorded.ResumedReview,
        };
    }

    // ── Validation ──────────────────────────────────────────────────────────

    private static (ValidatedArtifact? Artifact, CdexSubmissionResult? Failure) Validate(
        CdexOfferedAttachment candidate,
        CdexAdditionalInformationRequest request,
        string trackingId,
        string tenantId)
    {
        CdexSubmissionResult Fail(CdexSubmissionOutcome outcome, string detail) => new()
        {
            Outcome = outcome,
            TrackingId = trackingId,
            RequestId = request.Id,
            Detail = detail,
        };

        if (!string.IsNullOrWhiteSpace(candidate.Url) && candidate.Content is null)
        {
            // CHO never dereferences a caller-supplied URL: that would make the
            // payer's server fetch whatever the submitter points it at.
            return (null, Fail(CdexSubmissionOutcome.ExternalContentRejected,
                "Attachment.url is not accepted — submit the content inline in Attachment.data."));
        }

        if (candidate.Content is null || candidate.Content.Length == 0)
        {
            return (null, Fail(CdexSubmissionOutcome.MissingContent,
                "Each attachment must carry content in Attachment.data."));
        }

        if (string.IsNullOrWhiteSpace(candidate.ContentType))
        {
            return (null, Fail(CdexSubmissionOutcome.MissingContentType,
                "Each attachment must declare Attachment.contentType."));
        }

        if (!CdexAttachmentPolicy.IsAllowedContentType(candidate.ContentType))
        {
            return (null, Fail(CdexSubmissionOutcome.UnsupportedContentType,
                $"Content type '{candidate.ContentType}' is not accepted. Supported: "
                + string.Join(", ", CdexAttachmentPolicy.SupportedContentTypes) + "."));
        }

        if (candidate.Content.Length > CdexAttachmentPolicy.MaxAttachmentBytes)
        {
            return (null, Fail(CdexSubmissionOutcome.AttachmentTooLarge,
                $"An attachment may be at most {CdexAttachmentPolicy.MaxAttachmentBytes} bytes."));
        }

        var contentHash = CdexAttachmentPolicy.Sha256Hex(candidate.Content);
        var submissionId = CdexAttachmentPolicy.SubmissionId(
            tenantId, request.Id, trackingId, contentHash);

        return (new ValidatedArtifact
        {
            SubmissionId = submissionId,
            TenantId = tenantId,
            CaseId = request.Id,
            TrackingId = trackingId,
            Content = candidate.Content,
            ContentType = candidate.ContentType!,
            ContentHash = contentHash,
            Title = CdexAttachmentPolicy.SanitizeTitle(candidate.Title),
            DocumentTypeCode = candidate.DocumentTypeCode,
            DocumentTypeSystem = candidate.DocumentTypeSystem,
        }, null);
    }

    /// <summary>
    /// The submitter must be the provider the request was addressed to.
    ///
    /// There are two strengths of this check, and which one applies depends on
    /// what the deployment's identity provider actually asserts:
    ///
    ///   VERIFIED IDENTITY. When a trusted issuer asserted the caller's NPI,
    ///   that NPI — not the payload's — must match the request. This is real
    ///   caller binding: knowing another provider's NPI no longer helps, because
    ///   the value being compared came from the token, and the token is signed
    ///   by an issuer CHO trusts. A verified caller who is not the requested
    ///   provider is refused even if the payload names the right one, which is
    ///   precisely the substitution the corroborating key could not detect.
    ///
    ///   CORROBORATING KEY. With no verified identity — no issuer configured to
    ///   assert NPI — the original rule stands: the payload's NPI must match the
    ///   request's. That is a weaker check and has always been documented as
    ///   one, since NPIs are public. It is kept rather than removed because
    ///   removing it would loosen deployments that have no provider identity
    ///   claim available, and this change must only ever tighten.
    /// </summary>
    private bool SubmitterMatchesRequestedProvider(
        Parameters parameters,
        CdexAdditionalInformationRequest request,
        string? verifiedProviderNpi)
    {
        if (string.IsNullOrWhiteSpace(request.RequestingProviderNpi))
        {
            // A request naming no provider cannot corroborate anyone — but a
            // caller whose identity IS verified is still pinned to it, so a
            // verified NPI is not silently discarded here.
            _logger.LogInformation(
                "CDex $submit-attachment: request {Request} records no provider, so the "
                + "submitter could not be corroborated against one.",
                Sanitize(request.Id));
            return true;
        }

        if (!string.IsNullOrWhiteSpace(verifiedProviderNpi))
        {
            var bound = string.Equals(
                verifiedProviderNpi.Trim(), request.RequestingProviderNpi.Trim(),
                StringComparison.Ordinal);

            if (!bound)
            {
                _logger.LogWarning(
                    "CDex $submit-attachment: caller identity verified by the trusted issuer "
                    + "does not match the provider request {Request} was addressed to.",
                    Sanitize(request.Id));
            }

            return bound;
        }

        var submitted = CdexSubmitAttachmentParameters.ProviderNpi(parameters);
        if (string.IsNullOrWhiteSpace(submitted))
            return false;

        return string.Equals(
            submitted.Trim(), request.RequestingProviderNpi.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes one artifact through the shared attachment content store and
    /// returns the pointer to record. Every value the store derives the key from
    /// is server-side: the tenant, the request, the submission id, the checksum
    /// and the validated content type. The caller's title travels only as a
    /// display name.
    /// </summary>
    private async Task<CdexResponseArtifact> StoreAsync(
        ValidatedArtifact artifact, string? callerId, CancellationToken ct)
    {
        using var stream = new MemoryStream(artifact.Content, writable: false);

        var reference = await _content.StoreAsync(
            new ClaimAttachmentStoreRequest
            {
                TenantId = CdexAttachmentPolicy.Slug(artifact.TenantId),
                TransmissionId = CdexAttachmentPolicy.Slug(artifact.CaseId),
                AttachmentId = artifact.SubmissionId,
                ContentType = artifact.ContentType,
                DisplayName = artifact.Title,
                ScanStatus = artifact.ScanStatus,
            },
            stream,
            ct);

        return artifact.ToResponseArtifact(callerId, reference);
    }

    private static CdexSubmissionResult Refused(CdexSubmissionOutcome outcome, string trackingId)
        => new() { Outcome = outcome, TrackingId = trackingId };

    private static string NormalizeIdentifier(string value)
    {
        var trimmed = value.Trim();
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);

    /// <summary>An attachment that passed policy, with its server-derived identity.</summary>
    private sealed record ValidatedArtifact
    {
        public required string SubmissionId { get; init; }
        public required string TenantId { get; init; }
        public required string CaseId { get; init; }
        public required string TrackingId { get; init; }
        public required byte[] Content { get; init; }
        public required string ContentType { get; init; }
        public required string ContentHash { get; init; }
        public string? Title { get; init; }
        public string? DocumentTypeCode { get; init; }
        public string? DocumentTypeSystem { get; init; }
        public ClaimAttachmentScanStatus ScanStatus { get; init; } = ClaimAttachmentScanStatus.Unknown;

        public CdexResponseArtifact ToResponseArtifact(
            string? callerId, ClaimAttachmentContentReference reference) => new()
        {
            SubmissionId = SubmissionId,
            AttachmentControlNumber = TrackingId,
            StorageProvider = reference.Container,
            StorageKey = reference.StorageKey,
            FileHash = string.IsNullOrWhiteSpace(reference.ChecksumSha256)
                ? ContentHash
                : reference.ChecksumSha256,
            ContentType = ContentType,
            SizeBytes = Content.Length,
            Title = Title,
            DocumentTypeCode = DocumentTypeCode,
            DocumentTypeSystem = DocumentTypeSystem,
            SubmittedBy = callerId,
            Channel = "cdex-submit-attachment",
        };
    }
}
