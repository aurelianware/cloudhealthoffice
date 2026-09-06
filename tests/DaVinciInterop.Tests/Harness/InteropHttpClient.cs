using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// One recorded exchange with an external implementation: the raw response plus
/// whatever the harness could parse out of it.
/// </summary>
public sealed record InteropResponse(
    HttpStatusCode StatusCode,
    string? ContentType,
    string Body,
    Resource? Resource,
    InteropInteraction Interaction)
{
    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;

    /// <summary>The parsed resource as <typeparamref name="T"/>, or null if it is a different type.</summary>
    public T? As<T>() where T : Resource => Resource as T;

    /// <summary>The response parsed as an OperationOutcome, when the server returned one.</summary>
    public OperationOutcome? OperationOutcome => Resource as OperationOutcome;
}

/// <summary>
/// Standards-focused HTTP helpers for talking to an external Da Vinci
/// implementation.
///
/// Deliberately thin. The helpers set the FHIR content types, parse the response
/// with the same Hl7.Fhir parser the CHO FHIR service uses, and record a
/// sanitized interaction — they do not decide what a scenario means. A test still
/// shows the method, the URL and the payload it sent, because an interop failure
/// is only diagnosable if the exchange is legible.
///
/// Nothing here retries. Retrying belongs to readiness — an external container
/// that is still coming up — and lives in <see cref="ReadinessProbe"/>. Once a
/// scenario is exchanging protocol messages, a 400 or a validation error is a
/// result to record, not something to paper over by trying again.
/// </summary>
public sealed class InteropHttpClient : IDisposable
{
    public const string FhirJsonContentType = "application/fhir+json";

    private static readonly FhirJsonParser Parser = new(new ParserSettings
    {
        AcceptUnknownMembers = true,
        PermissiveParsing = true,
        AllowUnrecognizedEnums = true,
    });

    private static readonly FhirJsonSerializer Serializer = new(new SerializerSettings { Pretty = true });

    private readonly HttpClient _http;
    private readonly string _targetName;
    private readonly List<InteropInteraction> _interactions = new();
    private readonly Dictionary<string, string> _capturedBodies = new(StringComparer.Ordinal);
    private readonly bool _ownsHttpClient;
    private int _sequence;

    public InteropHttpClient(string targetName, TimeSpan requestTimeout, HttpClient? httpClient = null)
    {
        _targetName = targetName;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient(new HttpClientHandler
        {
            // External implementations run on the loopback interface of the test
            // host. Never route them through an ambient corporate/agent proxy.
            UseProxy = false,
        });
        _http.Timeout = requestTimeout;
    }

    /// <summary>Every interaction recorded so far, in order, sanitized.</summary>
    public IReadOnlyList<InteropInteraction> Interactions => _interactions;

    /// <summary>The underlying client, for the readiness probe.</summary>
    public HttpClient RawClient => _http;

    /// <summary>
    /// Redacted request and response bodies, keyed by the artifact-relative path
    /// recorded on the corresponding interaction. The evidence writer drains this
    /// so bodies never sit in the run summary itself.
    /// </summary>
    public IReadOnlyDictionary<string, string> CapturedBodies => _capturedBodies;

    /// <summary>GET a FHIR resource, e.g. the server's CapabilityStatement.</summary>
    public Task<InteropResponse> GetFhirAsync(string url, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, url, content: null, requestBodyForCapture: null, cancellationToken);

    /// <summary>
    /// POST a FHIR resource — a $submit request bundle wrapped in Parameters, for
    /// instance. The body is serialized with the CHO FHIR serializer so what goes
    /// on the wire is what CHO's own stack would produce.
    /// </summary>
    public Task<InteropResponse> PostFhirAsync(string url, Resource resource, CancellationToken cancellationToken = default)
    {
        var json = Serializer.SerializeToString(resource);
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(FhirJsonContentType) { CharSet = "utf-8" };
        return SendAsync(HttpMethod.Post, url, content, json, cancellationToken);
    }

    /// <summary>
    /// POST a CDS Hooks service invocation.
    ///
    /// Deliberately separate from <see cref="PostFhirAsync"/>: a CDS Hooks request
    /// is plain JSON with hook-specific context and prefetch, not a FHIR resource,
    /// and sending it with a FHIR content type would misrepresent the exchange.
    /// </summary>
    /// <param name="url">The service endpoint, built from the id discovery advertised.</param>
    /// <param name="request">The request to send; its JSON is captured for evidence.</param>
    /// <param name="kind">Interaction kind recorded in evidence, e.g. "cds-hooks-invoke".</param>
    public async Task<(CdsHooksResponse? Response, InteropResponse Raw)> PostCdsHooksAsync(
        string url,
        CdsHooksRequest request,
        string kind = "cds-hooks-invoke",
        CancellationToken cancellationToken = default)
    {
        var json = request.ToJson();
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        var raw = await SendAsync(
            HttpMethod.Post, url, content, json, cancellationToken,
            kind: kind, hook: request.Hook);

        return (raw.IsSuccess ? CdsHooksResponse.Parse(raw.Body) : null, raw);
    }

    /// <summary>
    /// GET the CDS Hooks discovery document. Returns the parsed document together
    /// with the recorded interaction so a scenario can assert on both.
    /// </summary>
    public async Task<(CdsHooksDiscovery? Discovery, InteropResponse Response)> GetCdsHooksDiscoveryAsync(
        string cdsHooksBaseUrl,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            HttpMethod.Get, cdsHooksBaseUrl, content: null, requestBodyForCapture: null, cancellationToken,
            kind: "cds-hooks-discovery");
        if (!response.IsSuccess)
        {
            return (null, response);
        }

