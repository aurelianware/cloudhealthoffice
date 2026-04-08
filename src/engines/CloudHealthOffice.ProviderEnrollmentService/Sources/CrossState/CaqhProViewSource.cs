using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Cache;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.ProviderEnrollmentService.Sources.CrossState;

/// <summary>
/// CAQH ProView — cross-state credentialing and primary source verification.
/// Used by virtually every commercial and Medicaid plan in the US.
///
/// Unlike state adapters, CAQH is not state-specific — it provides
/// credentialing data across all states a provider is licensed in.
/// StateCode is set to "US" to indicate national scope.
///
/// Integration: CAQH ProView API v2 (REST, Basic Auth)
/// Docs: https://proview.caqh.org/api/reference
///
/// Value-add for CHO:
///   - Board certifications and expirations
///   - DEA / CDS license status
///   - Malpractice coverage verification
///   - Hospital privileges
///   - Re-attestation due dates (equivalent to Medicaid revalidation)
/// </summary>
public sealed class CaqhProViewSource : IStateEnrollmentSource
{
    public string StateCode         => "US";   // national scope
    public string SourceSystemName  => "CAQH-ProView";
    public LineOfBusiness SupportedLobs => LineOfBusiness.All;

    private readonly HttpClient _http;
    private readonly IEnrollmentRepository _cache;
    private readonly CaqhOptions _opts;
    private readonly ILogger<CaqhProViewSource> _logger;

    public CaqhProViewSource(
        HttpClient http,
        IEnrollmentRepository cache,
        IOptions<ProviderEnrollmentOptions> options,
        ILogger<CaqhProViewSource> logger)
    {
        _http   = http;
        _cache  = cache;
        _opts   = options.Value.Caqh;
        _logger = logger;

        // CAQH uses HTTP Basic auth
        var credentials = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{_opts.Username}:{_opts.Password}"));
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", credentials);
    }

    // ── Real-time lookup ──────────────────────────────────────────

