using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Appeals.Contracts;
using FhirService.Middleware;
using Microsoft.AspNetCore.Http;

namespace FhirService.Services;

/// <summary>
/// Calls appeals-service's HTTP REST surface to back the FHIR appeal
/// projections. Uses the "ChoAppealsService" named HttpClient, which
/// ships with both <see cref="TenantHeaderPropagationHandler"/> and
/// <see cref="CorrelationIdPropagationHandler"/> attached as
/// DelegatingHandlers so tenant + correlation headers flow end-to-end
/// without per-call plumbing.
///
/// First implementation of the IFhirDataAdapter pattern for a real
/// backing service. Sets the shape future CHO adapters (e.g. a
/// real member-service adapter replacing MockFhirDataAdapter) will
/// follow.
/// </summary>
public sealed class HttpFhirAppealAdapter : IFhirAppealAdapter
{
    public const string HttpClientName = "ChoAppealsService";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IHttpClientFactory _clientFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<HttpFhirAppealAdapter> _logger;

    public HttpFhirAppealAdapter(
        IHttpClientFactory clientFactory,
        IHttpContextAccessor httpContextAccessor,
        ILogger<HttpFhirAppealAdapter> logger)
    {
        _clientFactory = clientFactory;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private void AssertTenantConsistency(string tenantId)
    {
        var handlerTenant = _httpContextAccessor.HttpContext?.GetTenantId();
        if (!string.IsNullOrEmpty(handlerTenant) &&
            !string.Equals(handlerTenant, tenantId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Tenant parameter ({ParamTenant}) differs from HttpContext tenant ({CtxTenant}); using parameter.",
                Sanitize(tenantId), Sanitize(handlerTenant));
        }
    }

    // ── Read ────────────────────────────────────────────────────────────

    public async Task<AppealDto?> GetAppealAsync(string id, string tenantId, CancellationToken ct = default)
    {
        AssertTenantConsistency(tenantId);
        var client = _clientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync($"api/appeals/{Uri.EscapeDataString(id)}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "appeals-service GET failed for id={AppealId} tenant={TenantId}",
                Sanitize(id), Sanitize(tenantId));
            throw;
        }

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<AppealDto>(JsonOptions, ct);
    }

