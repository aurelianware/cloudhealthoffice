using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Infrastructure.Logging;

namespace FhirService.Services.Pas;

/// <summary>
/// Stores a PAS request and the automated decision on it as the authorization
/// record, before <c>$submit</c> answers (D-01). The record is the system of
/// record: a <c>preAuthRef</c> is only ever issued for one that was stored.
/// </summary>
public interface IPasAuthorizationRecorder
{
    Task<PasRecordOutcome> RecordAsync(
        PasAuthorizationRecordRequest request, string tenantId, CancellationToken ct = default);
}

public enum PasRecordFailure
{
    None = 0,

    /// <summary>authorization-service could not be reached, timed out, answered 5xx, or no service token could be obtained.</summary>
    Unavailable,

    /// <summary>authorization-service answered 4xx or with something other than the record asked for.</summary>
    Rejected,
}

/// <summary>What was stored: the stored status can differ from the decision (a plan exclusion denies).</summary>
public sealed record PasRecordOutcome
{
    public bool Recorded => Failure == PasRecordFailure.None;

    public PasRecordFailure Failure { get; init; }

    public PasStoredAuthorization? Stored { get; init; }

    public static PasRecordOutcome Failed(PasRecordFailure failure) => new() { Failure = failure };
}

/// <summary>The fields of the stored record that decide the PAS response.</summary>
public sealed record PasStoredAuthorization
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; init; } = string.Empty;

    [JsonPropertyName("authorizationNumber")]
    public string AuthorizationNumber { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public PriorAuthorizationStatus Status { get; init; }

    [JsonPropertyName("reviewDecision")]
    public string? ReviewDecision { get; init; }

    [JsonPropertyName("denialReasonCode")]
    public string? DenialReasonCode { get; init; }

    [JsonPropertyName("denialReason")]
    public string? DenialReason { get; init; }

    [JsonPropertyName("approvedServiceDateFrom")]
    public DateTime? ApprovedServiceDateFrom { get; init; }

    [JsonPropertyName("approvedServiceDateTo")]
    public DateTime? ApprovedServiceDateTo { get; init; }

    /// <summary>The hash of the request this record was created from (see <see cref="PasAuthorizationRecordRequest.RequestHash"/>).</summary>
    [JsonPropertyName("requestHash")]
    public string? RequestHash { get; init; }
}

/// <summary>
/// Posts to authorization-service's internal automated-decision endpoint with
/// fhir-service's OWN service token for the authenticated tenant, never the
/// caller's token: that endpoint accepts fhir-service and nothing else, so a
/// user or SMART client cannot record a decision by calling it directly.
///
/// Idempotent across a lost answer: every request carries a hash of its
/// payload. When the write's answer is lost (timeout, transport error, 5xx)
/// the recorder reads the record back once by number and answers from it if
/// it is this request; authorization-service likewise answers a repeat of the
/// same request with the stored record instead of a conflict.
/// </summary>
public sealed class HttpPasAuthorizationRecorder : IPasAuthorizationRecorder
{
    public const string HttpClientName = "AuthorizationService";
    public const string Path = "api/internal/authorizations/automated-decisions";

    /// <summary>PAS answers within 15 s overall; the write gets a bounded share of that.</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The one compensating read after a lost answer gets a smaller share.</summary>
    private static readonly TimeSpan ReadBackTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions Wire = HttpPriorAuthorizationStore.WireFormat;

    private readonly IHttpClientFactory _factory;
    private readonly IServiceProvider _services;
    private readonly ILogger<HttpPasAuthorizationRecorder> _logger;

    public HttpPasAuthorizationRecorder(
        IHttpClientFactory factory, IServiceProvider services, ILogger<HttpPasAuthorizationRecorder> logger)
    {
        _factory = factory;
        _services = services;
        _logger = logger;
    }

    public async Task<PasRecordOutcome> RecordAsync(
        PasAuthorizationRecordRequest request, string tenantId, CancellationToken ct = default)
    {
        var tokens = ChoServiceTokens.Resolve(_services);
        if (tokens is null)
        {
            _logger.LogError("PAS authorization {AuthNumber} not recorded: no service token is configured.",
                LogSanitizer.Sanitize(request.AuthorizationNumber));
            return PasRecordOutcome.Failed(PasRecordFailure.Unavailable);
        }

        request.RequestHash = HashOf(request);

        var written = await WriteAsync(request, tenantId, tokens, ct);
        if (written.Failure != PasRecordFailure.Unavailable)
            return written;

        // The write's answer was lost (timeout, transport error, 5xx), which
        // does not mean the write failed. One compensating read by number: if
        // the record exists and is THIS request (same hash), answer from it
        // rather than fail a request that was in fact stored, and leave the
        // provider no reason to resubmit and create a second record.
        var readBack = await ReadBackAsync(request, tenantId, tokens, ct);
        if (readBack is not null)
        {
            _logger.LogWarning(
                "PAS authorization {AuthNumber}: the write's answer was lost; the stored record was read back and matches.",
                LogSanitizer.Sanitize(request.AuthorizationNumber));
            return new PasRecordOutcome { Stored = readBack };
        }

        return written;
    }

