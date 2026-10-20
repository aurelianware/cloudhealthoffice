using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.PlanEdits.Contracts;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;

namespace ClaimsService.Services.Adjudication.PlanEdits;

/// <summary>The plan's answer, or why there is none.</summary>
public abstract record PlanEditCallResult
{
    private PlanEditCallResult() { }

    /// <summary>A valid response: these edits fired (possibly none).</summary>
    public sealed record Answered(IReadOnlyList<PlanEdit> Edits) : PlanEditCallResult;

    /// <summary>
    /// No usable answer. <see cref="Kind"/> is one of <see cref="PlanEditUnavailableKinds"/>;
    /// <see cref="Detail"/> holds no PHI and no plan text, so it can be logged.
    /// </summary>
    public sealed record Unavailable(string Kind, string Detail) : PlanEditCallResult;
}

public static class PlanEditUnavailableKinds
{
    public const string Timeout = "timeout";
    public const string Unreachable = "unreachable";
    public const string CircuitOpen = "circuit-open";
    public const string HttpStatus = "http-status";
    public const string TooLarge = "response-too-large";
    public const string InvalidResponse = "invalid-response";
}

public interface IPlanEditServiceClient
{
    /// <summary>The tenant's plan edit service settings, or null when it has none.</summary>
    PlanEditTenantOptions? For(string tenantId);

    /// <summary>
    /// Sends <paramref name="request"/> to the tenant's plan edit service and
    /// checks the response against the contract for a claim with
    /// <paramref name="claimLineNumbers"/>. Never throws for a plan-side
    /// failure; throws <see cref="OperationCanceledException"/> only when
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<PlanEditCallResult> EvaluateAsync(
        string tenantId, PlanEditRequest request, IReadOnlyCollection<int> claimLineNumbers, CancellationToken ct);
}

/// <summary>
/// Calls a plan's edit service over HTTPS: one named client per tenant (its
/// own timeout and circuit breaker), the body signed with the tenant's secret
/// (<see cref="PlanEditSignature"/>), redirects never followed, the response
/// read up to <see cref="PlanEditContract.MaxResponseBytes"/> and checked by
/// <see cref="PlanEditResponseValidator"/>.
/// </summary>
public sealed class PlanEditServiceClient : IPlanEditServiceClient
{
    public static string ClientName(string tenantId) => $"PlanEditService:{tenantId}";

    private readonly IHttpClientFactory _clients;
    private readonly PlanEditServiceOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PlanEditServiceClient> _logger;

