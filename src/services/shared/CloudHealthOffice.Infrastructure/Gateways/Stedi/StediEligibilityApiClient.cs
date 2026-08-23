using System.Net;
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<StediGatewayOptions> _options;
    private readonly ILogger<StediEligibilityApiClient> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public StediEligibilityApiClient(
        IHttpClientFactory httpClientFactory,
        IOptions<StediGatewayOptions> options,
        ILogger<StediEligibilityApiClient> logger,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delay = delay ?? ((d, ct) => Task.Delay(d, _timeProvider, ct));
    }

    public async Task<StediApiResult> SendEligibilityAsync(
        StediEligibilityRequestDto request, CancellationToken ct)
    {
        var opts = _options.Value;
        var payload = JsonSerializer.Serialize(request, JsonOptions);
        var maxAttempts = Math.Max(1, opts.MaxRetries + 1);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var retryCount = attempt - 1;
            try
            {
                return await AttemptAsync(opts, payload, retryCount, ct).ConfigureAwait(false);
            }
            catch (StediApiException ex)
            {
                ex.RetryCount = retryCount;

                if (!ex.IsTransient || attempt >= maxAttempts)
                {
                    throw;
                }

                var wait = ex.RetryAfter ?? Backoff(attempt);
                _logger.LogWarning(
                    "Stedi eligibility attempt {Attempt}/{Max} failed transiently ({Category}); retrying in {DelayMs}ms",
                    attempt, maxAttempts, ex.Category, (int)wait.TotalMilliseconds);
                await _delay(wait, ct).ConfigureAwait(false);
            }
        }

        // Unreachable: the loop either returns or throws on the final attempt.
        throw new StediApiException(
            GatewayErrorCategory.ServiceUnavailable, "Stedi eligibility request failed after retries.", isTransient: true);
    }

    private async Task<StediApiResult> AttemptAsync(
        StediGatewayOptions opts, string payload, int retryCount, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, opts.EligibilityPath)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        // Stedi authenticates with the raw API key in the Authorization header.
        // Set per-request so the key is never stored on a long-lived handler.
        httpRequest.Headers.TryAddWithoutValidation("Authorization", opts.ApiKey);

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // Client timeout (not caller cancellation).
            throw new StediApiException(
                GatewayErrorCategory.Timeout, "Stedi eligibility request timed out.", isTransient: true);
        }
        catch (HttpRequestException ex)
        {
            throw new StediApiException(
                GatewayErrorCategory.Connectivity,
                "Network error contacting Stedi.", isTransient: true, inner: ex);
        }

        using (httpResponse)
        {
            var externalId = ExtractRequestId(httpResponse);

            if (httpResponse.IsSuccessStatusCode)
            {
                var body = await httpResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                StediEligibilityResponseDto? dto;
                try
                {
                    dto = JsonSerializer.Deserialize<StediEligibilityResponseDto>(body, JsonOptions);
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

                return new StediApiResult(dto, retryCount, dto.Meta?.TraceId ?? externalId);
            }

            throw ClassifyHttpError(httpResponse);
        }
    }

    private static StediApiException ClassifyHttpError(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        return response.StatusCode switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => new StediApiException(
                GatewayErrorCategory.Validation, $"Stedi rejected the request as invalid (HTTP {status})."),
            HttpStatusCode.Unauthorized => new StediApiException(
                GatewayErrorCategory.Authentication, "Stedi authentication failed (HTTP 401)."),
            HttpStatusCode.Forbidden => new StediApiException(
                GatewayErrorCategory.Authorization, "Stedi authorization failed (HTTP 403)."),
            HttpStatusCode.TooManyRequests => new StediApiException(
                GatewayErrorCategory.RateLimited, "Stedi rate limit reached (HTTP 429).",
                isTransient: true, retryAfter: ReadRetryAfter(response)),
            _ when status >= 500 => new StediApiException(
                GatewayErrorCategory.ServiceUnavailable,
                $"Stedi is temporarily unavailable (HTTP {status}).",
                isTransient: true, retryAfter: ReadRetryAfter(response)),
            _ => new StediApiException(
                GatewayErrorCategory.Internal, $"Unexpected Stedi response (HTTP {status}).")
        };
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null)
        {
            return null;
        }
        if (ra.Delta is { } delta)
        {
            return delta;
        }
        if (ra.Date is { } date)
        {
            var diff = date - DateTimeOffset.UtcNow;
            return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
        }
        return null;
    }

    private static string? ExtractRequestId(HttpResponseMessage response)
    {
        foreach (var header in new[] { "x-request-id", "x-amzn-RequestId", "x-amzn-requestid" })
        {
            if (response.Headers.TryGetValues(header, out var values))
            {
                return values.FirstOrDefault();
            }
        }
        return null;
    }

    private static TimeSpan Backoff(int attempt)
    {
        // Exponential backoff (200ms, 400ms, 800ms, ...) with jitter, capped.
        var baseMs = Math.Min(200 * Math.Pow(2, attempt - 1), 5000);
        var jitter = Random.Shared.Next(0, 100);
        return TimeSpan.FromMilliseconds(baseMs + jitter);
    }
}
