using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirService.Services.Cdex;

/// <summary>
/// An additional-information request as fhir-service sees it — a deliberately
/// NARROW projection of rfai-service's <c>RfaiCase</c>.
///
/// fhir-service does not reference rfai-service, and the fields it does not need
/// (patient name, date of birth, note bodies, clinical content) have no property
/// here to land in, so they cannot leak into a FHIR resource or a log by
/// accident. This mirrors how <see cref="PriorAuthorizationRecord"/> projects
/// authorization-service's aggregate for <c>Claim/$inquire</c>.
///
/// There is exactly ONE additional-information store, and it is rfai-service.
/// This type is a read/write VIEW of it, never a second copy.
/// </summary>
public sealed record CdexAdditionalInformationRequest
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; init; } = string.Empty;

    /// <summary>The case document id — also the id of the projected FHIR Task.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Prior authorization this request belongs to (278 TRN02 / PAS preAuthRef).</summary>
    [JsonPropertyName("authNumber")]
    public string AuthNumber { get; init; } = string.Empty;

    [JsonPropertyName("authorizationId")]
    public string? AuthorizationId { get; init; }

    /// <summary>Provider-facing handle; the CDex <c>TrackingId</c> / X12 275 ACN.</summary>
    [JsonPropertyName("trackingId")]
    public string TrackingId { get; init; } = string.Empty;

    /// <summary>1-based cycle number. Later cycles are new records, never overwrites.</summary>
    [JsonPropertyName("sequence")]
    public int Sequence { get; init; } = 1;

    [JsonPropertyName("status")]
    public CdexAdditionalInformationStatus Status { get; init; }

    [JsonPropertyName("requestedItems")]
    public List<CdexRequestedItem> RequestedItems { get; init; } = new();

    [JsonPropertyName("receivedAttachments")]
    public List<CdexReceivedArtifact> ReceivedAttachments { get; init; } = new();

    [JsonPropertyName("dueDate")]
    public DateTime? DueDate { get; init; }

    /// <summary>Free text SUPPLEMENTING the coded items — it never replaces them.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("memberId")]
    public string? MemberId { get; init; }

    /// <summary>The provider expected to answer. The corroborating key an intake must match.</summary>
    [JsonPropertyName("requestingProviderNpi")]
    public string? RequestingProviderNpi { get; init; }

    /// <summary>X12 278 review decision that caused the request — expected "A4".</summary>
    [JsonPropertyName("reviewDecision")]
    public string? ReviewDecision { get; init; }

    [JsonPropertyName("reasonCode")]
    public string? ReasonCode { get; init; }

    [JsonPropertyName("reasonDescription")]
    public string? ReasonDescription { get; init; }

    [JsonPropertyName("requestedBy")]
    public string? RequestedBy { get; init; }

    [JsonPropertyName("requestSource")]
    public string? RequestSource { get; init; }

    [JsonPropertyName("firstDeliveredAt")]
    public DateTime? FirstDeliveredAt { get; init; }

    [JsonPropertyName("lastDeliveredAt")]
    public DateTime? LastDeliveredAt { get; init; }

    [JsonPropertyName("respondedAt")]
    public DateTime? RespondedAt { get; init; }

    [JsonPropertyName("closedAt")]
    public DateTime? ClosedAt { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }

    /// <summary>True while the payer is still waiting on a FIRST response.</summary>
    [JsonIgnore]
    public bool IsOpen => Status == CdexAdditionalInformationStatus.Open;

    /// <summary>
    /// True while the request can still take a response.
    ///
    /// Deliberately WIDER than <see cref="IsOpen"/>: a request that has already
    /// been answered still accepts more. A provider who sends a supplementary
    /// document must not be turned away, and — the reason this distinction has
    /// to exist at all — a RETRY of an accepted submission has to reach the
    /// duplicate check to be recognised as a replay rather than refused as
    /// "closed". Only Closed and Cancelled end a request.
    /// </summary>
    [JsonIgnore]
    public bool AcceptsResponse => Status is CdexAdditionalInformationStatus.Open
                                          or CdexAdditionalInformationStatus.DocsReceived;
}

/// <summary>
/// The RFAI case lifecycle, mirroring rfai-service's <c>RfaiStatus</c> by NAME
/// and by VALUE. rfai-service serializes enums as their declared names, so the
/// wire carries <c>"Open"</c>, not <c>0</c>; the numbers are kept aligned anyway
/// so the two enums cannot drift apart silently.
/// </summary>
public enum CdexAdditionalInformationStatus
{
    /// <summary>Requested and awaiting the provider's response.</summary>
    Open = 0,

