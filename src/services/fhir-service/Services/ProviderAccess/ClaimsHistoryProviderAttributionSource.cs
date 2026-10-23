using System.Net;
using System.Text.Json;
using FhirService.Middleware;
using FhirService.Services;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Options;

namespace FhirService.Services.ProviderAccess;

/// <summary>
/// Raised when claims history could not be read. Distinct from "no
/// relationship": the attribution control must refuse on a fault, and it can
/// only do that if a fault is not reported as a false.
/// </summary>
public sealed class AttributionSourceUnavailableException : Exception
{
    public AttributionSourceUnavailableException(string message) : base(message) { }
}

/// <summary>
/// Attribution from the plan's own claims (CMS-0057-F PROV-02).
///
/// <para>
/// A member is this provider's patient when the plan's claims show the
/// provider treating them inside the configured lookback — the provider's NPI
/// as the billing or the rendering provider on a current, non-voided claim for
/// that member. That is a treatment relationship evidenced by the plan's own
/// records, which is what the rule describes, and unlike a configured panel it
/// needs no roster feed from a source system to be true.
/// </para>
///
/// <para>
/// This source owns the <em>policy</em> — how far back treatment counts — and
/// resolves it to an absolute instant before calling claims-service, which owns
/// only the data. Putting the window here keeps it configured in one place.
/// </para>
///
/// <para>
/// Fail-closed, and loudly: an unreachable or refusing claims-service throws
/// rather than answering false, because a false is indistinguishable from "not
/// this provider's patient" and would turn an outage into a silent, uniform
/// denial that looks like correct behaviour.
/// <see cref="ProviderAccessAuthorizationService"/> catches it and denies.
/// </para>
/// </summary>
public sealed class ClaimsHistoryProviderAttributionSource : IProviderAttributionSource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _clientFactory;
    private readonly IOptions<ProviderAttributionOptions> _options;
    private readonly ILogger<ClaimsHistoryProviderAttributionSource> _logger;
    private readonly TimeProvider _time;

    public ClaimsHistoryProviderAttributionSource(
        IHttpClientFactory clientFactory,
        IOptions<ProviderAttributionOptions> options,
        ILogger<ClaimsHistoryProviderAttributionSource> logger,
        TimeProvider? time = null)
    {
        _clientFactory = clientFactory;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// The lookback floor for a decision made at <paramref name="nowUtc"/>.
    /// Calendar months rather than a day count, so the window a plan configures
    /// as "two years" is two years whatever months it spans.
    /// </summary>
    internal static DateTime LookbackFloor(DateTime nowUtc, int lookbackMonths)
        => nowUtc.AddMonths(-lookbackMonths);

    public async Task<bool> IsAttributedAsync(
        string tenantId, string providerId, string memberId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(providerId)
            || string.IsNullOrWhiteSpace(memberId))
            return false;

        var lookbackMonths = _options.Value.ClaimsHistory.LookbackMonths;
        if (lookbackMonths <= 0)
        {
            // Start-up validation should have stopped this. If it is reached,
            // refuse rather than ask a question whose answer is always no.
            throw new AttributionSourceUnavailableException(
                "Provider attribution lookback is not configured.");
        }

        var since = LookbackFloor(_time.GetUtcNow().UtcDateTime, lookbackMonths);

        var path = "api/provider-attribution"
                   + $"?provider={Uri.EscapeDataString(providerId.Trim())}"
                   + $"&member={Uri.EscapeDataString(memberId.Trim())}"
                   + $"&since={Uri.EscapeDataString(since.ToString("o"))}";

        var client = _clientFactory.CreateClient(UpstreamClientNames.ClaimsService);

        HttpResponseMessage response;
        try
        {
            // The tenant being decided for is named on the call rather than
            // left to ambient propagation, so the question asked is about the
            // tenant the decision is for.
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add(TenantHeaderPropagationHandler.HeaderName, tenantId);
            // Set explicitly: the shared claims-service client defaults to
            // application/fhir+json for the FHIR proxies, and this endpoint is
            // not FHIR. A request that carries its own Accept keeps the
            // client default from being applied, so asking for JSON here is
            // what stops a 406. The FHIR callers are unaffected.
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            response = await client.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Category only: the exception text can name internal hosts.
            _logger.LogWarning(
                "Claims history unreachable for tenant={Tenant} ({Fault}); attribution will be denied.",
                LogSanitizer.Sanitize(tenantId), ex.GetType().Name);
            throw new AttributionSourceUnavailableException("Claims history could not be read.");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogError(
                    "Claims history refused the attribution call ({Status}) for tenant={Tenant}; "
                    + "check service authentication. Attribution will be denied.",
                    (int)response.StatusCode, LogSanitizer.Sanitize(tenantId));
                throw new AttributionSourceUnavailableException(
                    "Claims history refused the attribution call.");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Claims history answered {Status} for tenant={Tenant}; attribution will be denied.",
                    (int)response.StatusCode, LogSanitizer.Sanitize(tenantId));
                throw new AttributionSourceUnavailableException(
                    "Claims history could not answer the attribution question.");
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AttributionSourceUnavailableException(
                    "Claims history response could not be read.");
            }

            ClaimsAttributionAnswer? answer;
            try
            {
                answer = JsonSerializer.Deserialize<ClaimsAttributionAnswer>(body, JsonOptions);
            }
            catch (JsonException)
            {
                answer = null;
            }

            if (answer is null)
            {
                // An unparseable body is not a "no". It is an upstream whose
                // contract has moved, and guessing either way would be wrong.
                _logger.LogWarning(
                    "Claims history answered an unreadable body for tenant={Tenant}; "
                    + "attribution will be denied.",
                    LogSanitizer.Sanitize(tenantId));
                throw new AttributionSourceUnavailableException(
                    "Claims history answered an unreadable body.");
            }

            return answer.Attributed;
        }
    }

    private sealed record ClaimsAttributionAnswer(bool Attributed);
}