    public async Task<(IReadOnlyList<AppealDto> Items, int Total)> SearchAppealsAsync(
        AppealSearchQuery query, string tenantId, CancellationToken ct = default)
    {
        AssertTenantConsistency(tenantId);
        var client = _clientFactory.CreateClient(HttpClientName);
        var qs = BuildSearchQueryString(query);

        var response = await client.GetAsync($"api/appeals/search{qs}", ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AppealsListResponse>(JsonOptions, ct);
        var items = body?.Items ?? new List<AppealDto>();
        return (items, items.Count);
    }

    // ── Submit ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AppealSubmitChildOutcome>> SubmitAppealAsync(
        AppealSubmitBundleDto bundle, string tenantId, CancellationToken ct = default)
    {
        AssertTenantConsistency(tenantId);
        var client = _clientFactory.CreateClient(HttpClientName);
        var outcomes = new List<AppealSubmitChildOutcome>();

        // 1. POST /api/appeals — creates the top-level appeal. Failure
        //    short-circuits the rest of the bundle (notes and attachments
        //    belong to the appeal that wasn't created).
        var appealOutcome = await PostChildAsync(
            client,
            AppealSubmitChildKind.Appeal,
            bundle.Appeal.Id,
            entryIndex: bundle.AppealEntryIndex,
            method: HttpMethod.Post,
            requestUri: "api/appeals",
            body: bundle.Appeal,
            retryUri: "api/appeals",
            responseIdSelector: static (AppealDto? a) => a?.Id,
            ct);
        outcomes.Add(appealOutcome);

        if (!appealOutcome.Success)
        {
            _logger.LogWarning(
                "Appeal create failed — skipping {NoteCount} notes and {AttachmentCount} attachments",
                bundle.Notes.Count, bundle.Attachments.Count);
            return outcomes;
        }

        var appealId = appealOutcome.AssignedId!;

        // 2. POST /api/appeals/{id}/notes (serial; each failure is
        //    independent — keep going with the rest).
        for (var i = 0; i < bundle.Notes.Count; i++)
        {
            var note = bundle.Notes[i];
            var noteEntryIndex = i < bundle.NoteEntryIndices.Count ? bundle.NoteEntryIndices[i] : i + 1;
            var noteOutcome = await PostChildAsync(
                client,
                AppealSubmitChildKind.Note,
                note.NoteId,
                entryIndex: noteEntryIndex,
                method: HttpMethod.Post,
                requestUri: $"api/appeals/{Uri.EscapeDataString(appealId)}/notes",
                body: note,
                retryUri: $"api/appeals/{Uri.EscapeDataString(appealId)}/notes",
                responseIdSelector: static (AppealDto? a) => a?.Notes.LastOrDefault()?.NoteId,
                ct);
            outcomes.Add(noteOutcome);
        }

        // 3. POST /api/appeals/{id}/attachments (same pattern).
        for (var i = 0; i < bundle.Attachments.Count; i++)
        {
            var att = bundle.Attachments[i];
            var attEntryIndex = i < bundle.AttachmentEntryIndices.Count ? bundle.AttachmentEntryIndices[i] : bundle.Notes.Count + i + 1;
            var attOutcome = await PostChildAsync(
                client,
                AppealSubmitChildKind.Attachment,
                att.AttachmentId,
                entryIndex: attEntryIndex,
                method: HttpMethod.Post,
                requestUri: $"api/appeals/{Uri.EscapeDataString(appealId)}/attachments",
                body: att,
                retryUri: $"api/appeals/{Uri.EscapeDataString(appealId)}/attachments",
                responseIdSelector: static (AppealDto? a) => a?.Attachments.LastOrDefault()?.AttachmentId,
                ct);
            outcomes.Add(attOutcome);
        }

        return outcomes;
    }

    // ── Child helpers ───────────────────────────────────────────────────

    private async Task<AppealSubmitChildOutcome> PostChildAsync<TBody>(
        HttpClient client,
        AppealSubmitChildKind kind,
        string childRef,
        int entryIndex,
        HttpMethod method,
        string requestUri,
        TBody body,
        string retryUri,
        Func<AppealDto?, string?> responseIdSelector,
        CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(method, requestUri)
            {
                Content = JsonContent.Create(body, options: JsonOptions)
            };
            response = await client.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                var updated = await response.Content.ReadFromJsonAsync<AppealDto>(JsonOptions, ct);
                var assignedId = responseIdSelector(updated) ?? childRef;
                return new AppealSubmitChildOutcome
                {
                    Kind = kind,
                    ChildRef = childRef,
                    EntryIndex = entryIndex,
                    Success = true,
                    AssignedId = assignedId,
                    HttpStatus = (int)response.StatusCode,
                    FailureKind = AppealSubmitFailureKind.None
                };
            }

            // Non-2xx — classify.
            var kindClassification = ClassifyHttpFailure(response.StatusCode);
            var diagnostics = await BuildRedactedDiagnosticsAsync(response, ct);

            _logger.LogWarning(
                "Appeal submit child {Kind} {ChildRef} failed with {Status}: {Diag}",
                kind, Sanitize(childRef), (int)response.StatusCode, Sanitize(diagnostics));