        try
        {
            var discovery = JsonSerializer.Deserialize<CdsHooksDiscovery>(
                response.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return (discovery, response);
        }
        catch (JsonException)
        {
            return (null, response);
        }
    }

    /// <summary>
    /// Acquires a SMART access token via the client-credentials flow, for external
    /// targets that require one.
    ///
    /// Synthetic credentials only: the caller passes them in, they come from the
    /// interop configuration, and the token never reaches an artifact — the
    /// Authorization header it produces is redacted on capture.
    /// </summary>
    public async Task<string> AcquireSmartTokenAsync(
        string tokenUrl,
        string clientId,
        string clientSecret,
        string scope,
        CancellationToken cancellationToken = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = scope,
        });

        var response = await SendAsync(HttpMethod.Post, tokenUrl, form,
            requestBodyForCapture: "grant_type=client_credentials&client_id=" + clientId +
                                   "&client_secret=" + Redaction.Placeholder + "&scope=" + scope,
            cancellationToken);

        if (!response.IsSuccess)
        {
            throw new InvalidOperationException(
                $"SMART token request to {Redaction.Url(tokenUrl)} failed with HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(response.Body);
        return document.RootElement.TryGetProperty("access_token", out var token)
            ? token.GetString() ?? throw new InvalidOperationException("Token response carried a null access_token.")
            : throw new InvalidOperationException("Token response carried no access_token.");
    }

    /// <summary>
    /// Presents a bearer token on subsequent requests. The value is never written
    /// to an artifact — <see cref="Redaction"/> replaces the Authorization header
    /// on capture.
    /// </summary>
    public void UseBearerToken(string accessToken) =>
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    private async Task<InteropResponse> SendAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        string? requestBodyForCapture,
        CancellationToken cancellationToken,
        string? kind = null,
        string? hook = null)
    {
        var sequence = ++_sequence;
        var stopwatch = Stopwatch.StartNew();

        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(FhirJsonContentType));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var headers = Redaction.Headers(
            request.Headers
                .Concat(_http.DefaultRequestHeaders)
                .Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value)));

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            var resource = TryParseFhir(body, response.Content.Headers.ContentType?.MediaType);
            var interaction = new InteropInteraction
            {
                Sequence = sequence,
                Target = _targetName,
                Method = method.Method,
                Url = Redaction.Url(url),
                RequestContentType = content?.Headers.ContentType?.ToString(),
                StatusCode = (int)response.StatusCode,
                ResponseContentType = response.Content.Headers.ContentType?.ToString(),
                DurationMs = stopwatch.ElapsedMilliseconds,
                ResponseResourceType = resource?.TypeName,
                OperationOutcomeIssues = SummarizeIssues(resource as OperationOutcome),
                RequestHeaders = headers,
                Kind = kind,
                Hook = hook,
            };

            interaction = interaction with
            {
                RequestArtifact = requestBodyForCapture is null
                    ? null
                    : $"requests/{sequence:D3}-{method.Method.ToLowerInvariant()}.json",
                ResponseArtifact = string.IsNullOrEmpty(body)
                    ? null
                    : $"responses/{sequence:D3}-{(int)response.StatusCode}.json",
            };

            _interactions.Add(interaction);
            if (interaction.RequestArtifact is not null)
            {
                _capturedBodies[interaction.RequestArtifact] = Redaction.Body(requestBodyForCapture);
            }

            if (interaction.ResponseArtifact is not null)
            {
                _capturedBodies[interaction.ResponseArtifact] = Redaction.Body(body);
            }

            return new InteropResponse(
                response.StatusCode,
                response.Content.Headers.ContentType?.ToString(),
                body,
                resource,
                interaction);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            var interaction = new InteropInteraction
            {
                Sequence = sequence,
                Target = _targetName,
                Method = method.Method,
                Url = Redaction.Url(url),
                RequestContentType = content?.Headers.ContentType?.ToString(),
                StatusCode = 0,
                DurationMs = stopwatch.ElapsedMilliseconds,
                RequestHeaders = headers,
                Kind = kind,
                Hook = hook,
                TransportError = $"{ex.GetType().Name}: {ex.Message}",
            };

            _interactions.Add(interaction);
            throw new InteropTransportException(
                $"{method.Method} {Redaction.Url(url)} against '{_targetName}' failed before a response was received: {ex.Message}", ex);
        }
    }

    private static Resource? TryParseFhir(string body, string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var looksJson = mediaType is null
                        || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);
        if (!looksJson)
        {
            return null;
        }

        try
        {
            return Parser.Parse<Resource>(body);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or NotSupportedException)
        {
            // A non-FHIR body (HTML error page, plain text) is a legitimate
            // observation, not a harness failure. The raw body is still captured.
            return null;
        }
    }

    private static List<string> SummarizeIssues(OperationOutcome? outcome) =>
        outcome?.Issue
            .Select(issue => $"{issue.Severity}/{issue.Code}: {issue.Details?.Text ?? issue.Diagnostics ?? "(no detail)"}")
            .ToList()
        ?? new List<string>();

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}

/// <summary>Raised when a request never reached the external implementation.</summary>
public sealed class InteropTransportException : Exception
{
    public InteropTransportException(string message, Exception inner) : base(message, inner) { }
}
