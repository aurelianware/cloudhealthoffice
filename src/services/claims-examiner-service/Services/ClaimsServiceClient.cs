using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsExaminerService.Models;

namespace ClaimsExaminerService.Services;

public interface IClaimsServiceClient
{
    /// <summary>Fetch the full claim by id. Returns null on 404.</summary>
    Task<ClaimSnapshot?> GetClaimAsync(string claimId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Write the AI examiner's advisory recommendation back to the claim.
    /// Returns false if claims-service rejected the write (e.g., 409 because the
    /// claim is no longer Pended). Logged but not thrown — the caller should treat
    /// this as a no-op, not a poison-pill event.
    /// </summary>
    Task<bool> SetAiExaminationAsync(
        string claimId,
        string tenantId,
        AiExaminationDto examination,
        CancellationToken ct);
}

public class ClaimsServiceClient : IClaimsServiceClient
{
    private readonly HttpClient _http;
    private readonly ILogger<ClaimsServiceClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ClaimsServiceClient(HttpClient http, ILogger<ClaimsServiceClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<ClaimSnapshot?> GetClaimAsync(string claimId, string tenantId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/claims/{claimId}");
        req.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await _http.SendAsync(req, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Claim {ClaimId} not found in claims-service", claimId);
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClaimSnapshot>(JsonOptions, ct);
    }

    public async Task<bool> SetAiExaminationAsync(
        string claimId,
        string tenantId,
        AiExaminationDto examination,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/claims/{claimId}/ai-examination")
        {
            Content = JsonContent.Create(examination, options: JsonOptions)
        };
        req.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await _http.SendAsync(req, ct);

        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        // 409 Conflict means the claim is no longer in Pended status — a human
        // already acted on it before we got there. Not an error condition; the
        // examiner is intentionally non-blocking and we accept some races.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            _logger.LogInformation(
                "Skipping AI examination write for claim {ClaimId}: claim no longer Pended",
                claimId);
            return false;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogError(
            "Failed to write AI examination for claim {ClaimId}: {Status} {Body}",
            claimId, response.StatusCode, body);
        return false;
    }
}
