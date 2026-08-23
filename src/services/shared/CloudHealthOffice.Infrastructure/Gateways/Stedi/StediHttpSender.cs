using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi;

/// <summary>
/// Shared Stedi HTTP transport: Authorization header, status classification,
/// Retry-After honoring, and retry of transient failures. Used by both the
/// eligibility client and the payer-directory client so authentication is not
/// reimplemented per endpoint.
///
/// Request/response bodies are never logged. The API key is applied per
/// request and never written to logs or exception messages.
/// </summary>
internal sealed class StediHttpSender
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<StediGatewayOptions> _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public StediHttpSender(
        IHttpClientFactory httpClientFactory,
        IOptions<StediGatewayOptions> options,
        ILogger logger,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delay = delay ?? ((d, ct) => Task.Delay(d, _timeProvider, ct));
    }

    public async Task<StediHttpResult> SendAsync(
        string httpClientName,
        HttpMethod method,
        string path,
        Func<HttpContent?>? contentFactory,
        string operation,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        var opts = _options.Value;
        var maxAttempts = Math.Max(1, opts.MaxRetries + 1);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var retryCount = attempt - 1;
            try
            {
                return await AttemptAsync(
                    httpClientName, method, path, contentFactory, opts.ApiKey, retryCount, ct, extraHeaders)
                    .ConfigureAwait(false);
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
                    "Stedi {Operation} attempt {Attempt}/{Max} failed transiently ({Category}); retrying in {DelayMs}ms",
                    operation, attempt, maxAttempts, ex.Category, (int)wait.TotalMilliseconds);
                await _delay(wait, ct).ConfigureAwait(false);
            }
        }

        throw new StediApiException(
            GatewayErrorCategory.ServiceUnavailable,
            $"Stedi {operation} request failed after retries.",
            isTransient: true);
    }

    private async Task<StediHttpResult> AttemptAsync(
        string httpClientName,
        HttpMethod method,
        string path,
        Func<HttpContent?>? contentFactory,
        string? apiKey,
        int retryCount,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraHeaders)
    {
        var client = _httpClientFactory.CreateClient(httpClientName);

        using var httpRequest = new HttpRequestMessage(method, path);
        var content = contentFactory?.Invoke();
        if (content is not null)
        {
            httpRequest.Content = content;
        }

        // Stedi authenticates with the raw API key in the Authorization header.
        httpRequest.Headers.TryAddWithoutValidation("Authorization", apiKey);
        if (extraHeaders is not null)
        {
            foreach (var header in extraHeaders)
            {
                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new StediApiException(
                GatewayErrorCategory.Timeout, "Stedi request timed out.", isTransient: true);
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
            var body = await httpResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (httpResponse.IsSuccessStatusCode)
            {
                return new StediHttpResult(body, retryCount, externalId);
            }

            throw ClassifyHttpError(httpResponse);
        }
    }

    internal static StediApiException ClassifyHttpError(HttpResponseMessage response)
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

    internal static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
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

    internal static string? ExtractRequestId(HttpResponseMessage response)
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

    internal static TimeSpan Backoff(int attempt)
    {
        var baseMs = Math.Min(200 * Math.Pow(2, attempt - 1), 5000);
        var jitter = Random.Shared.Next(0, 100);
        return TimeSpan.FromMilliseconds(baseMs + jitter);
    }
}

internal sealed record StediHttpResult(string Body, int RetryCount, string? RequestId);
