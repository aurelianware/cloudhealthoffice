using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirService.Services.PayerToPayer.Outbound;

/// <summary>
/// Production <see cref="IPayerToPayerRemoteClient"/> over HTTP, using the named
/// <see cref="HttpClient"/> registered as <see cref="HttpClientName"/>. It POSTs
/// the SAME request shapes Cloud Health Office serves inbound (P2P-04
/// <c>Patient/$member-match</c>, P2P-01 <c>PayerToPayer/$member-data-export</c>)
/// — one Payer-to-Payer wire format in both directions.
///
/// Security posture:
///   * it calls only the URIs on the resolved <see cref="PayerToPayerEndpoint"/>,
///     which came from the trusted directory — never from a request body;
///   * redirects are not followed (the named client is registered with
///     <c>AllowAutoRedirect = false</c>), so a peer cannot bounce CHO onto
///     another host;
///   * TLS validation is never relaxed;
///   * response bodies are capped (<see cref="PayerToPayerTransportOptions.MaxResponseBytes"/>)
///     so a peer cannot exhaust memory;
///   * nothing sensitive is logged: no request/response bodies, no member
///     demographics, no Authorization header, no credential — log lines identify
///     the peer by its directory key, not its URL.
/// </summary>
public sealed class HttpPayerToPayerRemoteClient : IPayerToPayerRemoteClient
{
    public const string HttpClientName = "PayerToPayerRemote";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _clientFactory;
    private readonly IPayerToPayerCredentialProvider _credentials;
    private readonly IOptions<PayerToPayerTransportOptions> _transport;
    private readonly ILogger<HttpPayerToPayerRemoteClient> _logger;

    public HttpPayerToPayerRemoteClient(
        IHttpClientFactory clientFactory,
        IPayerToPayerCredentialProvider credentials,
        IOptions<PayerToPayerTransportOptions> transport,
        ILogger<HttpPayerToPayerRemoteClient> logger)
    {
        _clientFactory = clientFactory;
        _credentials = credentials;
        _transport = transport;
        _logger = logger;
    }

    public Task<RemoteCallResponse> MatchMemberAsync(
        PayerToPayerEndpoint endpoint, RemoteMemberMatchRequest request, CancellationToken ct = default)
        => PostAsync(endpoint, endpoint.MemberMatchUri, "member-match", new
        {
            receivingPayerId = request.ReceivingPayerId,
            memberId = request.MemberId,
            familyName = request.FamilyName,
            birthDate = request.BirthDate,
            requestedPayerId = request.RequestedPayerId,
            asOfDate = request.AsOfDate,
        }, ct);

    public Task<RemoteCallResponse> RequestMemberDataAsync(
        PayerToPayerEndpoint endpoint, RemoteMemberDataRequest request, CancellationToken ct = default)
        => PostAsync(endpoint, endpoint.MemberDataExportUri, "member-data-export", new
        {
            receivingPayerId = request.ReceivingPayerId,
            memberId = request.MemberId,
            lookbackYears = request.LookbackYears,
        }, ct);

    private async Task<RemoteCallResponse> PostAsync(
        PayerToPayerEndpoint endpoint, Uri uri, string operation, object body, CancellationToken ct)
    {
        var client = _clientFactory.CreateClient(HttpClientName);

        using var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/fhir+json"),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));

        var token = await _credentials.GetAccessTokenAsync(endpoint, ct);
        if (!string.IsNullOrWhiteSpace(token))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller gave up; not a peer failure
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Transport fault or client-side timeout. The exception message can
            // carry peer detail, so it is not logged as text — the category is.
            _logger.LogWarning(
                "Payer-to-Payer {Operation} to peer {EndpointKey} was unreachable ({Fault}).",
                operation, Clean(endpoint.EndpointKey), ex.GetType().Name);
            return RemoteCallResponse.Failure(RemoteCallOutcome.Unavailable);
        }

        using (response)
        {
            var outcome = MapStatus(response.StatusCode);
            if (outcome != RemoteCallOutcome.Success)
            {
                _logger.LogWarning(
                    "Payer-to-Payer {Operation} to peer {EndpointKey} returned {Status} → {Outcome}.",
                    operation, Clean(endpoint.EndpointKey), (int)response.StatusCode, outcome);
                return RemoteCallResponse.Failure(outcome);
            }

            var payload = await ReadCappedAsync(response, ct);
            if (payload is null)
            {
                _logger.LogWarning(
                    "Payer-to-Payer {Operation} from peer {EndpointKey} returned an empty or oversized payload.",
                    operation, Clean(endpoint.EndpointKey));
                return RemoteCallResponse.Failure(RemoteCallOutcome.InvalidResponse);
            }

            return RemoteCallResponse.Success(payload);
        }
    }

    /// <summary>
    /// Reads at most <see cref="PayerToPayerTransportOptions.MaxResponseBytes"/>
    /// of the response, returning null when the body is empty or exceeds the cap.
    /// </summary>
    private async Task<string?> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var max = Math.Max(1, _transport.Value.MaxResponseBytes);
        if (response.Content.Headers.ContentLength is { } declared && declared > max) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0) return null;
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Maps a peer's HTTP status onto the transport-neutral outcome.
    ///
    /// A peer that follows the CHO inbound convention collapses no-match,
    /// ambiguous identity, and cross-tenant into a single 422 so its endpoint
    /// cannot be used to enumerate members. That status therefore maps to
    /// <see cref="RemoteCallOutcome.NoMatch"/>: CHO cannot tell the two apart
    /// from the wire, and both stop the exchange before any data is requested.
    /// A peer that does distinguish them can say so with 409.
    /// </summary>
    private static RemoteCallOutcome MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Accepted => RemoteCallOutcome.Success,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => RemoteCallOutcome.Unauthorized,
        HttpStatusCode.Conflict => RemoteCallOutcome.Ambiguous,
        HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity => RemoteCallOutcome.NoMatch,
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => RemoteCallOutcome.Unavailable,
        >= HttpStatusCode.InternalServerError => RemoteCallOutcome.Unavailable,
        // Anything else (including a redirect, which this client does not follow)
        // is a response CHO will not act on.
        _ => RemoteCallOutcome.InvalidResponse,
    };

    /// <summary>Strips CR/LF so config-derived values cannot forge log entries (CWE-117).</summary>
    private static string Clean(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);
}

/// <summary>Transport limits for outbound Payer-to-Payer calls.</summary>
public sealed class PayerToPayerTransportOptions
{
    public const string SectionName = "Cms0057:PayerToPayerOutbound:Transport";

    /// <summary>Per-call timeout applied to the named HttpClient.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Largest response body CHO will read from a peer (default 8 MiB).</summary>
    public int MaxResponseBytes { get; set; } = 8 * 1024 * 1024;
}
