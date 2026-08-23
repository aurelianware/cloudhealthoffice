using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi;

/// <summary>Outcome of a Stedi eligibility call, including retry accounting.</summary>
internal sealed record StediApiResult(
    StediEligibilityResponseDto Response,
    int RetryCount,
    string? ExternalTransactionId);

/// <summary>
/// Thin transport client for Stedi's real-time eligibility (270/271) JSON
/// endpoint. Owns HTTP concerns only: authentication, serialization, status-code
/// classification, and retry of transient failures.
///
/// Resilience note: rather than the shared <c>AddStandardResilienceHandler</c>,
/// this client runs an explicit, configurable retry loop so the number of
/// retries can be surfaced on <see cref="GatewayTransactionMetadata"/> and so the
/// behaviour is deterministically unit-testable. Only transient categories
/// (429, 5xx, network, timeout) are retried; validation, auth, and business
/// rejections are never retried.
///
/// PHI/secret discipline: request and response bodies are never logged, and the
/// API key never appears in logs or exception messages.
/// </summary>
internal sealed class StediEligibilityApiClient
{
    /// <summary>Named <see cref="System.Net.Http.HttpClient"/> registered for Stedi.</summary>
    public const string HttpClientName = "StediHealthcare";

    private readonly StediHttpSender _sender;
    private readonly IOptions<StediGatewayOptions> _options;

    public StediEligibilityApiClient(
        IHttpClientFactory httpClientFactory,
        IOptions<StediGatewayOptions> options,
        ILogger<StediEligibilityApiClient> logger,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _options = options;
        _sender = new StediHttpSender(httpClientFactory, options, logger, timeProvider, delay);
    }

    public async Task<StediApiResult> SendEligibilityAsync(
        StediEligibilityRequestDto request, CancellationToken ct)
    {
        var opts = _options.Value;
        var payload = JsonSerializer.Serialize(request, StediHttpSender.JsonOptions);

        var http = await _sender.SendAsync(
            HttpClientName,
            HttpMethod.Post,
            opts.EligibilityPath,
            () => new StringContent(payload, Encoding.UTF8, "application/json"),
            "eligibility",
            ct).ConfigureAwait(false);

        StediEligibilityResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<StediEligibilityResponseDto>(http.Body, StediHttpSender.JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new StediApiException(
                GatewayErrorCategory.MalformedResponse,
                "Stedi returned a response that could not be parsed.", isTransient: false, inner: ex);
        }

        if (dto is null)
        {
            throw new StediApiException(
                GatewayErrorCategory.MalformedResponse, "Stedi returned an empty response.");
        }

        return new StediApiResult(dto, http.RetryCount, dto.Meta?.TraceId ?? http.RequestId);
    }
}
