using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProviderService.Services;

/// <summary>
/// HTTP wrapper over <c>provider-verification-service</c>. Used by the
/// integrity-projection write-back path (capability 5.4.5).
///
/// <para>
/// HTTP — not project reference — so the service boundary stays intact
/// (mirrors <c>HttpProviderIntegrityGate</c> in <c>benefit-plan-service</c>).
/// Project-referencing the verification engine into provider-service would
/// double up the six data-source HTTP clients (NPPES, LEIE, PECOS, Open
/// Payments, Medicare, FSMB).
/// </para>
/// </summary>
public interface IProviderVerificationClient
{
    /// <summary>
    /// Calls <c>POST /api/v1/providers/verify/batch</c>. The verification
    /// service caps batch size at 100 NPIs; pass at most that many. Empty
    /// or null inputs surface an empty result (no HTTP call).
    /// </summary>
    Task<IReadOnlyList<VerificationResult>> VerifyBatchAsync(
        IReadOnlyList<string> npis,
        CancellationToken ct = default);
}

public sealed class HttpProviderVerificationClient : IProviderVerificationClient
{
    public const string HttpClientName = "provider-verification";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;
    private readonly ILogger<HttpProviderVerificationClient> _logger;

    public HttpProviderVerificationClient(
        HttpClient http,
        ILogger<HttpProviderVerificationClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VerificationResult>> VerifyBatchAsync(
        IReadOnlyList<string> npis,
        CancellationToken ct = default)
    {
        if (npis is null || npis.Count == 0)
        {
            return Array.Empty<VerificationResult>();
        }
        if (npis.Count > 100)
        {
            throw new ArgumentException(
                "VerifyBatchAsync accepts at most 100 NPIs per call (provider-verification-service limit).",
                nameof(npis));
        }

        try
        {
            var payload = new BatchRequest { Npis = npis.ToList() };
            using var response = await _http.PostAsJsonAsync(
                "api/v1/providers/verify/batch", payload, _json, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "verification batch returned {StatusCode} for {Count} NPIs",
                    response.StatusCode, npis.Count);
                return Array.Empty<VerificationResult>();
            }

            var envelope = await response.Content.ReadFromJsonAsync<BatchResponse>(_json, ct);
            return envelope?.Results ?? (IReadOnlyList<VerificationResult>)Array.Empty<VerificationResult>();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-driven cancellation (worker shutdown, request abort) must
            // propagate. Without this guard we'd swallow it as an "outage" and
            // delay teardown by one full sweep cycle.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // HTTP timeout / transport failure / non-cancellation TaskCanceled
            // (e.g. HttpClient.Timeout fired). Treat as an outage so the
            // projection writer preserves cached scores instead of crashing
            // the sweep.
            _logger.LogWarning(ex,
                "verification batch failed for {Count} NPIs; preserving cached scores",
                npis.Count);
            return Array.Empty<VerificationResult>();
        }
    }

    private sealed class BatchRequest
    {
        [JsonPropertyName("npis")]
        public List<string> Npis { get; set; } = new();
    }

    private sealed class BatchResponse
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("results")]
        public List<VerificationResult> Results { get; set; } = new();
    }
}

/// <summary>
/// Slim projection of <c>ProviderVerificationRecord</c> — the only fields
/// the projection writer needs. Mirrors the verification-service response
/// shape but lives in provider-service so we don't take a project
/// reference on the engine library.
/// </summary>
public sealed class VerificationResult
{
    [JsonPropertyName("npi")]
    public string Npi { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public VerificationOutcome Status { get; set; } = VerificationOutcome.Pending;

    [JsonPropertyName("integrityScore")]
    public CompositeIntegrityScore IntegrityScore { get; set; } = new();

    [JsonPropertyName("lastVerifiedAt")]
    public DateTimeOffset LastVerifiedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("nextScheduledVerification")]
    public DateTimeOffset? NextScheduledVerification { get; set; }
}

/// <summary>Mirror of <c>ProviderIntegrityScore</c> (score + rating only).</summary>
public sealed class CompositeIntegrityScore
{
    [JsonPropertyName("compositeScore")]
    public int CompositeScore { get; set; }

    [JsonPropertyName("rating")]
    public string Rating { get; set; } = "Unknown";
}

/// <summary>
/// Mirror of <c>VerificationStatus</c>. The HTTP client deserializes by
/// name (PR #705 string-enum-strict, allowIntegerValues=false). Unknown=0
/// per project convention.
/// </summary>
public enum VerificationOutcome
{
    Unknown = 0,
    Pending = 1,
    Verified = 2,
    VerifiedWithWarnings = 3,
    Failed = 4,
    Excluded = 5,
    Expired = 6,
    ManualReviewRequired = 7,
}
