using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Mapping;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi;

internal sealed record StediClaimAttachmentApiResult(
    StediCreateClaimAttachmentResponseDto Response,
    int RetryCount,
    string? ExternalTransactionId);

/// <summary>
/// Stedi 275 JSON transport: create a pre-signed upload, then PUT the file.
/// The PUT does not send the Stedi API key (pre-signed S3 URL).
/// Bodies and API keys are never logged.
/// </summary>
internal sealed class StediClaimAttachmentApiClient
{
    public const string HttpClientName = "StediClaims";

    public const string UploadHttpClientName = "StediClaimAttachmentUpload";

    private readonly StediHttpSender _sender;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IClaimAttachmentContentStore _content;
    private readonly IOptions<StediGatewayOptions> _options;
    private readonly ILogger _logger;

    public StediClaimAttachmentApiClient(
        IHttpClientFactory httpClientFactory,
        IOptions<StediGatewayOptions> options,
        IClaimAttachmentContentStore content,
        ILogger<StediClaimAttachmentApiClient> logger,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _content = content;
        _logger = logger;
        _sender = new StediHttpSender(httpClientFactory, options, logger, timeProvider, delay);
    }

    public async Task<StediClaimAttachmentApiResult> SubmitAsync(
        ClaimAttachmentSubmissionRequest request,
        ClaimAttachmentContentReference content,
        CancellationToken ct)
    {
        var create = StediClaimAttachmentMapper.ToCreateFileRequest(request);
        var payload = JsonSerializer.Serialize(create, StediHttpSender.JsonOptions);
        var path = _options.Value.ClaimAttachmentCreatePath;

        var http = await _sender.SendAsync(
            HttpClientName,
            HttpMethod.Post,
            path,
            () => new StringContent(payload, Encoding.UTF8, "application/json"),
            "claim-attachment-create",
            ct).ConfigureAwait(false);

        StediCreateClaimAttachmentResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<StediCreateClaimAttachmentResponseDto>(
                http.Body, StediHttpSender.JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new StediApiException(
                GatewayErrorCategory.MalformedResponse,
                "Stedi returned a claim attachment response that could not be parsed.",
                isTransient: false, inner: ex);
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.AttachmentId))
        {
            throw new StediApiException(
                GatewayErrorCategory.MalformedResponse,
                "Stedi returned an empty claim attachment identifier.");
        }

        var uploadUrl = dto.UploadUrl;
        if (string.IsNullOrWhiteSpace(uploadUrl) ||
            !Uri.TryCreate(uploadUrl, UriKind.Absolute, out var uploadUri))
        {
            throw new StediApiException(
                GatewayErrorCategory.MalformedResponse,
                "Stedi returned an invalid claim attachment upload URL.");
        }

        await UploadAsync(uploadUri, content, create.ContentType, ct).ConfigureAwait(false);
        return new StediClaimAttachmentApiResult(dto, http.RetryCount, dto.AttachmentId);
    }

    private async Task UploadAsync(
        Uri uploadUri,
        ClaimAttachmentContentReference content,
        string contentType,
        CancellationToken ct)
    {
        await using var stream = await _content.OpenReadAsync(content, ct).ConfigureAwait(false);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, uploadUri)
        {
            Content = new StreamContent(stream)
        };
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        var client = _httpClientFactory.CreateClient(UploadHttpClientName);
        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new StediApiException(
                GatewayErrorCategory.Timeout, "Stedi attachment upload timed out.", isTransient: true);
        }
        catch (HttpRequestException ex)
        {
            throw new StediApiException(
                GatewayErrorCategory.Connectivity,
                "Network error uploading the claim attachment.", isTransient: true, inner: ex);
        }

        using (httpResponse)
        {
            if (httpResponse.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Stedi claim attachment upload completed contentType={ContentType} contentLength={ContentLength}",
                    contentType, content.ContentLength);
                return;
            }

            throw StediHttpSender.ClassifyHttpError(httpResponse);
        }
    }
}
