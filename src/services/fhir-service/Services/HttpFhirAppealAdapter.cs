using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Appeals.Contracts;

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
    private readonly ICorrelationIdAccessor _correlation;
    private readonly ILogger<HttpFhirAppealAdapter> _logger;

    public HttpFhirAppealAdapter(
        IHttpClientFactory clientFactory,
        ICorrelationIdAccessor correlation,
        ILogger<HttpFhirAppealAdapter> logger)
    {
        _clientFactory = clientFactory;
        _correlation = correlation;
        _logger = logger;
    }

    // ── Read ────────────────────────────────────────────────────────────

    public async Task<AppealDto?> GetAppealAsync(string id, string tenantId, CancellationToken ct = default)
    {
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
        var client = _clientFactory.CreateClient(HttpClientName);
        var outcomes = new List<AppealSubmitChildOutcome>();

        // 1. POST /api/appeals — creates the top-level appeal. Failure
        //    short-circuits the rest of the bundle (notes and attachments
        //    belong to the appeal that wasn't created).
        var appealOutcome = await PostChildAsync(
            client,
            AppealSubmitChildKind.Appeal,
            bundle.Appeal.Id,
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
        foreach (var note in bundle.Notes)
        {
            var noteOutcome = await PostChildAsync(
                client,
                AppealSubmitChildKind.Note,
                note.NoteId,
                method: HttpMethod.Post,
                requestUri: $"api/appeals/{Uri.EscapeDataString(appealId)}/notes",
                body: note,
                retryUri: $"api/appeals/{Uri.EscapeDataString(appealId)}/notes",
                responseIdSelector: static (AppealDto? a) => a?.Notes.LastOrDefault()?.NoteId,
                ct);
            outcomes.Add(noteOutcome);
        }

        // 3. POST /api/appeals/{id}/attachments (same pattern).
        foreach (var att in bundle.Attachments)
        {
            var attOutcome = await PostChildAsync(
                client,
                AppealSubmitChildKind.Attachment,
                att.AttachmentId,
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
            var request = new HttpRequestMessage(method, requestUri)
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
}