            return new AppealSubmitChildOutcome
            {
                Kind = kind,
                ChildRef = childRef,
                EntryIndex = entryIndex,
                Success = false,
                HttpStatus = (int)response.StatusCode,
                Diagnostics = diagnostics,
                FailureKind = kindClassification,
                RetryUrl = retryUri
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            _logger.LogWarning(ex,
                "Appeal submit child {Kind} {ChildRef} network/timeout failure",
                kind, Sanitize(childRef));

            return new AppealSubmitChildOutcome
            {
                Kind = kind,
                ChildRef = childRef,
                EntryIndex = entryIndex,
                Success = false,
                HttpStatus = null,
                Diagnostics = $"Transport failure: {ex.GetType().Name}",
                FailureKind = AppealSubmitFailureKind.Transient,
                RetryUrl = retryUri
            };
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>
    /// Downstream 4xx → <see cref="AppealSubmitFailureKind.Processing"/>
    /// (caller can adjust input and retry).
    /// Network, timeout, 5xx → <see cref="AppealSubmitFailureKind.Transient"/>
    /// (retry as-is may succeed).
    /// </summary>
    internal static AppealSubmitFailureKind ClassifyHttpFailure(HttpStatusCode status) =>
        (int)status switch
        {
            >= 400 and < 500 => AppealSubmitFailureKind.Processing,
            >= 500 => AppealSubmitFailureKind.Transient,
            _ => AppealSubmitFailureKind.None
        };

    /// <summary>
    /// Build a redacted diagnostic string from a failure response. Keeps
    /// structural info (HTTP status, ProblemDetails.title / .type,
    /// extension code keys) and omits free-text fields that could leak
    /// PHI (error messages, note text echoes, name echoes).
    /// </summary>
    internal static async Task<string> BuildRedactedDiagnosticsAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var header = $"HTTP {status} {response.StatusCode}";

        string? bodySample;
        try
        {
            bodySample = await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return header;
        }

        if (string.IsNullOrWhiteSpace(bodySample))
        {
            return header;
        }

        // Try to parse as JSON and extract only the known structural fields.
        try
        {
            using var doc = JsonDocument.Parse(bodySample);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var parts = new List<string> { header };
                if (root.TryGetProperty("status", out var s)) parts.Add($"status={s}");
                if (root.TryGetProperty("title", out var t)) parts.Add($"title={t}");
                if (root.TryGetProperty("type", out var ty)) parts.Add($"type={ty}");
                if (root.TryGetProperty("fromStatus", out var fs)) parts.Add($"fromStatus={fs}");
                if (root.TryGetProperty("toStatus", out var ts)) parts.Add($"toStatus={ts}");
                if (root.TryGetProperty("closureReasonCode", out var cr)) parts.Add($"closureReasonCode={cr}");
                // Deliberately DROP: detail, message, errors — these may
                // carry free-text inputs that echo PHI back.
                return string.Join("; ", parts);
            }
        }
        catch (JsonException)
        {
            // Not JSON — return the header only; never the raw body.
        }

