using CloudHealthOffice.Infrastructure.Security;
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

    /// <summary>
    /// The submitter's own patient reference from the PAS request
    /// (<c>Patient/pat-001</c>). Null for records not created by PAS.
    /// </summary>
    [JsonPropertyName("patientReference")]
    public string? PatientReference { get; init; }

    [JsonPropertyName("requestingProviderNPI")]
    public string? RequestingProviderNpi { get; init; }

    [JsonPropertyName("status")]
    public PriorAuthorizationStatus Status { get; init; }

    /// <summary>X12 278 review decision: A1 approved, A2 modified, A3 denied, A4 pended.</summary>
    [JsonPropertyName("reviewDecision")]
    public string? ReviewDecision { get; init; }

    [JsonPropertyName("denialReasonCode")]
    public string? DenialReasonCode { get; init; }

    [JsonPropertyName("denialReason")]
    public string? DenialReason { get; init; }

    /// <summary>Why the decision is pended. Present for A4.</summary>
    [JsonPropertyName("pendReason")]
    public string? PendReason { get; init; }

    /// <summary>
    /// Tracking id of the CDex additional-information request the pend raised
    /// in rfai-service (PAS-07), or null when it asked for nothing.
    /// </summary>
    [JsonPropertyName("rfaiReference")]
    public string? RfaiReference { get; init; }

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

    /// <summary>
    /// The record's status changes, oldest first: what each one moved to and
    /// which path moved it. Read only to say whether a person made the
    /// decision (<see cref="HumanReviewed"/>); the reasons and actors in
    /// authorization-service's history have no property here.
    /// </summary>
    [JsonPropertyName("statusHistory")]
    public List<PriorAuthorizationStatusChange> StatusHistory { get; init; } = new();

    /// <summary>
    /// Whether the current decision was made by a person: false when the
    /// change that made it came from one of CHO's automated paths
    /// (an automated rule, a plan benefit exclusion, a state program check),
    /// true when it came from anywhere else (a reviewer's decision or a 278
    /// response recorded against the record), and null when the record is not
    /// decided, was imported from another system, or its history does not show
    /// the change that decided it.
    /// </summary>
    public bool? HumanReviewed() => PriorAuthorizationStatusChange.HumanReviewed(Status, StatusHistory);
}

/// <summary>One status change on the record, as much of it as fhir-service reads.</summary>
public sealed record PriorAuthorizationStatusChange
{
    [JsonPropertyName("status")]
    public PriorAuthorizationStatus Status { get; init; }

    /// <summary>
    /// The path that made the change. authorization-service writes
    /// <c>automated[:rule];lob=..;state=..</c> for an automated decision,
    /// <c>benefit-exclusion</c> for a plan exclusion and
    /// <c>state-program:...</c> for a state program check; a reviewer's
    /// decision carries none of those.
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("changedAt")]
    public DateTime ChangedAt { get; init; }

    /// <summary>authorization-service's source for a record imported from another system.</summary>
    public const string ImportSource = "import";

    /// <summary>See <see cref="PriorAuthorizationRecord.HumanReviewed"/>.</summary>
    public static bool? HumanReviewed(
        PriorAuthorizationStatus status, IEnumerable<PriorAuthorizationStatusChange>? history)
    {
        if (status is not (PriorAuthorizationStatus.Approved or PriorAuthorizationStatus.Modified
            or PriorAuthorizationStatus.Denied))
            return null;

        var deciding = history?.LastOrDefault(c => c.Status == status);
        if (deciding is null) return null;
        // A record imported from another system says nothing about who decided it there.
        if (deciding.Source == ImportSource) return null;
        return !IsAutomated(deciding.Source);
    }

    public static bool IsAutomated(string? source)
        => source is not null
           && (source.StartsWith("automated", StringComparison.Ordinal)
               || source.StartsWith("benefit-exclusion", StringComparison.Ordinal)
               || source.StartsWith("state-program:", StringComparison.Ordinal));
}

/// <summary>
/// The authorization lifecycle, mirroring authorization-service's
/// <c>AuthorizationStatus</c> by NAME and by VALUE.
///
/// The names matter as much as the numbers: authorization-service serializes
/// enums as their declared names with integer values disallowed, so the wire
/// carries <c>"Approved"</c>, not <c>4</c>. The numeric values are kept aligned
/// anyway so the two enums cannot drift apart silently.
/// </summary>
public enum PriorAuthorizationStatus
{
    /// <summary>Not a value authorization-service emits; the default for an absent field.</summary>
    Unknown = 0,
    Submitted = 1,
    InReview = 2,
    /// <summary>Waiting for additional information. X12 278 A4.</summary>
    Pended = 3,
    /// <summary>X12 278 A1.</summary>
    Approved = 4,
    /// <summary>Some services approved, some reduced or denied. X12 278 A2.</summary>
    Modified = 5,
    /// <summary>X12 278 A3.</summary>
    Denied = 6,
    Expired = 7,
    Cancelled = 8,
}

/// <summary>One requested service line on the authorization.</summary>
public sealed record PriorAuthorizationService
{
    public int? LineNumber { get; init; }
    public string? ServiceStatus { get; init; }
    public string? DenialReasonCode { get; init; }
    public string? DenialReason { get; init; }
    public string? PendReason { get; init; }

    [JsonPropertyName("procedureCode")]
    public string? ProcedureCode { get; init; }

    [JsonPropertyName("procedureDescription")]
    public string? ProcedureDescription { get; init; }

