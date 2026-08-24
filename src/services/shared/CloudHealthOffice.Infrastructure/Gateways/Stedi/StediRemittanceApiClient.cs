using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi;

internal sealed record Stedi835ReportApiResult(
    Stedi835ReportDto Report,
    int RetryCount,
    string? ExternalTransactionId);

/// <summary>
/// Stedi 835 ERA Report client (healthcare.us.stedi.com, 2024-04-01).
/// Reuses <see cref="StediHttpSender"/>. Bodies and API keys are never logged.
/// </summary>
internal sealed class StediRemittanceApiClient
{
    public const string HttpClientName = StediEligibilityApiClient.HttpClientName;

    private readonly StediHttpSender _sender;
    private readonly IOptions<StediGatewayOptions> _options;

    public StediRemittanceApiClient(
        IHttpClientFactory httpClientFactory,
        IOptions<StediGatewayOptions> options,
        ILogger<StediRemittanceApiClient> logger,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _options = options;
        _sender = new StediHttpSender(httpClientFactory, options, logger, timeProvider, delay);
    }

    public async Task<Stedi835ReportApiResult> GetReportAsync(string transactionId, CancellationToken ct)
    {
        var path = _options.Value.ResolveRemittanceReportPath(transactionId);
        var http = await _sender.SendAsync(
            HttpClientName,
            HttpMethod.Get,
            path,
            contentFactory: null,
            "remittance-report",
            ct).ConfigureAwait(false);

        Stedi835ReportDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Stedi835ReportDto>(http.Body, StediHttpSender.JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new StediApiException(
                GatewayErrorCategory.MalformedResponse,
                "Stedi returned an 835 report that could not be parsed.",
                isTransient: false, inner: ex);
        }

        if (dto is null)
        {
            throw new StediApiException(
                GatewayErrorCategory.MalformedResponse, "Stedi returned an empty 835 report.");
        }

        return new Stedi835ReportApiResult(
            dto, http.RetryCount, http.RequestId ?? dto.Meta?.TransactionId);
    }
}
