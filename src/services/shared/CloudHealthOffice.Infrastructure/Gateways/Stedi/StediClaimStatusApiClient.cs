using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi;

internal sealed record StediClaimStatusApiResult(
    StediClaimStatusResponseDto Response,
    int RetryCount,
    string? ExternalTransactionId);

/// <summary>
/// Thin transport client for Stedi's Real-Time Claim Status (276/277) JSON
/// API. Reuses <see cref="StediHttpSender"/> — no second HTTP stack.
/// Request/response bodies and the API key are never logged.
/// </summary>
internal sealed class StediClaimStatusApiClient
{
    public const string HttpClientName = StediEligibilityApiClient.HttpClientName;

    private readonly StediHttpSender _sender;
    private readonly IOptions<StediGatewayOptions> _options;

    public StediClaimStatusApiClient(
        IHttpClientFactory httpClientFactory,
        IOptions<StediGatewayOptions> options,
        ILogger<StediClaimStatusApiClient> logger,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _options = options;
        _sender = new StediHttpSender(httpClientFactory, options, logger, timeProvider, delay);
    }

    public async Task<StediClaimStatusApiResult> CheckAsync(
        StediClaimStatusRequestDto request, CancellationToken ct)
    {
        var opts = _options.Value;
        var payload = JsonSerializer.Serialize(request, StediHttpSender.JsonOptions);

        var http = await _sender.SendAsync(
            HttpClientName,
            HttpMethod.Post,
            opts.ClaimStatusPath,
            () => new StringContent(payload, Encoding.UTF8, "application/json"),
            "claim-status",
            ct).ConfigureAwait(false);

        StediClaimStatusResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<StediClaimStatusResponseDto>(http.Body, StediHttpSender.JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new StediApiException(
                GatewayErrorCategory.MalformedResponse,
                "Stedi returned a claim status response that could not be parsed.",
                isTransient: false, inner: ex);
        }

        if (dto is null)
        {
            throw new StediApiException(
                GatewayErrorCategory.MalformedResponse, "Stedi returned an empty claim status response.");
        }

        return new StediClaimStatusApiResult(
            dto,
            http.RetryCount,
            dto.Meta?.TransactionId ?? dto.Meta?.TraceId ?? dto.ControlNumber ?? http.RequestId);
    }
}
