using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi;

internal sealed record Stedi277ReportApiResult(
    Stedi277ReportDto Report,
    int RetryCount,
    string? ExternalTransactionId);

internal sealed record StediPollApiResult(
    StediPollTransactionsDto Page,
    int RetryCount);

/// <summary>
/// Stedi 277CA Report (healthcare.us.stedi.com, 2024-04-01) and Poll
/// Transactions (core.us.stedi.com, 2023-08-01) client. Bodies and API keys
/// are never logged.
/// </summary>
internal sealed class StediClaimAcknowledgmentApiClient
{
    public const string HealthcareHttpClientName = StediEligibilityApiClient.HttpClientName;

    public const string CoreHttpClientName = "StediCore";

    private readonly StediHttpSender _sender;
    private readonly IOptions<StediGatewayOptions> _options;

    public StediClaimAcknowledgmentApiClient(
        IHttpClientFactory httpClientFactory,
        IOptions<StediGatewayOptions> options,
        ILogger<StediClaimAcknowledgmentApiClient> logger,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _options = options;
        _sender = new StediHttpSender(httpClientFactory, options, logger, timeProvider, delay);
    }

    public async Task<Stedi277ReportApiResult> GetReportAsync(string transactionId, CancellationToken ct)
    {
        var path = _options.Value.ResolveClaimAcknowledgmentReportPath(transactionId);
        var http = await _sender.SendAsync(
            HealthcareHttpClientName,
            HttpMethod.Get,
            path,
            contentFactory: null,
            "claim-acknowledgment-report",
            ct).ConfigureAwait(false);

        Stedi277ReportDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Stedi277ReportDto>(http.Body, StediHttpSender.JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new StediApiException(
                GatewayErrorCategory.MalformedResponse,
                "Stedi returned a 277CA report that could not be parsed.",
                isTransient: false, inner: ex);
        }

        if (dto is null)
        {
            throw new StediApiException(
                GatewayErrorCategory.MalformedResponse, "Stedi returned an empty 277CA report.");
        }

        return new Stedi277ReportApiResult(dto, http.RetryCount, http.RequestId ?? dto.Meta?.TransactionId);
    }

    public async Task<StediPollApiResult> PollAsync(string? startDateTime, string? pageToken, CancellationToken ct)
    {
        var path = BuildPollPath(startDateTime, pageToken);
        var http = await _sender.SendAsync(
            CoreHttpClientName,
            HttpMethod.Get,
            path,
            contentFactory: null,
            "claim-acknowledgment-poll",
            ct).ConfigureAwait(false);

        StediPollTransactionsDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<StediPollTransactionsDto>(http.Body, StediHttpSender.JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new StediApiException(
                GatewayErrorCategory.MalformedResponse,
                "Stedi returned a poll-transactions response that could not be parsed.",
                isTransient: false, inner: ex);
        }

        return new StediPollApiResult(dto ?? new StediPollTransactionsDto(), http.RetryCount);
    }

    internal string BuildPollPath(string? startDateTime, string? pageToken)
    {
        var basePath = string.IsNullOrWhiteSpace(_options.Value.PollTransactionsPath)
            ? "/2023-08-01/polling/transactions"
            : _options.Value.PollTransactionsPath;

        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            return $"{basePath}?pageToken={Uri.EscapeDataString(pageToken)}";
        }

        var start = string.IsNullOrWhiteSpace(startDateTime)
            ? DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
            : startDateTime;
        return $"{basePath}?startDateTime={Uri.EscapeDataString(start)}";
    }
}
