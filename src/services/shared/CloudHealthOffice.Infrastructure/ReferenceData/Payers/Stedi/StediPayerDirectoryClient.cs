using System.Net.Http;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi.DTOs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi;

/// <summary>
/// Stedi List Payers JSON client. Endpoint:
/// <c>GET https://payers.us.stedi.com/2024-04-01/payers</c> (version
/// <c>2024-04-01</c>). Pagination request query parameters are
/// <c>pageSize</c> and <c>pageToken</c>; the response field
/// <c>nextPageToken</c> is sent back as the next request's <c>pageToken</c>.
/// Authentication reuses the shared Stedi HTTP sender.
/// </summary>
internal sealed class StediPayerDirectoryClient
{
    public const string HttpClientName = "StediPayerDirectory";

    private readonly StediHttpSender _sender;
    private readonly IOptions<StediGatewayOptions> _gatewayOptions;
    private readonly IOptions<PayerReferenceOptions> _referenceOptions;
    private readonly ILogger<StediPayerDirectoryClient> _logger;

    public StediPayerDirectoryClient(
        IHttpClientFactory httpClientFactory,
        IOptions<StediGatewayOptions> gatewayOptions,
        IOptions<PayerReferenceOptions> referenceOptions,
        ILogger<StediPayerDirectoryClient> logger,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _gatewayOptions = gatewayOptions;
        _referenceOptions = referenceOptions;
        _logger = logger;
        _sender = new StediHttpSender(httpClientFactory, gatewayOptions, logger, timeProvider, delay);
    }

    public async Task<IReadOnlyList<StediPayerDto>> ListAllAsync(CancellationToken ct)
    {
        var path = _gatewayOptions.Value.PayerDirectoryPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new StediApiException(
                GatewayErrorCategory.Configuration, "Stedi payer directory path is not configured.");
        }

        var pageSize = Math.Max(10, _referenceOptions.Value.Sync.PageSize);
        var payers = new List<StediPayerDto>();
        string? pageToken = null;
        var page = 0;

        do
        {
            page++;
            var url = BuildUrl(path, pageSize, pageToken);
            var http = await _sender.SendAsync(
                HttpClientName,
                HttpMethod.Get,
                url,
                contentFactory: null,
                operation: "payer-directory",
                ct).ConfigureAwait(false);

            StediPayerListResponseDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<StediPayerListResponseDto>(http.Body, StediHttpSender.JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new StediApiException(
                    GatewayErrorCategory.MalformedResponse,
                    "Stedi payer directory returned a response that could not be parsed.",
                    isTransient: false,
                    inner: ex);
            }

            if (dto?.Items is { Count: > 0 })
            {
                payers.AddRange(dto.Items);
            }

            _logger.LogInformation(
                "Stedi payer directory page {Page} received {Count} payers",
                page, dto?.Items?.Count ?? 0);

            pageToken = string.IsNullOrWhiteSpace(dto?.NextPageToken) ? null : dto!.NextPageToken;
        } while (pageToken is not null);

        return payers;
    }

    private static string BuildUrl(string path, int pageSize, string? pageToken)
    {
        var url = $"{path}?pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            url += "&pageToken=" + Uri.EscapeDataString(pageToken);
        }

        return url;
    }
}
