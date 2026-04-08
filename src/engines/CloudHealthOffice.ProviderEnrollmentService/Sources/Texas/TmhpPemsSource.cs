using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Cache;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace CloudHealthOffice.ProviderEnrollmentService.Sources.Texas;

/// <summary>
/// Texas Medicaid PEMS (Provider Enrollment and Management System).
/// Operated by TMHP on behalf of HHSC.
///
/// Integration strategy:
///   Primary:  TMHP Provider Lookup REST API (real-time, cache-miss path)
///   Fallback: Cosmos DB cache populated by NightlyBatchSyncWorker
///   Batch:    SFTP pull of PEMS nightly export → BulkSyncAsync
///
/// Docs: https://www.tmhp.com/resources/provider-resources/provider-enrollment
/// </summary>
public sealed class TmhpPemsSource : IStateEnrollmentSource
{
    public string StateCode         => "TX";
    public string SourceSystemName  => "PEMS";
    public LineOfBusiness SupportedLobs =>
        LineOfBusiness.Medicaid |
        LineOfBusiness.CHIP     |
        LineOfBusiness.STAR     |
        LineOfBusiness.STARPlus |
        LineOfBusiness.STARKids |
        LineOfBusiness.LTSS;

    private readonly HttpClient _http;
    private readonly IEnrollmentRepository _cache;
    private readonly TmhpPemsOptions _opts;
    private readonly ILogger<TmhpPemsSource> _logger;

    public TmhpPemsSource(
        HttpClient http,
        IEnrollmentRepository cache,
        IOptions<ProviderEnrollmentOptions> options,
        ILogger<TmhpPemsSource> logger)
    {
        _http   = http;
        _cache  = cache;
        _opts   = options.Value.Tmhp;
        _logger = logger;
    }

    // ── Real-time lookup ──────────────────────────────────────────