    /// <summary>A valid response was accepted; the authorization returns to review.</summary>
    DocsReceived = 1,

    /// <summary>The payer is done with this cycle. Retained as history.</summary>
    Closed = 2,

    /// <summary>Withdrawn before a response was required.</summary>
    Cancelled = 3,
}

/// <summary>One thing the payer is asking for.</summary>
public sealed record CdexRequestedItem
{
    /// <summary>X12 PWK attachment-type code.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    /// <summary>LOINC attachment/document-type code, as CDex uses for <c>Task.input</c>.</summary>
    [JsonPropertyName("loincCode")]
    public string? LoincCode { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; init; } = true;

    /// <summary>The requested service line the question is about (HCPCS/CPT).</summary>
    [JsonPropertyName("serviceLineProcedureCode")]
    public string? ServiceLineProcedureCode { get; init; }

    /// <summary>Diagnosis context for the question (ICD-10).</summary>
    [JsonPropertyName("diagnosisCode")]
    public string? DiagnosisCode { get; init; }
}

/// <summary>
/// One artifact already received against a request. The POINTER and the
/// metadata only — never the bytes, which stay in the document store.
/// </summary>
public sealed record CdexReceivedArtifact
{
    [JsonPropertyName("submissionId")]
    public string? SubmissionId { get; init; }

    [JsonPropertyName("receivedAt")]
    public DateTime ReceivedAt { get; init; }

    [JsonPropertyName("attachmentControlNumber")]
    public string? AttachmentControlNumber { get; init; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long? SizeBytes { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("documentTypeCode")]
    public string? DocumentTypeCode { get; init; }

    [JsonPropertyName("documentTypeSystem")]
    public string? DocumentTypeSystem { get; init; }

    [JsonPropertyName("fileHash")]
    public string? FileHash { get; init; }

    [JsonPropertyName("channel")]
    public string? Channel { get; init; }
}

/// <summary>An artifact being offered in response, as rfai-service accepts it.</summary>
public sealed record CdexResponseArtifact
{
    [JsonPropertyName("submissionId")]
    public required string SubmissionId { get; init; }

    [JsonPropertyName("attachmentControlNumber")]
    public string? AttachmentControlNumber { get; init; }

    [JsonPropertyName("storageProvider")]
    public string? StorageProvider { get; init; }

    [JsonPropertyName("storageKey")]
    public string? StorageKey { get; init; }

    [JsonPropertyName("fileHash")]
    public string? FileHash { get; init; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long? SizeBytes { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("documentTypeCode")]
    public string? DocumentTypeCode { get; init; }

    [JsonPropertyName("documentTypeSystem")]
    public string? DocumentTypeSystem { get; init; }

    [JsonPropertyName("submittedBy")]
    public string? SubmittedBy { get; init; }

    [JsonPropertyName("channel")]
    public string? Channel { get; init; }
}

/// <summary>What rfai-service reports after being offered a response.</summary>
public sealed record CdexResponseRecordResult
{
    /// <summary>rfai-service's own outcome name: Accepted, DuplicateIgnored, CaseNotOpenForResponse, TooManyArtifacts.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    [JsonPropertyName("recorded")]
    public int Recorded { get; init; }

    /// <summary>True when THIS call is the one that lets the authorization resume review.</summary>
    [JsonPropertyName("resumedReview")]
    public bool ResumedReview { get; init; }

    [JsonIgnore]
    public bool Accepted => string.Equals(Outcome, "Accepted", StringComparison.Ordinal);

    [JsonIgnore]
    public bool Duplicate => string.Equals(Outcome, "DuplicateIgnored", StringComparison.Ordinal);
}

/// <summary>
/// Access to the authoritative additional-information record held by
/// rfai-service.
///
/// Every method names the tenant explicitly: there is no lookup here that can
/// reach a case without one, so the CDex surface cannot read or write across
/// tenants even if header propagation is ever lost.
/// </summary>
public interface ICdexAdditionalInformationStore
{
    Task<CdexAdditionalInformationRequest?> GetByIdAsync(
        string tenantId, string id, CancellationToken ct = default);

    Task<CdexAdditionalInformationRequest?> GetByTrackingIdAsync(
        string tenantId, string trackingId, CancellationToken ct = default);

    /// <summary>Every cycle for one authorization, newest first. Closed cycles included.</summary>
    Task<IReadOnlyList<CdexAdditionalInformationRequest>> GetByAuthorizationNumberAsync(
        string tenantId, string authorizationNumber, CancellationToken ct = default);

