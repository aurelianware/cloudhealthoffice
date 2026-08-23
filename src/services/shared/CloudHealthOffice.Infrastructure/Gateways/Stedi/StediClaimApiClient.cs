using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi;

internal sealed record StediClaimApiResult(
    StediClaimSubmissionResponseDto Response,
    int RetryCount,
    string? ExternalTransactionId);

/// <summary>
/// Thin transport client for Stedi 837 JSON submission endpoints.
/// Bodies and API keys are never logged.
/// </summary>
internal sealed class StediClaimApiClient
{
    public const string HttpClientName = StediEligibilityApiClient.HttpClientName;

    private readonly StediHttpSender _sender;
    private readonly IOptions<StediGatewayOptions> _options;

    public StediClaimApiClient(
        IHttpClientFactory httpClientFactory,
        IOptions<StediGatewayOptions> options,
        ILogger<StediClaimApiClient> logger,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _options = options;
        _sender = new StediHttpSender(httpClientFactory, options, logger, timeProvider, delay);
    }

    public async Task<StediClaimApiResult> SubmitAsync(
        GatewayClaimType claimType,
        StediClaimSubmissionRequestDto request,
        string idempotencyKey,
        CancellationToken ct)
    {
        var opts = _options.Value;
        var path = PathFor(claimType, opts);
        var payload = JsonSerializer.Serialize(request, StediHttpSender.JsonOptions);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Idempotency-Key"] = Truncate(idempotencyKey, 255)
        };

        var http = await _sender.SendAsync(
            HttpClientName,
            HttpMethod.Post,
            path,
            () => new StringContent(payload, Encoding.UTF8, "application/json"),
            "claim-submission",
            ct,
            headers).ConfigureAwait(false);

        StediClaimSubmissionResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<StediClaimSubmissionResponseDto>(http.Body, StediHttpSender.JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new StediApiException(
                GatewayErrorCategory.MalformedResponse,
                "Stedi returned a claim submission response that could not be parsed.",
                isTransient: false, inner: ex);
        }

        if (dto is null)
        {
            throw new StediApiException(
                GatewayErrorCategory.MalformedResponse, "Stedi returned an empty claim submission response.");
        }

        return new StediClaimApiResult(dto, http.RetryCount, http.RequestId ?? dto.Meta?.TraceId);
    }

    internal static string PathFor(GatewayClaimType claimType, StediGatewayOptions opts) =>
        claimType switch
        {
            GatewayClaimType.Institutional => opts.InstitutionalClaimPath,
            GatewayClaimType.Dental => opts.DentalClaimPath,
            _ => opts.ProfessionalClaimPath
        };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
