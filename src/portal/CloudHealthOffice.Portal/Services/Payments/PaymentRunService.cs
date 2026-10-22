using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

// ── Payment Run Service ─────────────────────────────────────────────────

public class PaymentRunService : IPaymentRunService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentRunService> _logger;

    public PaymentRunService(HttpClient httpClient, IConfiguration configuration, ILogger<PaymentRunService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<PaymentRunSummary>> GetPaymentRunsAsync(int limit = 50)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<PaymentRunSummary>>($"{baseUrl}/paymentruns?limit={limit}");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<PaymentRunDetails?> GetPaymentRunByIdAsync(string runId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<PaymentRunDetails>($"{baseUrl}/paymentruns/{runId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<string> CreatePaymentRunAsync(CreatePaymentRunRequest request)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/paymentruns", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<CreateRunResponse>();
            return result?.RunId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task CancelPaymentRunAsync(string runId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var response = await _httpClient.PostAsync($"{baseUrl}/paymentruns/{runId}/cancel", null);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    /// <summary>
    /// A run's ERAs: payment-service builds one 835 per payment
    /// (GET api/Payments/{id}/835, each access audited), so the run's 835s
    /// come back as one zip, one file per payment.
    /// </summary>
    public async Task<Stream> DownloadEraForRunAsync(string runId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var run = await _httpClient.GetFromJsonAsync<RunPayments>($"{baseUrl}/paymentruns/{Uri.EscapeDataString(runId)}")
                ?? throw new InvalidOperationException($"Payment run {runId} was not found.");
            if (run.PaymentIds.Count == 0)
                throw new InvalidOperationException($"Payment run {runId} has no payments, so it has no ERAs.");

            var zip = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var paymentId in run.PaymentIds.Distinct())
                {
                    var era = await _httpClient.GetByteArrayAsync($"{baseUrl}/payments/{Uri.EscapeDataString(paymentId)}/835");
                    var entry = archive.CreateEntry($"ERA_{SafeName(paymentId)}.835", System.IO.Compression.CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(era);
                }
            }
            zip.Position = 0;
            return zip;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    private static string SafeName(string id) =>
        new(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    private class RunPayments { public List<string> PaymentIds { get; set; } = new(); }

    private class CreateRunResponse { public string RunId { get; set; } = string.Empty; }
}