    [JsonPropertyName("requestedUnits")]
    public decimal? RequestedUnits { get; init; }
    public string? UnitType { get; init; }
    public string? UnitCode { get; init; }
    public string? UnitSystem { get; init; }

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
/// The member's decided-approved authorizations, read live from
/// authorization-service, for CRD to answer pa-needed <c>satisfied</c>. A
/// separate seam from <see cref="IPriorAuthorizationStore"/>, which stays the
/// single PAS inquiry lookup; like it, this reads and never writes or keeps.
/// </summary>
public interface IApprovedAuthorizationSearch
{
    /// <summary>
    /// The member's approved (and approved-as-modified) authorizations. Empty
    /// when there are none or they cannot be read: a requirement CHO cannot
    /// show is satisfied stays a requirement.
    /// </summary>
    Task<IReadOnlyList<PriorAuthorizationRecord>> FindApprovedForMemberAsync(
        string tenantId, string memberId, CancellationToken ct = default);
}

/// <summary>
/// The PAS-recorded authorization a submitter identified with its own
/// <c>Claim.identifier</c>, for an inquiry that names that identifier rather
/// than the <c>preAuthRef</c> CHO issued. A separate seam from
/// <see cref="IPriorAuthorizationStore"/> for the same reason as
/// <see cref="IApprovedAuthorizationSearch"/>; it reads and never writes.
/// The inquiry service applies the same tenant and corroborating-key checks
/// to what it returns.
/// </summary>
public interface ISubmitterIdentifierLookup
{
    /// <summary>The tenant's most recent authorization recorded under <paramref name="submitterIdentifier"/> (<c>system|value</c>), or null.</summary>
    Task<PriorAuthorizationRecord?> GetBySubmitterIdentifierAsync(
        string tenantId, string submitterIdentifier, CancellationToken ct = default);
}

/// <summary>
/// Reads the authoritative record from authorization-service over HTTP, using
/// the read endpoint that already exists (<c>GET api/authorizations/number/{n}</c>).
/// No new store, no new status field, no second copy of the state.
/// </summary>
public sealed class HttpPriorAuthorizationStore
    : IPriorAuthorizationStore, IApprovedAuthorizationSearch, ISubmitterIdentifierLookup
{
    public const string HttpClientName = "AuthorizationService";

    /// <summary>
    /// The wire format authorization-service actually speaks. Web defaults do
    /// NOT read string enum names, so without this converter every status
    /// deserialization throws, the catch below turns it into "not found", and
    /// every inquiry 404s against the real service.
    /// </summary>
    public static readonly JsonSerializerOptions WireFormat = BuildWireFormat();

    private static JsonSerializerOptions BuildWireFormat()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        // Accepts the declared names authorization-service emits, and integers
        // too, so a numeric status from any other producer still reads.
        options.Converters.Add(new JsonStringEnumConverter(
            namingPolicy: null, allowIntegerValues: true));
        return options;
    }

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<HttpPriorAuthorizationStore> _logger;

    public HttpPriorAuthorizationStore(
        IHttpClientFactory factory, ILogger<HttpPriorAuthorizationStore> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PriorAuthorizationRecord>> FindApprovedForMemberAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memberId) || string.IsNullOrWhiteSpace(tenantId))
            return [];

        var found = new List<PriorAuthorizationRecord>();
        foreach (var status in new[] { PriorAuthorizationStatus.Approved, PriorAuthorizationStatus.Modified })
        {
            var page = await SearchAsync(tenantId,
                $"api/authorizations/search?memberId={Uri.EscapeDataString(memberId.Trim())}&status={status}&pageSize=50", ct);
            found.AddRange(page.Where(r => r.Status == status));
        }

        return found;
    }

    public async Task<PriorAuthorizationRecord?> GetBySubmitterIdentifierAsync(
        string tenantId, string submitterIdentifier, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(submitterIdentifier))
            return null;

        var client = _factory.CreateClient(HttpClientName);
        try
        {
            // The internal endpoint takes fhir-service's own service token
            // (the one the submit path records with), for the caller's tenant.
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "api/internal/authorizations/automated-decisions/by-submitter-identifier?identifier="
                + Uri.EscapeDataString(submitterIdentifier.Trim())).UseChoServiceToken();
            request.Headers.Add(TenantHeaderPropagationHandler.HeaderName, tenantId);
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Prior-authorization lookup by submitter identifier returned {Status}; treating as not found.",
                    (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<PriorAuthorizationRecord>(WireFormat, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Category only — never the URL or the body.
            _logger.LogWarning(
                "Prior-authorization lookup by submitter identifier failed ({Fault}); treating as not found.",
                ex.GetType().Name);
            return null;
        }
    }

    private async Task<List<PriorAuthorizationRecord>> SearchAsync(string tenantId, string path, CancellationToken ct)
    {
        var client = _factory.CreateClient(HttpClientName);
        try
        {
            // CRD's caller is an EHR with a CDS Hooks JWT, not a CHO token, so
            // nothing would be forwarded and authorization-service would
            // refuse the read. The search is for the caller's own tenant and
            // the member CRD already resolved, so CHO's service token is used.
            // The tenant is named on the request itself: the token is minted
            // for the tenant the request names, before any later handler adds it.
            using var request = new HttpRequestMessage(HttpMethod.Get, path).UseChoServiceToken();
            request.Headers.Add(TenantHeaderPropagationHandler.HeaderName, tenantId);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Prior-authorization search returned {Status}; treating as none.", (int)response.StatusCode);
                return [];
            }

            return await response.Content.ReadFromJsonAsync<List<PriorAuthorizationRecord>>(WireFormat, ct) ?? [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Category only — never the URL (it carries the member id) or the body.
            _logger.LogWarning("Prior-authorization search failed ({Fault}); treating as none.", ex.GetType().Name);
            return [];
        }
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
            return await response.Content.ReadFromJsonAsync<PriorAuthorizationRecord>(WireFormat, ct);
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
