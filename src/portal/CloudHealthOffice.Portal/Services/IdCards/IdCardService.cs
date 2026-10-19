using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

public class IdCardService : IIdCardService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<IdCardService> _logger;

    public IdCardService(HttpClient httpClient, IConfiguration configuration, ILogger<IdCardService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string IdCardBaseUrl =>
        _configuration["Services:IdCardService"] ?? "http://idcard-service.cloudhealthoffice/api/v1";

    public async Task<IdCardOrderView> OrderAsync(string memberId, string? languageCode = null, string? requestedBy = null)
    {
        try
        {
            var body = new
            {
                memberId,
                channel = "Digital",
                languageCode,
                requestedBy
            };
            var response = await _httpClient.PostAsJsonAsync($"{IdCardBaseUrl}/id-cards/orders", body);
            response.EnsureSuccessStatusCode();
            var order = await response.Content.ReadFromJsonAsync<IdCardOrderView>();
            return order ?? throw new Exception("Empty order response");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "ID Card Service");
            throw new ServiceUnavailableException("ID Card Service", ex);
        }
    }

    public async Task<IdCardOrderView?> GetOrderAsync(string orderId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<IdCardOrderView>($"{IdCardBaseUrl}/id-cards/{Uri.EscapeDataString(orderId)}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "ID Card Service");
            throw new ServiceUnavailableException("ID Card Service", ex);
        }
    }

    public async Task<List<IdCardHistoryView>> ListForMemberAsync(string memberId)
    {
        try
        {
            var url = $"{IdCardBaseUrl}/members/{Uri.EscapeDataString(memberId)}/id-cards";
            var result = await _httpClient.GetFromJsonAsync<List<IdCardHistoryView>>(url);
            return result ?? new List<IdCardHistoryView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "ID Card Service");
            throw new ServiceUnavailableException("ID Card Service", ex);
        }
    }

    /// <summary>
    /// A link the browser can follow: the portal's own download endpoint, which
    /// fetches the document with the signed-in user's CHO token. The browser can
    /// neither reach member-document-service nor hold a CHO token.
    /// </summary>
    public string BuildDocumentDownloadUrl(string documentId) =>
        CloudHealthOffice.Portal.Infrastructure.MemberDocumentDownloadEndpoint.PathFor(documentId);

    public async Task RevokeAsync(string cardId, string reason, string? notes = null)
    {
        try
        {
            var body = new { reason, notes };
            var response = await _httpClient.PostAsJsonAsync(
                $"{IdCardBaseUrl}/id-cards/{Uri.EscapeDataString(cardId)}/revoke", body);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "ID Card Service");
            throw new ServiceUnavailableException("ID Card Service", ex);
        }
    }
}