    public async Task<StateEnrollmentRecord?> GetEnrollmentAsync(
        string npi, DateOnly asOfDate, CancellationToken ct = default)
    {
        var cached = await _cache.GetAsync(npi, StateCode, ct);
        if (cached is not null && !IsCacheStale(cached))
            return cached with { IsFromCache = true };

        try
        {
            // Step 1: resolve NPI → CAQH provider ID
            var caqhId = await ResolveCaqhIdAsync(npi, ct);
            if (caqhId is null)
            {
                _logger.LogDebug("CAQH: no provider ID found for NPI {Npi}", SanitizeForLog(npi));
                return null;
            }

            // Step 2: fetch provider details
            var detail = await _http.GetFromJsonAsync<CaqhProviderDetail>(
                $"/provider/{caqhId}/details?organization_id={_opts.OrganizationId}",
                cancellationToken: ct);

            if (detail is null) return null;

            var record = MapToRecord(npi, detail);
            await _cache.UpsertAsync(record, ct);
            return record;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "CAQH API unavailable for NPI {Npi}", SanitizeForLog(npi));
            return cached;
        }
    }

    public async Task<IReadOnlyList<StateEnrollmentRecord>> GetPanelAsync(
        IEnumerable<string> npis, DateOnly asOfDate, CancellationToken ct = default)
    {
        // CAQH supports batch status check — up to 100 providers per call
        var batches = npis.Chunk(100);
        var results = new List<StateEnrollmentRecord>();

        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();
            var batchResults = await Task.WhenAll(
                batch.Select(npi => GetEnrollmentAsync(npi, asOfDate, ct)));
            results.AddRange(batchResults.Where(r => r is not null).Cast<StateEnrollmentRecord>());
        }

        return results;
    }

    public Task<EnrollmentApplication?> GetApplicationStatusAsync(
        string applicationId, CancellationToken ct = default)
    {
        // CAQH does not have a separate application concept — re-attestation
        // due dates are surfaced through GetEnrollmentAsync as RevalidationDueDate
        return Task.FromResult<EnrollmentApplication?>(null);
    }

    public Task<BatchSyncResult> BulkSyncAsync(CancellationToken ct = default)
    {
        // CAQH does not offer a bulk export; panel must be refreshed via GetPanelAsync.
        // Bulk refresh is handled by the NightlyBatchSyncWorker calling GetPanelAsync
        // with the full enrolled NPI list from the CHO ProviderContract master record.
        _logger.LogInformation("CAQH does not support bulk sync; use NightlyBatchSyncWorker.RefreshCaqhPanelAsync()");
        return Task.FromResult(new BatchSyncResult
        {
            StateCode        = StateCode,
            SourceSystem     = SourceSystemName,
            SyncStarted      = DateTime.UtcNow,
            SyncCompleted    = DateTime.UtcNow,
            RecordsProcessed = 0,
            RecordsUpserted  = 0,
            RecordsSkipped   = 0,
            Errors           = 0,
            ErrorDetails     = ["CAQH bulk sync not supported; use panel refresh workflow"]
        });
    }

    // ── Helpers ───────────────────────────────────────────────────

    private async Task<string?> ResolveCaqhIdAsync(string npi, CancellationToken ct)
    {
        var response = await _http.GetFromJsonAsync<CaqhNpiLookupResponse>(
            $"/provider?npi={npi}&organization_id={_opts.OrganizationId}",
            cancellationToken: ct);
        return response?.CaqhProviderId;
    }

    private static StateEnrollmentRecord MapToRecord(string npi, CaqhProviderDetail d) => new()
    {
        Npi              = npi,
        StateCode        = "US",
        SourceSystem     = "CAQH-ProView",
        Status           = MapAttestationStatus(d.AttestationStatus),
        EffectiveDate    = DateOnly.FromDateTime(DateTime.UtcNow),
        RevalidationDueDate = string.IsNullOrEmpty(d.ReattestationDueDate)
                              ? null
                              : DateOnly.Parse(d.ReattestationDueDate),
        LastVerifiedDate = DateOnly.FromDateTime(DateTime.UtcNow),
        ProviderType     = ProviderTypeClassification.Unknown,   // enriched downstream
        SupportedLobs    = LineOfBusiness.All,
        EnrolledTaxonomies  = d.Specialties?.Select(s => s.TaxonomyCode).ToList() ?? [],
        EnrolledCounties    = [],
        McoParticipation    = [],
        RawSourcePayload    = System.Text.Json.JsonSerializer.Serialize(d)
    };

    private static EnrollmentStatus MapAttestationStatus(string status) =>
        status.ToUpperInvariant() switch
        {
            "AUTHORIZED"   => EnrollmentStatus.Active,
            "NOT_AUTHORIZED" => EnrollmentStatus.Pending,
            "EXPIRED"      => EnrollmentStatus.RevalidationRequired,
            _              => EnrollmentStatus.Unknown
        };

    private static bool IsCacheStale(StateEnrollmentRecord r) =>
        DateTime.UtcNow - r.CachedAt > TimeSpan.FromHours(24);

    // ── CAQH API response DTOs ────────────────────────────────────

    private sealed record CaqhNpiLookupResponse
    {
        [JsonPropertyName("caqh_provider_id")]
        public string? CaqhProviderId { get; init; }
    }

    private sealed record CaqhProviderDetail
    {
        [JsonPropertyName("attestation_status")]
        public string AttestationStatus         { get; init; } = string.Empty;
        [JsonPropertyName("reattestation_due_date")]
        public string ReattestationDueDate      { get; init; } = string.Empty;
        [JsonPropertyName("specialties")]
        public IList<CaqhSpecialty>? Specialties { get; init; }
    }

    private sealed record CaqhSpecialty
    {
        [JsonPropertyName("taxonomy_code")]
        public string TaxonomyCode { get; init; } = string.Empty;
        [JsonPropertyName("isPrimary")]
        public bool IsPrimary      { get; init; }
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
