using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirService.Services;

/// <summary>
/// The prior-authorization state an inquiry projects, as fhir-service sees it.
///
/// A deliberately NARROW view of authorization-service's <c>Authorization</c>
/// aggregate: the identifiers, status and decision an inquiry answers with, and
/// nothing else. fhir-service does not reference that project, and the fields it
/// does not need — patient name, date of birth, clinical attachments, notes —
/// have no property here to land in, so they cannot leak into a response or a
/// log by accident.
///
/// This is a READ projection of the one authoritative record. There is no
/// inquiry-specific store and no second status field: everything below is the
/// same state the submit path wrote and the rest of Cloud Health Office updates.
/// </summary>
public sealed record PriorAuthorizationRecord
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("authorizationNumber")]
    public string AuthorizationNumber { get; init; } = string.Empty;

    /// <summary>Member the authorization is for, as the submit path recorded it.</summary>
    [JsonPropertyName("memberId")]
    public string MemberId { get; init; } = string.Empty;

    [JsonPropertyName("requestingProviderNPI")]
    public string? RequestingProviderNpi { get; init; }

    /// <summary>1 Submitted, 2 InReview, 3 Pended, 4 Approved, 5 Modified, 6 Denied, 7 Expired, 8 Cancelled.</summary>
    [JsonPropertyName("status")]
    public int Status { get; init; }

    /// <summary>X12 278 review decision: A1 approved, A2 modified, A3 denied, A4 pended.</summary>
    [JsonPropertyName("reviewDecision")]
    public string? ReviewDecision { get; init; }

    [JsonPropertyName("denialReasonCode")]
    public string? DenialReasonCode { get; init; }

    [JsonPropertyName("denialReason")]
    public string? DenialReason { get; init; }

    /// <summary>Why the decision is pended. Present for A4; NOT a CDex exchange.</summary>
    [JsonPropertyName("pendReason")]
    public string? PendReason { get; init; }

    [JsonPropertyName("followUpAction")]
    public string? FollowUpAction { get; init; }

    [JsonPropertyName("approvedServiceDateFrom")]
    public DateTime? ApprovedServiceDateFrom { get; init; }

    [JsonPropertyName("approvedServiceDateTo")]
    public DateTime? ApprovedServiceDateTo { get; init; }

    [JsonPropertyName("expirationDate")]
    public DateTime? ExpirationDate { get; init; }

    [JsonPropertyName("submittedDate")]
    public DateTime SubmittedDate { get; init; }

    [JsonPropertyName("reviewedDate")]
    public DateTime? ReviewedDate { get; init; }

    [JsonPropertyName("lastUpdatedDate")]
    public DateTime LastUpdatedDate { get; init; }

    [JsonPropertyName("requestedServices")]
    public List<PriorAuthorizationService> RequestedServices { get; init; } = new();
}

/// <summary>One requested service line on the authorization.</summary>
public sealed record PriorAuthorizationService
{
    [JsonPropertyName("procedureCode")]
    public string? ProcedureCode { get; init; }

    [JsonPropertyName("procedureDescription")]
    public string? ProcedureDescription { get; init; }

    [JsonPropertyName("requestedUnits")]
    public decimal? RequestedUnits { get; init; }

    [JsonPropertyName("approvedUnits")]
    public decimal? ApprovedUnits { get; init; }
}

/// <summary>
/// Read-only access to the authoritative prior-authorization record.
///
/// READ ONLY by contract: there is no write method on this interface, so an
/// inquiry cannot create a record, change a status, restart a decision clock, or
/// re-submit anything to a payer however it is called.
/// </summary>
public interface IPriorAuthorizationStore
{
    /// <summary>
    /// The current committed record for this authorization number, or null when
    /// there is none. Reads live state on every call — never a submission-time
    /// snapshot — so a status changed since submission is the status returned.
    /// </summary>
    Task<PriorAuthorizationRecord?> GetByAuthorizationNumberAsync(
        string authorizationNumber, CancellationToken ct = default);
}

/// <summary>
/// Reads the authoritative record from authorization-service over HTTP, using
/// the read endpoint that already exists (<c>GET api/authorizations/number/{n}</c>).
/// No new store, no new status field, no second copy of the state.
/// </summary>
public sealed class HttpPriorAuthorizationStore : IPriorAuthorizationStore
{
    public const string HttpClientName = "AuthorizationService";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<HttpPriorAuthorizationStore> _logger;

    public HttpPriorAuthorizationStore(
        IHttpClientFactory factory, ILogger<HttpPriorAuthorizationStore> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<PriorAuthorizationRecord?> GetByAuthorizationNumberAsync(
        string authorizationNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authorizationNumber))
            return null;

        var client = _factory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(
                $"api/authorizations/number/{Uri.EscapeDataString(authorizationNumber)}", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Category only — never the URL or the response body.
            _logger.LogWarning(
                "Prior-authorization lookup failed ({Fault}); treating as not found.",
                ex.GetType().Name);
            return null;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Prior-authorization lookup returned {Status}; treating as not found.",
                (int)response.StatusCode);
            return null;
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<PriorAuthorizationRecord>(Json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Prior-authorization response could not be read ({Fault}); treating as not found.",
                ex.GetType().Name);
            return null;
        }
    }
}
