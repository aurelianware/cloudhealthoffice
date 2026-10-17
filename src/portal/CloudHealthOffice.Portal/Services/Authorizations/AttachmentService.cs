using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Net.Mail;

namespace CloudHealthOffice.Portal.Services;

public class AttachmentService : IAttachmentService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AttachmentService> _logger;

    // The CHO token for these calls is attached by ChoBearerTokenHandler.
    public AttachmentService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<AttachmentService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<AttachmentInfo>> GetAttachmentsAsync(string authorizationId)
    {
        var baseUrl = _configuration["Services:AttachmentService"];
        try
        {
            var attachments = await _httpClient.GetFromJsonAsync<List<AttachmentInfo>>($"{baseUrl}/attachments/authorization/{authorizationId}");
            return attachments ?? new List<AttachmentInfo>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Attachment Service");
            throw new ServiceUnavailableException("Attachment Service", ex);
        }
    }

    public async Task<string> UploadAttachmentAsync(string authorizationId, Stream fileStream, string fileName, string contentType)
    {
        var baseUrl = _configuration["Services:AttachmentService"];
        try
        {
            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            content.Add(streamContent, "file", fileName);
            content.Add(new StringContent(authorizationId), "authorizationId");

            var response = await _httpClient.PostAsync($"{baseUrl}/attachments/upload", content);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<UploadAttachmentResponse>();
            return result?.AttachmentId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Attachment Service");
            throw new ServiceUnavailableException("Attachment Service", ex);
        }
    }

    public async Task<Stream> DownloadAttachmentAsync(string authorizationId, string attachmentId)
    {
        var baseUrl = _configuration["Services:AttachmentService"];
        try
        {
            var response = await _httpClient.GetAsync($"{baseUrl}/attachments/{authorizationId}/{attachmentId}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Attachment Service");
            throw new ServiceUnavailableException("Attachment Service", ex);
        }
    }

    public async Task DeleteAttachmentAsync(string authorizationId, string attachmentId)
    {
        var baseUrl = _configuration["Services:AttachmentService"];
        try
        {
            var response = await _httpClient.DeleteAsync($"{baseUrl}/attachments/{authorizationId}/{attachmentId}");
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Attachment Service");
            throw new ServiceUnavailableException("Attachment Service", ex);
        }
    }

    private class UploadAttachmentResponse
    {
        public string AttachmentId { get; set; } = string.Empty;
    }
}