    public async Task<StateEnrollmentRecord?> GetEnrollmentAsync(
        string npi, DateOnly asOfDate, CancellationToken ct = default)
    {
        // 1. Try cache
        var cached = await _cache.GetAsync(npi, StateCode, ct);
        if (cached is not null && !IsCacheStale(cached))
        {
            _logger.LogDebug("PEMS cache hit for NPI {Npi}", SanitizeForLog(npi));
            return cached with { IsFromCache = true };
        }

        // 2. Live API call
        try
        {
            var response = await _http.GetFromJsonAsync<PemsApiResponse>(
                $"/provider/{npi}",
                cancellationToken: ct);

            if (response is null)
                return null;

            var record = MapToRecord(npi, response);
            await _cache.UpsertAsync(record, ct);
            return record;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "TMHP API unavailable for NPI {Npi}; returning stale cache if available", SanitizeForLog(npi));
            return cached;   // return stale rather than null — callers must check IsFromCache
        }
    }

    public async Task<IReadOnlyList<StateEnrollmentRecord>> GetPanelAsync(
        IEnumerable<string> npis, DateOnly asOfDate, CancellationToken ct = default)
    {
        // Fan-out with concurrency limit — TMHP rate limits to ~5 req/s
        var semaphore = new SemaphoreSlim(5);
        var tasks = npis.Select(async npi =>
        {
            await semaphore.WaitAsync(ct);
            try   { return await GetEnrollmentAsync(npi, asOfDate, ct); }
            finally { semaphore.Release(); }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).Cast<StateEnrollmentRecord>().ToList();
    }

    public async Task<EnrollmentApplication?> GetApplicationStatusAsync(
        string applicationId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetFromJsonAsync<PemsApplicationResponse>(
                $"/applications/{applicationId}",
                cancellationToken: ct);

            return response is null ? null : MapToApplication(response);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "TMHP application lookup failed for {ApplicationId}", applicationId);
            return null;
        }
    }

    // ── Nightly batch sync via SFTP ───────────────────────────────

    public async Task<BatchSyncResult> BulkSyncAsync(CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var errors  = new List<string>();
        int processed = 0, upserted = 0, skipped = 0;

        _logger.LogInformation("PEMS bulk sync starting from {Host}{Path}", _opts.SftpHost, _opts.BatchDropPath);

        try
        {
            using var privateKey = new PrivateKeyFile(_opts.SftpPrivateKeyPath);
            using var sftp = new SftpClient(_opts.SftpHost, _opts.SftpUsername, privateKey);

            sftp.Connect();

            // PEMS drops a nightly CSV: PEMS_PROVIDER_YYYYMMDD.csv
            var files = sftp.ListDirectory(_opts.BatchDropPath)
                .Where(f => f.Name.StartsWith("PEMS_PROVIDER_") && f.Name.EndsWith(".csv"))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(1)
                .ToList();

            foreach (var file in files)
            {
                using var stream = new MemoryStream();
                sftp.DownloadFile(file.FullName, stream);
                stream.Seek(0, SeekOrigin.Begin);

                using var reader = new StreamReader(stream);
                await reader.ReadLineAsync(ct); // skip header

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    processed++;

                    try
                    {
                        var record = ParseCsvLine(line);
                        if (record is null) { skipped++; continue; }

                        await _cache.UpsertAsync(record, ct);
                        upserted++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Line {processed}: {ex.Message}");
                        if (errors.Count >= 100) break; // safety cap
                    }
                }
            }

            sftp.Disconnect();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PEMS bulk sync failed");
            errors.Add(ex.Message);
        }

        var result = new BatchSyncResult
        {
            StateCode        = StateCode,
            SourceSystem     = SourceSystemName,
            SyncStarted      = started,
            SyncCompleted    = DateTime.UtcNow,
            RecordsProcessed = processed,
            RecordsUpserted  = upserted,
            RecordsSkipped   = skipped,
            Errors           = errors.Count,
            ErrorDetails     = errors
        };

        _logger.LogInformation(
            "PEMS bulk sync complete: {Processed} processed, {Upserted} upserted, {Errors} errors",
            processed, upserted, errors.Count);

        return result;
    }

    // ── Mapping ───────────────────────────────────────────────────

    private static StateEnrollmentRecord MapToRecord(string npi, PemsApiResponse r) => new()
    {
        Npi              = npi,
        StateCode        = "TX",
        SourceSystem     = "PEMS",
        Status           = MapStatus(r.EnrollmentStatus),
        EffectiveDate    = DateOnly.Parse(r.EffectiveDate),
        TerminationDate  = string.IsNullOrEmpty(r.TerminationDate) ? null : DateOnly.Parse(r.TerminationDate),
        RevalidationDueDate = string.IsNullOrEmpty(r.RevalidationDate) ? null : DateOnly.Parse(r.RevalidationDate),
        LastVerifiedDate = DateOnly.FromDateTime(DateTime.UtcNow),
        ProviderType     = MapProviderType(r.ProviderTypeCode),
        SupportedLobs    = MapLobs(r.Programs),
        EnrolledTaxonomies  = r.TaxonomyCodes?.ToArray() ?? [],
        EnrolledCounties    = r.CountiesServed?.ToArray() ?? [],
        McoParticipation    = r.McoContracts?.ToArray() ?? [],
        Restrictions        = MapRestrictions(r.Restrictions),
        RawSourcePayload    = System.Text.Json.JsonSerializer.Serialize(r)
    };

    private static EnrollmentApplication MapToApplication(PemsApplicationResponse r) => new()
    {
        ApplicationId       = r.ApplicationId,
        Npi                 = r.Npi,
        StateCode           = "TX",
        SourceSystem        = "PEMS",
        Status              = MapApplicationStatus(r.Status),
        SubmittedDate       = string.IsNullOrEmpty(r.SubmittedDate) ? null : DateOnly.Parse(r.SubmittedDate),
        ExpectedDecisionDate = string.IsNullOrEmpty(r.ExpectedDecisionDate) ? null : DateOnly.Parse(r.ExpectedDecisionDate),
        DenialReason        = r.DenialReason,
        OpenDeficiencies    = r.Deficiencies?.Select(d => new DeficiencyNotice
        {
            DeficiencyCode = d.Code,
            Description    = d.Description,
            DueDate        = string.IsNullOrEmpty(d.DueDate) ? null : DateOnly.Parse(d.DueDate),
            IsResolved     = d.IsResolved
        }).ToList() ?? []
    };

    private static StateEnrollmentRecord? ParseCsvLine(string line)
    {
        // PEMS CSV format: NPI,Status,EffectiveDate,TermDate,RevalDate,ProvType,Taxonomies,Counties,Programs,McoContracts
        var fields = line.Split(',');
        if (fields.Length < 10 || string.IsNullOrWhiteSpace(fields[0]))
            return null;

        return new StateEnrollmentRecord
        {
            Npi              = fields[0].Trim(),
            StateCode        = "TX",
            SourceSystem     = "PEMS",
            Status           = MapStatus(fields[1].Trim()),
            EffectiveDate    = DateOnly.Parse(fields[2].Trim()),
            TerminationDate  = string.IsNullOrEmpty(fields[3].Trim()) ? null : DateOnly.Parse(fields[3].Trim()),
            RevalidationDueDate = string.IsNullOrEmpty(fields[4].Trim()) ? null : DateOnly.Parse(fields[4].Trim()),
            ProviderType     = MapProviderType(fields[5].Trim()),
            EnrolledTaxonomies  = fields[6].Split('|', StringSplitOptions.RemoveEmptyEntries),
            EnrolledCounties    = fields[7].Split('|', StringSplitOptions.RemoveEmptyEntries),
            SupportedLobs    = MapLobString(fields[8].Trim()),
            McoParticipation    = fields[9].Split('|', StringSplitOptions.RemoveEmptyEntries),
            CachedAt         = DateTime.UtcNow
        };
    }

    private static EnrollmentStatus MapStatus(string raw) => raw.ToUpperInvariant() switch
    {
        "ACTIVE"    or "A" => EnrollmentStatus.Active,
        "PENDING"   or "P" => EnrollmentStatus.Pending,
        "SUSPENDED" or "S" => EnrollmentStatus.Suspended,
        "TERMINATED"or "T" => EnrollmentStatus.Terminated,
        "DENIED"    or "D" => EnrollmentStatus.Denied,
        _                  => EnrollmentStatus.Unknown
    };

    private static ApplicationStatus MapApplicationStatus(string raw) => raw.ToUpperInvariant() switch
    {
        "SUBMITTED"          => ApplicationStatus.Submitted,
        "PENDING_DOCUMENTS"  => ApplicationStatus.PendingDocuments,
        "UNDER_REVIEW"       => ApplicationStatus.UnderReview,
        "PENDING_APPROVAL"   => ApplicationStatus.PendingApproval,
        "APPROVED"           => ApplicationStatus.Approved,
        "DENIED"             => ApplicationStatus.Denied,
        _                    => ApplicationStatus.Draft
    };

    private static ProviderTypeClassification MapProviderType(string code) => code switch
    {
        "20" or "MD"  => ProviderTypeClassification.PhysicianMD,
        "21" or "DO"  => ProviderTypeClassification.PhysicianDO,
        "50" or "NP"  => ProviderTypeClassification.NursePractitioner,
        "51" or "PA"  => ProviderTypeClassification.PhysicianAssistant,
        "33" or "BH"  => ProviderTypeClassification.BehavioralHealth,
        "34" or "PHY" => ProviderTypeClassification.Pharmacy,
        "80" or "FAC" => ProviderTypeClassification.Facility,
        _             => ProviderTypeClassification.Other
    };

    private static LineOfBusiness MapLobs(IList<string>? programs)
    {
        if (programs is null || programs.Count == 0) return LineOfBusiness.None;
        var lob = LineOfBusiness.None;
        foreach (var p in programs) lob |= MapLobString(p);
        return lob;
    }

    private static LineOfBusiness MapLobString(string program) => program.ToUpperInvariant() switch
    {
        "MEDICAID" or "TXMD"  => LineOfBusiness.Medicaid,
        "CHIP"                => LineOfBusiness.CHIP,
        "STAR"                => LineOfBusiness.STAR,
        "STAR+"   or "STARPLUS" => LineOfBusiness.STARPlus,
        "STARKIDS"            => LineOfBusiness.STARKids,
        "LTSS"                => LineOfBusiness.LTSS,
        _                     => LineOfBusiness.None
    };

    private static IReadOnlyList<EnrollmentRestriction> MapRestrictions(
        IList<PemsRestriction>? restrictions)
    {
        if (restrictions is null) return [];
        return restrictions.Select(r => new EnrollmentRestriction
        {
            Type        = r.Type.ToUpperInvariant() switch
            {
                "PAYMENT_HOLD"       => RestrictionType.PaymentHold,
                "PREPAYMENT_REVIEW"  => RestrictionType.PrepaymentReview,
                _                   => RestrictionType.PaymentHold
            },
            Description  = r.Description,
            EffectiveDate = string.IsNullOrEmpty(r.EffectiveDate) ? null : DateOnly.Parse(r.EffectiveDate)
        }).ToList();
    }

    private static bool IsCacheStale(StateEnrollmentRecord record) =>
        DateTime.UtcNow - record.CachedAt > TimeSpan.FromHours(4);

    // ── TMHP API response DTOs ────────────────────────────────────

    private sealed record PemsApiResponse
    {
        [JsonPropertyName("enrollmentStatus")]
        public string EnrollmentStatus      { get; init; } = string.Empty;
        [JsonPropertyName("effectiveDate")]
        public string EffectiveDate         { get; init; } = string.Empty;
        [JsonPropertyName("terminationDate")]
        public string TerminationDate       { get; init; } = string.Empty;
        [JsonPropertyName("revalidationDate")]
        public string RevalidationDate      { get; init; } = string.Empty;
        [JsonPropertyName("providerTypeCode")]
        public string ProviderTypeCode      { get; init; } = string.Empty;
        [JsonPropertyName("taxonomyCodes")]
        public IList<string>? TaxonomyCodes { get; init; }
        [JsonPropertyName("countiesServed")]
        public IList<string>? CountiesServed { get; init; }
        [JsonPropertyName("programs")]
        public IList<string>? Programs      { get; init; }
        [JsonPropertyName("mcoContracts")]
        public IList<string>? McoContracts  { get; init; }
        [JsonPropertyName("restrictions")]
        public IList<PemsRestriction>? Restrictions { get; init; }
    }

    private sealed record PemsRestriction
    {
        [JsonPropertyName("type")]
        public string Type          { get; init; } = string.Empty;
        [JsonPropertyName("description")]
        public string Description   { get; init; } = string.Empty;
        [JsonPropertyName("effectiveDate")]
        public string EffectiveDate { get; init; } = string.Empty;
    }

    private sealed record PemsApplicationResponse
    {
        [JsonPropertyName("applicationId")]
        public string ApplicationId         { get; init; } = string.Empty;
        [JsonPropertyName("npi")]
        public string Npi                   { get; init; } = string.Empty;
        [JsonPropertyName("status")]
        public string Status                { get; init; } = string.Empty;
        [JsonPropertyName("submittedDate")]
        public string SubmittedDate         { get; init; } = string.Empty;
        [JsonPropertyName("expectedDecisionDate")]
        public string ExpectedDecisionDate  { get; init; } = string.Empty;
        [JsonPropertyName("denialReason")]
        public string? DenialReason         { get; init; }
        [JsonPropertyName("deficiencies")]
        public IList<PemsDeficiency>? Deficiencies { get; init; }
    }

    private sealed record PemsDeficiency
    {
        [JsonPropertyName("code")]
        public string Code          { get; init; } = string.Empty;
        [JsonPropertyName("description")]
        public string Description   { get; init; } = string.Empty;
        [JsonPropertyName("dueDate")]
        public string DueDate       { get; init; } = string.Empty;
        [JsonPropertyName("isResolved")]
        public bool IsResolved      { get; init; }
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
