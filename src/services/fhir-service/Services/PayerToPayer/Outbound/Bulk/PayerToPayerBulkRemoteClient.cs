using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirService.Services.PayerToPayer.Outbound.Bulk;

/// <summary>Where an asynchronous PDex bulk request stands, as the peer answered.</summary>
public enum BulkRemoteState
{
    /// <summary>Kicked off (202): poll <see cref="BulkRemoteResponse.StatusUrl"/>.</summary>
    Accepted,

    /// <summary>Still running (202 to a poll, or a 429/503 to retry): ask again later.</summary>
    InProgress,

    /// <summary>Done: <see cref="BulkRemoteResponse.Payload"/> holds the answer.</summary>
    Completed,

    /// <summary>Refused or broken: <see cref="BulkRemoteResponse.Outcome"/> says how.</summary>
    Failed,
}

/// <summary>A peer's answer to one bulk call.</summary>
public sealed record BulkRemoteResponse(
    BulkRemoteState State,
    RemoteCallOutcome Outcome,
    Uri? StatusUrl = null,
    TimeSpan? RetryAfter = null,
    string? Payload = null)
{
    public static BulkRemoteResponse Accepted(Uri statusUrl) => new(BulkRemoteState.Accepted, RemoteCallOutcome.Success, statusUrl);

    public static BulkRemoteResponse InProgress(TimeSpan? retryAfter) =>
        new(BulkRemoteState.InProgress, RemoteCallOutcome.Success, RetryAfter: retryAfter);

    public static BulkRemoteResponse Completed(string payload) =>
        new(BulkRemoteState.Completed, RemoteCallOutcome.Success, Payload: payload);

    public static BulkRemoteResponse Failed(RemoteCallOutcome outcome) => new(BulkRemoteState.Failed, outcome);
}

/// <summary>
/// Transport for the PDex 2.1 bulk Payer-to-Payer flow, CHO as the requesting
/// (new) payer: <c>Group/$bulk-member-match</c>, then
/// <c>Group/{id}/$davinci-data-export</c> on the matched Group, polling each
/// status URL and downloading the export's NDJSON files. Every call is made to
/// the payer's directory endpoint with its SMART backend token
/// (<see cref="IPayerToPayerCredentialProvider"/>).
/// </summary>
public interface IPayerToPayerBulkRemoteClient
{
    /// <summary>POST <c>Group/$bulk-member-match</c> with <c>Prefer: respond-async</c>.</summary>
    Task<BulkRemoteResponse> SubmitMatchAsync(PayerToPayerEndpoint endpoint, JsonObject parameters, CancellationToken ct = default);

    /// <summary>Kick off <c>Group/{groupId}/$davinci-data-export</c> for the payer-to-payer export type.</summary>
    Task<BulkRemoteResponse> KickOffExportAsync(PayerToPayerEndpoint endpoint, string groupId, CancellationToken ct = default);

    /// <summary>GET a status URL the payer handed back.</summary>
    Task<BulkRemoteResponse> PollAsync(PayerToPayerEndpoint endpoint, Uri statusUrl, CancellationToken ct = default);

    /// <summary>GET one export file from the manifest.</summary>
    Task<BulkRemoteResponse> DownloadAsync(
        PayerToPayerEndpoint endpoint, Uri fileUrl, bool requiresAccessToken, CancellationToken ct = default);