    private async Task<PasRecordOutcome> WriteAsync(
        PasAuthorizationRecordRequest request, string tenantId, IChoServiceTokenSource tokens, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WriteTimeout);

        HttpResponseMessage response;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, Path)
            {
                Content = JsonContent.Create(request, options: Wire),
            };
            await AuthorizeAsync(message, tenantId, tokens, timeout.Token);
            response = await _factory.CreateClient(HttpClientName).SendAsync(message, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Category only: never the payload, which carries demographics.
            _logger.LogError("PAS authorization {AuthNumber} not recorded: authorization-service unavailable ({Fault}).",
                LogSanitizer.Sanitize(request.AuthorizationNumber), ex.GetType().Name);
            return PasRecordOutcome.Failed(PasRecordFailure.Unavailable);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("PAS authorization {AuthNumber} not recorded: authorization-service answered {Status}.",
                    LogSanitizer.Sanitize(request.AuthorizationNumber), (int)response.StatusCode);
                return PasRecordOutcome.Failed((int)response.StatusCode >= 500
                    ? PasRecordFailure.Unavailable
                    : PasRecordFailure.Rejected);
            }

            PasStoredAuthorization? stored;
            try
            {
                stored = await response.Content.ReadFromJsonAsync<PasStoredAuthorization>(Wire, timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Timed out reading the answer: as good as lost.
                _logger.LogError("PAS authorization {AuthNumber}: the write's answer timed out.",
                    LogSanitizer.Sanitize(request.AuthorizationNumber));
                return PasRecordOutcome.Failed(PasRecordFailure.Unavailable);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                _logger.LogError("PAS authorization {AuthNumber}: the stored record could not be read ({Fault}).",
                    LogSanitizer.Sanitize(request.AuthorizationNumber), ex.GetType().Name);
                return PasRecordOutcome.Failed(PasRecordFailure.Rejected);
            }

            // The record must be the one asked for, in the caller's tenant.
            if (!IsThisRequest(stored, request, tenantId, requireHash: false))
            {
                _logger.LogError("PAS authorization {AuthNumber}: authorization-service returned a different record.",
                    LogSanitizer.Sanitize(request.AuthorizationNumber));
                return PasRecordOutcome.Failed(PasRecordFailure.Rejected);
            }

            return new PasRecordOutcome { Stored = stored };
        }
    }

    /// <summary>The stored record for this request, or null when it cannot be shown to exist.</summary>
    private async Task<PasStoredAuthorization?> ReadBackAsync(
        PasAuthorizationRecordRequest request, string tenantId, IChoServiceTokenSource tokens, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadBackTimeout);

        try
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Get, $"{Path}/{Uri.EscapeDataString(request.AuthorizationNumber)}");
            await AuthorizeAsync(message, tenantId, tokens, timeout.Token);
            using var response = await _factory.CreateClient(HttpClientName).SendAsync(message, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            var stored = await response.Content.ReadFromJsonAsync<PasStoredAuthorization>(Wire, timeout.Token);
            return IsThisRequest(stored, request, tenantId, requireHash: true) ? stored : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("PAS authorization {AuthNumber}: the compensating read failed ({Fault}).",
                LogSanitizer.Sanitize(request.AuthorizationNumber), ex.GetType().Name);
            return null;
        }
    }

    private static async Task AuthorizeAsync(
        HttpRequestMessage message, string tenantId, IChoServiceTokenSource tokens, CancellationToken ct)
    {
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(tenantId, ct));
        message.Headers.Add(TenantHeaderPropagationHandler.HeaderName, tenantId);
    }

    /// <summary>
    /// The record named, in the caller's tenant; on a read-back also created
    /// from this very request (same hash), since another record under the
    /// number says nothing about whether this one was stored.
    /// </summary>
    private static bool IsThisRequest(
        PasStoredAuthorization? stored, PasAuthorizationRecordRequest request, string tenantId, bool requireHash)
        => stored is not null
           && string.Equals(stored.AuthorizationNumber, request.AuthorizationNumber, StringComparison.Ordinal)
           && string.Equals(stored.TenantId, tenantId, StringComparison.Ordinal)
           && (!requireHash || string.Equals(stored.RequestHash, request.RequestHash, StringComparison.Ordinal));

    /// <summary>
    /// SHA-256 (lower-case hex) of the request as it goes on the wire, with no
    /// hash in it. The same request object hashes the same every time.
    /// </summary>
    public static string HashOf(PasAuthorizationRecordRequest request)
    {
        var previous = request.RequestHash;
        request.RequestHash = null;
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(request, Wire);
            return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        }
        finally
        {
            request.RequestHash = previous;
        }
    }
}
