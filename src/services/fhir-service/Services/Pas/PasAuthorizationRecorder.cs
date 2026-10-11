using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;

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
}

/// <summary>
/// Posts to authorization-service's internal automated-decision endpoint with
/// fhir-service's OWN service token for the authenticated tenant, never the
/// caller's token: that endpoint accepts fhir-service and nothing else, so a
/// user or SMART client cannot record a decision by calling it directly.
/// </summary>
public sealed class HttpPasAuthorizationRecorder : IPasAuthorizationRecorder
{
    public const string HttpClientName = "AuthorizationService";
    public const string Path = "api/internal/authorizations/automated-decisions";

    /// <summary>PAS answers within 15 s overall; the write gets a bounded share of that.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        HttpResponseMessage response;
        try
        {
            var tokens = ChoServiceTokens.Resolve(_services);
            if (tokens is null)
            {
                _logger.LogError("PAS authorization {AuthNumber} not recorded: no service token is configured.",
                    Sanitize(request.AuthorizationNumber));
                return PasRecordOutcome.Failed(PasRecordFailure.Unavailable);
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, Path)
            {
                Content = JsonContent.Create(request, options: Wire),
            };
            message.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", await tokens.GetTokenAsync(tenantId, timeout.Token));
            message.Headers.Add(TenantHeaderPropagationHandler.HeaderName, tenantId);

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
                Sanitize(request.AuthorizationNumber), ex.GetType().Name);
            return PasRecordOutcome.Failed(PasRecordFailure.Unavailable);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("PAS authorization {AuthNumber} not recorded: authorization-service answered {Status}.",
                    Sanitize(request.AuthorizationNumber), (int)response.StatusCode);
                return PasRecordOutcome.Failed((int)response.StatusCode >= 500
                    ? PasRecordFailure.Unavailable
                    : PasRecordFailure.Rejected);
            }

            PasStoredAuthorization? stored;
            try
            {
                stored = await response.Content.ReadFromJsonAsync<PasStoredAuthorization>(Wire, timeout.Token);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or OperationCanceledException)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
                _logger.LogError("PAS authorization {AuthNumber}: the stored record could not be read ({Fault}).",
                    Sanitize(request.AuthorizationNumber), ex.GetType().Name);
                return PasRecordOutcome.Failed(PasRecordFailure.Rejected);
            }

            // The record must be the one asked for, in the caller's tenant.
            if (stored is null
                || !string.Equals(stored.AuthorizationNumber, request.AuthorizationNumber, StringComparison.Ordinal)
                || !string.Equals(stored.TenantId, tenantId, StringComparison.Ordinal))
            {
                _logger.LogError("PAS authorization {AuthNumber}: authorization-service returned a different record.",
                    Sanitize(request.AuthorizationNumber));
                return PasRecordOutcome.Failed(PasRecordFailure.Rejected);
            }

            return new PasRecordOutcome { Stored = stored };
        }
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
