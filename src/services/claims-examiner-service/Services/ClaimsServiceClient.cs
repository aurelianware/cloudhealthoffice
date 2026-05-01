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
    /// <summary>
    /// Capability 5.9 — bounded retry on 404 from <c>GET /api/claims/{id}</c>.
    /// Mitigates the AiExaminationStage emission → PersistenceStage
    /// persistence race (Plan-First Decision 16 / D.1): the stage emits the
    /// Kafka event at Order=600 but the claim isn't persisted until
    /// Order=999. If the consumer races persistence, the GET 404s.
    /// 3 attempts × 250 ms backoff covers PersistenceStage's typical
    /// latency comfortably; if a claim genuinely doesn't exist after
    /// retry exhaustion, log and return — consumer commits offset, claim
    /// remains pended-without-AI. Operations alarm on the
    /// <c>cho.claims_examiner.claim_not_found</c> counter to catch any
    /// systemic latency regression.
    /// </summary>
    internal const int GetClaimNotFoundMaxAttempts = 3;
    internal static readonly TimeSpan GetClaimNotFoundRetryDelay = TimeSpan.FromMilliseconds(250);

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
        for (var attempt = 1; attempt <= GetClaimNotFoundMaxAttempts; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/claims/{claimId}");
            req.Headers.Add("X-Tenant-ID", tenantId);

            using var response = await _http.SendAsync(req, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                if (attempt < GetClaimNotFoundMaxAttempts)
                {
                    _logger.LogDebug(
                        "Claim {ClaimId} not yet visible (attempt {Attempt}/{Max}); retrying after {DelayMs}ms (Phase 1 stage→persistence race mitigation)",
                        claimId, attempt, GetClaimNotFoundMaxAttempts,
                        GetClaimNotFoundRetryDelay.TotalMilliseconds);
                    await Task.Delay(GetClaimNotFoundRetryDelay, ct);
                    continue;
                }

                _logger.LogWarning(
                    "Claim {ClaimId} not found after {Max} attempts; AI examination skipped",
                    claimId, GetClaimNotFoundMaxAttempts);
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ClaimSnapshot>(JsonOptions, ct);
        }

        return null;
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