    /// <summary>DELETE a finished export's status URL, so the payer can drop its files. Best effort.</summary>
    Task CompleteAsync(PayerToPayerEndpoint endpoint, Uri statusUrl, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IPayerToPayerBulkRemoteClient"/> over HTTP, on the same named
/// client as the single-member exchange (no redirects, TLS never relaxed).
///
/// A peer hands back URLs here (status URLs, file URLs), which the single
/// exchange never had to follow. They are where SSRF would come in, so:
/// <list type="bullet">
///   <item>a status URL must be on the payer's own origin (scheme, host and
///   port of its directory base URL);</item>
///   <item>a file URL must be on that origin or on one of the entry's
///   <see cref="PayerToPayerEndpointEntry.BulkFileHosts"/> over https, and
///   CHO's token goes only to the payer's own origin;</item>
///   <item>a Group id is used in a path only when it is a FHIR id.</item>
/// </list>
/// Anything else is an invalid response, and nothing is called.
/// </summary>
public sealed partial class HttpPayerToPayerBulkRemoteClient : IPayerToPayerBulkRemoteClient
{
    public const string ExportType = "hl7.fhir.us.davinci-pdex#payertopayer";

    private readonly IHttpClientFactory _clientFactory;
    private readonly IPayerToPayerCredentialProvider _credentials;
    private readonly IOptions<PayerToPayerBulkOutboundOptions> _options;
    private readonly ILogger<HttpPayerToPayerBulkRemoteClient> _logger;

    public HttpPayerToPayerBulkRemoteClient(
        IHttpClientFactory clientFactory,
        IPayerToPayerCredentialProvider credentials,
        IOptions<PayerToPayerBulkOutboundOptions> options,
        ILogger<HttpPayerToPayerBulkRemoteClient> logger)
    {
        _clientFactory = clientFactory;
        _credentials = credentials;
        _options = options;
        _logger = logger;
    }

    public async Task<BulkRemoteResponse> SubmitMatchAsync(
        PayerToPayerEndpoint endpoint, JsonObject parameters, CancellationToken ct = default)
    {
        if (endpoint.FhirBaseUri is not { } baseUri)
            return BulkRemoteResponse.Failed(RemoteCallOutcome.InvalidResponse);

        var json = parameters.ToJsonString();
        return await KickOffAsync(endpoint, "bulk-member-match", () =>
        {
            var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "Group/$bulk-member-match"))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/fhir+json"),
            };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
            message.Headers.Add("Prefer", "respond-async");
            return message;
        }, ct);
    }

    public async Task<BulkRemoteResponse> KickOffExportAsync(
        PayerToPayerEndpoint endpoint, string groupId, CancellationToken ct = default)
    {
        if (endpoint.FhirBaseUri is not { } baseUri || !FhirId().IsMatch(groupId ?? string.Empty))
            return BulkRemoteResponse.Failed(RemoteCallOutcome.InvalidResponse);

        var uri = new Uri(baseUri,
            $"Group/{groupId}/$davinci-data-export?exportType={Uri.EscapeDataString(ExportType)}");
        return await KickOffAsync(endpoint, "davinci-data-export", () =>
        {
            var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
            message.Headers.Add("Prefer", "respond-async");
            return message;
        }, ct);
    }

    public async Task<BulkRemoteResponse> PollAsync(
        PayerToPayerEndpoint endpoint, Uri statusUrl, CancellationToken ct = default)
    {
        if (!IsOwnOrigin(endpoint, statusUrl))
            return Refused(endpoint, "status");

        using var response = await SendAsync(endpoint, "status", withToken: true, () =>
        {
            var message = new HttpRequestMessage(HttpMethod.Get, statusUrl);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
            return message;
        }, ct);
        if (response is null) return BulkRemoteResponse.InProgress(null);

        if (response.StatusCode == HttpStatusCode.Accepted
            || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            return BulkRemoteResponse.InProgress(RetryAfter(response));

        return await CompletedOrFailedAsync(endpoint, "status", response, _options.Value.MaxResponseBytes, ct);
    }

    public async Task<BulkRemoteResponse> DownloadAsync(
        PayerToPayerEndpoint endpoint, Uri fileUrl, bool requiresAccessToken, CancellationToken ct = default)
    {
        var ownOrigin = IsOwnOrigin(endpoint, fileUrl);
        var fileHost = !ownOrigin
                       && fileUrl.IsAbsoluteUri
                       && fileUrl.Scheme == Uri.UriSchemeHttps
                       && string.IsNullOrEmpty(fileUrl.UserInfo)
                       && endpoint.BulkFileHosts.Contains(fileUrl.IdnHost.ToLowerInvariant(), StringComparer.Ordinal);
        if (!ownOrigin && !fileHost)
            return Refused(endpoint, "file");

        using var response = await SendAsync(endpoint, "file", withToken: ownOrigin && requiresAccessToken, () =>
        {
            var message = new HttpRequestMessage(HttpMethod.Get, fileUrl);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+ndjson"));
            return message;
        }, ct);
        if (response is null) return BulkRemoteResponse.Failed(RemoteCallOutcome.Unavailable);

        return await CompletedOrFailedAsync(endpoint, "file", response, _options.Value.MaxFileBytes, ct);
    }

    public async Task CompleteAsync(PayerToPayerEndpoint endpoint, Uri statusUrl, CancellationToken ct = default)
    {
        if (!IsOwnOrigin(endpoint, statusUrl)) return;
        using var _ = await SendAsync(endpoint, "status-delete", withToken: true,
            () => new HttpRequestMessage(HttpMethod.Delete, statusUrl), ct);
    }

    /// <summary>A kick-off: 202 with a status URL on the payer's origin, or 200 with the answer at once.</summary>
    private async Task<BulkRemoteResponse> KickOffAsync(
        PayerToPayerEndpoint endpoint, string operation, Func<HttpRequestMessage> build, CancellationToken ct)
    {
        using var response = await SendAsync(endpoint, operation, withToken: true, build, ct);
        if (response is null) return BulkRemoteResponse.Failed(RemoteCallOutcome.Unavailable);

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            var location = response.Content.Headers.ContentLocation ?? response.Headers.Location;
            if (location is null) return Refused(endpoint, operation);
            var statusUrl = location.IsAbsoluteUri ? location : new Uri(endpoint.FhirBaseUri!, location);
            return IsOwnOrigin(endpoint, statusUrl) ? BulkRemoteResponse.Accepted(statusUrl) : Refused(endpoint, operation);
        }

        return await CompletedOrFailedAsync(endpoint, operation, response, _options.Value.MaxResponseBytes, ct);
    }

    private async Task<BulkRemoteResponse> CompletedOrFailedAsync(
        PayerToPayerEndpoint endpoint, string operation, HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var outcome = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => RemoteCallOutcome.Unauthorized,
                HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => RemoteCallOutcome.Unavailable,
                >= HttpStatusCode.InternalServerError => RemoteCallOutcome.Unavailable,
                _ => RemoteCallOutcome.InvalidResponse,
            };
            _logger.LogWarning(
                "Payer-to-Payer bulk {Operation} to peer {EndpointKey} returned {Status} → {Outcome}.",
                operation, LogSanitizer.Sanitize(endpoint.EndpointKey), (int)response.StatusCode, outcome);
            return BulkRemoteResponse.Failed(outcome);
        }

        var payload = await ReadCappedAsync(response, maxBytes, ct);
        if (payload is null)
        {
            _logger.LogWarning(
                "Payer-to-Payer bulk {Operation} from peer {EndpointKey} was empty or over the size cap.",
                operation, LogSanitizer.Sanitize(endpoint.EndpointKey));
            return BulkRemoteResponse.Failed(RemoteCallOutcome.InvalidResponse);
        }
        return BulkRemoteResponse.Completed(payload);
    }

    /// <summary>
    /// One call, with CHO's token when <paramref name="withToken"/>; a 401 to
    /// a token drops it and tries once more with a new one. Null when the peer
    /// could not be reached.
    /// </summary>
    private async Task<HttpResponseMessage?> SendAsync(
        PayerToPayerEndpoint endpoint, string operation, bool withToken, Func<HttpRequestMessage> build, CancellationToken ct)
    {
        var client = _clientFactory.CreateClient(HttpPayerToPayerRemoteClient.HttpClientName);
        var token = withToken ? await _credentials.GetAccessTokenAsync(endpoint, ct) : null;
        var response = await SendOnceAsync(client, build, token, endpoint, operation, ct);

        if (response?.StatusCode == HttpStatusCode.Unauthorized && !string.IsNullOrWhiteSpace(token))
        {
            response.Dispose();
            _credentials.Invalidate(endpoint);
            token = await _credentials.GetAccessTokenAsync(endpoint, ct);
            if (string.IsNullOrWhiteSpace(token))
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            response = await SendOnceAsync(client, build, token, endpoint, operation, ct);
        }
        return response;
    }

    private async Task<HttpResponseMessage?> SendOnceAsync(
        HttpClient client, Func<HttpRequestMessage> build, string? token,
        PayerToPayerEndpoint endpoint, string operation, CancellationToken ct)
    {
        using var message = build();
        if (!string.IsNullOrWhiteSpace(token))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            return await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(
                "Payer-to-Payer bulk {Operation} to peer {EndpointKey} was unreachable ({Fault}).",
                operation, LogSanitizer.Sanitize(endpoint.EndpointKey), ex.GetType().Name);
            return null;
        }
    }

    private BulkRemoteResponse Refused(PayerToPayerEndpoint endpoint, string operation)
    {
        // The URL itself is the peer's text and may carry anything: it is not logged.
        _logger.LogWarning(
            "Payer-to-Payer bulk {Operation} from peer {EndpointKey} named a URL outside its directory origin; not followed.",
            operation, LogSanitizer.Sanitize(endpoint.EndpointKey));
        return BulkRemoteResponse.Failed(RemoteCallOutcome.InvalidResponse);
    }

    /// <summary>Same scheme, host and port as the payer's directory base URL, and no user info.</summary>
    internal static bool IsOwnOrigin(PayerToPayerEndpoint endpoint, Uri url)
        => endpoint.FhirBaseUri is { } baseUri
           && url.IsAbsoluteUri
           && string.IsNullOrEmpty(url.UserInfo)
           && Uri.Compare(url, baseUri, UriComponents.SchemeAndServer, UriFormat.Unescaped,
               StringComparison.OrdinalIgnoreCase) == 0;

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    private static async Task<string?> ReadCappedAsync(HttpResponseMessage response, long max, CancellationToken ct)
    {
        max = Math.Max(1, max);
        if (response.Content.Headers.ContentLength is { } declared && declared > max) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    [GeneratedRegex("^[A-Za-z0-9\\-\\.]{1,64}$")]
    private static partial Regex FhirId();
}