        return header;
    }

    // ── Query-string assembly ───────────────────────────────────────────

    private static string BuildSearchQueryString(AppealSearchQuery query)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(query.MemberId))
            parts.Add($"memberId={Uri.EscapeDataString(query.MemberId)}");
        if (!string.IsNullOrEmpty(query.Status))
            parts.Add($"status={Uri.EscapeDataString(query.Status)}");
        if (!string.IsNullOrEmpty(query.ClaimId))
            parts.Add($"claimId={Uri.EscapeDataString(query.ClaimId)}");
        if (!string.IsNullOrEmpty(query.AssignedReviewerId))
            parts.Add($"assignedReviewerId={Uri.EscapeDataString(query.AssignedReviewerId)}");
        if (query.ClosureReasonCode.HasValue)
            parts.Add($"closureReasonCode={Uri.EscapeDataString(query.ClosureReasonCode.Value.ToString())}");
        parts.Add($"page={query.Page}");
        parts.Add($"pageSize={query.PageSize}");
        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    public async Task<(AppealDto Appeal, AppealNoteDto Note)?> GetNoteByIdAsync(
        string noteId, string tenantId, CancellationToken ct = default)
    {
        AssertTenantConsistency(tenantId);
        var client = _clientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync($"api/appeals/notes/{Uri.EscapeDataString(noteId)}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "appeals-service GET notes/{NoteId} failed for tenant={TenantId}",
                Sanitize(noteId), Sanitize(tenantId));
            throw;
        }

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var lookup = await response.Content.ReadFromJsonAsync<AppealNoteLookupResponse>(JsonOptions, ct);
        if (lookup is null) return null;

        var appeal = new AppealDto { Id = lookup.AppealId, MemberId = lookup.MemberId };
        var note = new AppealNoteDto
        {
            NoteId = lookup.NoteId,
            CreatedBy = lookup.CreatedBy,
            NoteText = lookup.NoteText,
            IsInternal = lookup.IsInternal,
            CreatedAt = lookup.CreatedAt
        };
        return (appeal, note);
    }

    public async Task<(AppealDto Appeal, AppealAttachmentDto Attachment)?> GetAttachmentByIdAsync(
        string attachmentId, string tenantId, CancellationToken ct = default)
    {
        AssertTenantConsistency(tenantId);
        var client = _clientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync($"api/appeals/attachments/{Uri.EscapeDataString(attachmentId)}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "appeals-service GET attachments/{AttachmentId} failed for tenant={TenantId}",
                Sanitize(attachmentId), Sanitize(tenantId));
            throw;
        }

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var lookup = await response.Content.ReadFromJsonAsync<AppealAttachmentLookupResponse>(JsonOptions, ct);
        if (lookup is null) return null;

        var appeal = new AppealDto { Id = lookup.AppealId, MemberId = lookup.MemberId };
        var attachment = new AppealAttachmentDto
        {
            AttachmentId = lookup.AttachmentId,
            ControlNumber = lookup.ControlNumber,
            AttachmentTypeCode = lookup.AttachmentTypeCode,
            AttachmentTypeDescription = lookup.AttachmentTypeDescription,
            TransmissionCode = lookup.TransmissionCode,
            FileName = lookup.FileName,
            BlobUrl = lookup.BlobUrl,
            ContentType = lookup.ContentType,
            FileSizeBytes = lookup.FileSizeBytes,
            UploadedAt = lookup.UploadedAt,
            Description = lookup.Description,
            Status = lookup.Status,
            SentDate = lookup.SentDate,
            AcknowledgmentReceived = lookup.AcknowledgmentReceived
        };
        return (appeal, attachment);
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty
            : value.Replace("\r", "").Replace("\n", "");

    /// <summary>
    /// Response envelope for appeals-service's list endpoints. Mirrors
    /// <c>AppealsService.Controllers.AppealListResponse</c>.
    /// </summary>
    private sealed class AppealsListResponse
    {
        public List<AppealDto> Items { get; set; } = new();
    }

    private sealed class AppealNoteLookupResponse
    {
        public string AppealId { get; set; } = string.Empty;
        public string MemberId { get; set; } = string.Empty;
        public string NoteId { get; set; } = string.Empty;
        public string CreatedBy { get; set; } = string.Empty;
        public string NoteText { get; set; } = string.Empty;
        public bool IsInternal { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class AppealAttachmentLookupResponse
    {
        public string AppealId { get; set; } = string.Empty;
        public string MemberId { get; set; } = string.Empty;
        public string AttachmentId { get; set; } = string.Empty;
        public string? ControlNumber { get; set; }
        public string AttachmentTypeCode { get; set; } = string.Empty;
        public string? AttachmentTypeDescription { get; set; }
        public string TransmissionCode { get; set; } = "EL";
        public string? FileName { get; set; }
        public string? BlobUrl { get; set; }
        public string? ContentType { get; set; }
        public long? FileSizeBytes { get; set; }
        public DateTime UploadedAt { get; set; }
        public string? Description { get; set; }
        public AttachmentStatus Status { get; set; }
        public DateTime? SentDate { get; set; }
        public bool AcknowledgmentReceived { get; set; }
    }
}