    /// <summary>Records that the request was handed to the provider/system.</summary>
    Task MarkDeliveredAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Offers artifacts to a case; null when the case is gone.</summary>
    Task<CdexResponseRecordResult?> RecordResponseAsync(
        string tenantId, string id, IReadOnlyList<CdexResponseArtifact> artifacts,
        CancellationToken ct = default);
}

/// <summary>
/// Reads and writes the authoritative record over rfai-service's internal API.
/// No new store, no second status field, no duplicate copy of the case.
/// </summary>
public sealed class HttpCdexAdditionalInformationStore : ICdexAdditionalInformationStore
{
    public const string HttpClientName = "RfaiService";

    /// <summary>
    /// The wire format rfai-service actually speaks. Web defaults do NOT read
    /// string enum names, so without this converter every status deserialization
    /// throws and every lookup silently becomes "not found".
    /// </summary>
    public static readonly JsonSerializerOptions WireFormat = BuildWireFormat();

    private static JsonSerializerOptions BuildWireFormat()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(
            namingPolicy: null, allowIntegerValues: true));
        return options;
    }

    /// <summary>
    /// Header rfai-service's TenantMiddleware reads. Sent explicitly on every
    /// call so the tenant travelling with the request is the authenticated one
    /// this service resolved, not whatever ambient context a handler might hold.
    /// </summary>
    private const string TenantHeader = "X-Tenant-ID";

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<HttpCdexAdditionalInformationStore> _logger;

    public HttpCdexAdditionalInformationStore(
        IHttpClientFactory factory, ILogger<HttpCdexAdditionalInformationStore> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public Task<CdexAdditionalInformationRequest?> GetByIdAsync(
        string tenantId, string id, CancellationToken ct = default)
        => GetAsync<CdexAdditionalInformationRequest>(
            tenantId, $"api/rfai/{Uri.EscapeDataString(id)}", ct);

    public Task<CdexAdditionalInformationRequest?> GetByTrackingIdAsync(
        string tenantId, string trackingId, CancellationToken ct = default)
        => GetAsync<CdexAdditionalInformationRequest>(
            tenantId, $"api/rfai/by-tracking/{Uri.EscapeDataString(trackingId)}", ct);

    public async Task<IReadOnlyList<CdexAdditionalInformationRequest>> GetByAuthorizationNumberAsync(
        string tenantId, string authorizationNumber, CancellationToken ct = default)
        => await GetAsync<List<CdexAdditionalInformationRequest>>(
               tenantId, $"api/rfai/by-auth/{Uri.EscapeDataString(authorizationNumber)}", ct)
           ?? [];

    public async Task MarkDeliveredAsync(string tenantId, string id, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"api/rfai/{Uri.EscapeDataString(id)}/delivered");
            request.Headers.TryAddWithoutValidation(TenantHeader, tenantId);
            using var response = await Client().SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Recording RFAI delivery returned {Status}; the request was still served.",
                    (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Provenance is important, but losing the "delivered" stamp must not
            // stop a provider from seeing what the payer needs from them.
            _logger.LogWarning(
                "Recording RFAI delivery failed ({Fault}); the request was still served.",
                ex.GetType().Name);
        }
    }

    public async Task<CdexResponseRecordResult?> RecordResponseAsync(
        string tenantId, string id, IReadOnlyList<CdexResponseArtifact> artifacts,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"api/rfai/{Uri.EscapeDataString(id)}/responses")
        {
            Content = JsonContent.Create(new { artifacts }, options: WireFormat),
        };
        request.Headers.TryAddWithoutValidation(TenantHeader, tenantId);

        using var response = await Client().SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            // Status category only — never the response body, which echoes the case.
            _logger.LogWarning(
                "Recording an RFAI response returned {Status}.", (int)response.StatusCode);

            return response.StatusCode == HttpStatusCode.Conflict
                ? new CdexResponseRecordResult { Outcome = "CaseNotOpenForResponse" }
                : throw new HttpRequestException(
                    $"rfai-service rejected the response with {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<CdexResponseRecordResult>(WireFormat, ct);
    }

    private HttpClient Client() => _factory.CreateClient(HttpClientName);

    private async Task<T?> GetAsync<T>(string tenantId, string path, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation(TenantHeader, tenantId);
            response = await Client().SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Category only — never the URL (it carries the identifier) or the body.
            _logger.LogWarning(
                "Additional-information lookup failed ({Fault}); treating as not found.",
                ex.GetType().Name);
            return default;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return default;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Additional-information lookup returned {Status}; treating as not found.",
                    (int)response.StatusCode);
                return default;
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(WireFormat, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Additional-information response could not be read ({Fault}); treating as not found.",
                    ex.GetType().Name);
                return default;
            }
        }
    }
}