    public PlanEditServiceClient(
        IHttpClientFactory clients,
        IOptions<PlanEditServiceOptions> options,
        TimeProvider time,
        ILogger<PlanEditServiceClient> logger)
    {
        _clients = clients;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public PlanEditTenantOptions? For(string tenantId) =>
        !string.IsNullOrEmpty(tenantId) && _options.Tenants.TryGetValue(tenantId, out var tenant) ? tenant : null;

    public async Task<PlanEditCallResult> EvaluateAsync(
        string tenantId, PlanEditRequest request, IReadOnlyCollection<int> claimLineNumbers, CancellationToken ct)
    {
        var tenant = For(tenantId) ?? throw new InvalidOperationException($"Tenant {tenantId} has no plan edit service.");

        var body = JsonSerializer.SerializeToUtf8Bytes(request, PlanEditJson.Options);
        var timestamp = _time.GetUtcNow().ToUnixTimeSeconds();

        using var message = new HttpRequestMessage(HttpMethod.Post, tenant.Endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue(PlanEditContract.MediaType);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(PlanEditContract.MediaType));
        message.Headers.Add(PlanEditContract.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        message.Headers.Add(PlanEditContract.SignatureHeader, PlanEditSignature.Sign(tenant.SigningSecret!, timestamp, body));
        message.Headers.Add(PlanEditContract.RequestIdHeader, request.RequestId);
        message.Headers.Add(PlanEditContract.IdempotencyKeyHeader, request.RequestId);

        var started = Stopwatch.GetTimestamp();
        var result = await SendAsync(tenantId, tenant, message, claimLineNumbers, ct).ConfigureAwait(false);
        var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (result is PlanEditCallResult.Unavailable unavailable)
        {
            _logger.LogWarning(
                "Plan edit service for tenant {TenantId} gave no usable answer for request {RequestId} after {ElapsedMs} ms: {Kind} ({Detail})",
                LogSanitizer.Sanitize(tenantId), LogSanitizer.Sanitize(request.RequestId), elapsedMs,
                unavailable.Kind, unavailable.Detail);
        }
        else if (result is PlanEditCallResult.Answered answered)
        {
            _logger.LogInformation(
                "Plan edit service for tenant {TenantId} answered request {RequestId} in {ElapsedMs} ms with {Count} edit(s)",
                LogSanitizer.Sanitize(tenantId), LogSanitizer.Sanitize(request.RequestId), elapsedMs, answered.Edits.Count);
        }
        return result;
    }

    private async Task<PlanEditCallResult> SendAsync(
        string tenantId, PlanEditTenantOptions tenant, HttpRequestMessage message,
        IReadOnlyCollection<int> claimLineNumbers, CancellationToken ct)
    {
        var client = _clients.CreateClient(ClientName(tenantId));
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new PlanEditCallResult.Unavailable(PlanEditUnavailableKinds.Timeout, "no response within the timeout");
        }
        catch (HttpRequestException ex) when (ex.InnerException is BrokenCircuitException)
        {
            return new PlanEditCallResult.Unavailable(PlanEditUnavailableKinds.CircuitOpen, "circuit open after repeated failures");
        }
        catch (HttpRequestException ex)
        {
            return new PlanEditCallResult.Unavailable(PlanEditUnavailableKinds.Unreachable,
                ex.HttpRequestError.ToString());
        }

        using (response)
        {
            if ((int)response.StatusCode != 200)
            {
                return new PlanEditCallResult.Unavailable(PlanEditUnavailableKinds.HttpStatus,
                    $"HTTP {(int)response.StatusCode}; 200 is the only success");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !mediaType.Equals(PlanEditContract.MediaType, StringComparison.OrdinalIgnoreCase))
            {
                return new PlanEditCallResult.Unavailable(PlanEditUnavailableKinds.InvalidResponse,
                    $"content type must be {PlanEditContract.MediaType}");
            }

            if (response.Content.Headers.ContentLength is > PlanEditContract.MaxResponseBytes)
            {
                return new PlanEditCallResult.Unavailable(PlanEditUnavailableKinds.TooLarge,
                    $"body over {PlanEditContract.MaxResponseBytes} bytes");
            }

            // The client's timeouts stop at the response headers; the body gets
            // its own deadline, so a service that sends 200 and then stalls
            // cannot hold the adjudication consumer.
            byte[] bytes;
            using var bodyDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bodyDeadline.CancelAfter(TimeSpan.FromMilliseconds(tenant.TimeoutMilliseconds));
            try
            {
                bytes = await ReadLimitedAsync(response.Content, bodyDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new PlanEditCallResult.Unavailable(PlanEditUnavailableKinds.Timeout, "body not read within the timeout");
            }
            catch (IOException)
            {
                return new PlanEditCallResult.Unavailable(PlanEditUnavailableKinds.Unreachable, "connection lost reading the body");
            }
            catch (HttpRequestException)
            {
                return new PlanEditCallResult.Unavailable(PlanEditUnavailableKinds.Unreachable, "connection lost reading the body");
            }

            if (bytes.Length > PlanEditContract.MaxResponseBytes)
            {
                return new PlanEditCallResult.Unavailable(PlanEditUnavailableKinds.TooLarge,
                    $"body over {PlanEditContract.MaxResponseBytes} bytes");
            }

            PlanEditResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<PlanEditResponse>(bytes, PlanEditJson.Options);
            }
            catch (JsonException ex)
            {
                // The path, never the value: the value could be anything the plan sent.
                return new PlanEditCallResult.Unavailable(PlanEditUnavailableKinds.InvalidResponse,
                    $"not contract JSON at {ex.Path ?? "$"}");
            }

            var problems = PlanEditResponseValidator.Validate(parsed, claimLineNumbers);
            if (problems.Count > 0)
            {
                return new PlanEditCallResult.Unavailable(PlanEditUnavailableKinds.InvalidResponse,
                    string.Join("; ", problems.Take(5)) + (problems.Count > 5 ? $"; and {problems.Count - 5} more" : string.Empty));
            }

            return new PlanEditCallResult.Answered(parsed!.Edits!);
        }
    }

    /// <summary>Reads at most one byte past the limit, so an oversized body is detected without buffering it.</summary>
    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > PlanEditContract.MaxResponseBytes) break;
        }
        return buffer.ToArray();
    }
}
