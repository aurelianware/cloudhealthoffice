namespace CloudHealthOffice.ProviderVerificationEngine;

using CloudHealthOffice.ProviderVerificationEngine.DataSources;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using CloudHealthOffice.ProviderVerificationEngine.Scoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Core orchestrator: drives multi-source provider verification and
/// produces a composite ProviderVerificationRecord with integrity scoring.
///
/// Verification tiers control which data sources are queried:
///   Tier 1 (Free/Instant):  NPPES + NLM Crosswalk
///   Tier 2 (Free/Bulk):     + LEIE/SAM + PECOS + Open Payments + Utilization
///   Tier 3 (Premium):       + FSMB license verification
///
/// Designed to be called both synchronously (single NPI lookup via API)
/// and as a batch worker (scheduled full-network re-verification).
/// </summary>
public class ProviderVerificationOrchestrator
{
    private readonly INppesAdapter _nppes;
    private readonly IExclusionScreeningAdapter _exclusions;
    private readonly IPecosAdapter _pecos;
    private readonly IOpenPaymentsAdapter _openPayments;
    private readonly IMedicareUtilizationAdapter _utilization;
    private readonly INlmTaxonomyCrosswalkAdapter _taxonomyCrosswalk;
    private readonly IFsmbAdapter _fsmb;
    private readonly IntegrityScoreCalculator _scoreCalculator;
    private readonly ILogger<ProviderVerificationOrchestrator> _logger;
    private readonly VerificationOptions _options;

    public ProviderVerificationOrchestrator(
        INppesAdapter nppes,
        IExclusionScreeningAdapter exclusions,
        IPecosAdapter pecos,
        IOpenPaymentsAdapter openPayments,
        IMedicareUtilizationAdapter utilization,
        INlmTaxonomyCrosswalkAdapter taxonomyCrosswalk,
        IFsmbAdapter fsmb,
        IntegrityScoreCalculator scoreCalculator,
        ILogger<ProviderVerificationOrchestrator> logger,
        IOptions<VerificationOptions> options)
    {
        _nppes = nppes;
        _exclusions = exclusions;
        _pecos = pecos;
        _openPayments = openPayments;
        _utilization = utilization;
        _taxonomyCrosswalk = taxonomyCrosswalk;
        _fsmb = fsmb;
        _scoreCalculator = scoreCalculator;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Full verification pipeline for a single NPI.
    /// Runs all configured tiers in parallel where possible.
    /// </summary>
    public async Task<ProviderVerificationRecord> VerifyProviderAsync(
        string npi,
        VerificationTier tier = VerificationTier.Standard,
        CancellationToken ct = default)
    {
        // NPI is public data published in the NPPES registry — intentionally logged for audit trail.
        var safeNpi = SanitizeForLog(npi);
        _logger.LogInformation("Starting {Tier} verification for NPI {Npi}", tier, safeNpi);

        var record = new ProviderVerificationRecord { Npi = npi };

        // ── Tier 1: NPPES (always runs) ──────────────────────────
        var nppesTask = VerifyNppesAsync(npi, ct);

        // ── Tier 2: Exclusions + PECOS + Open Payments + Utilization ──
        Task<ExclusionScreeningResult>? exclusionTask = null;
        Task<PecosEnrollmentStatus?>? pecosTask = null;
        Task<OpenPaymentsSummary?>? openPaymentsTask = null;
        Task<MedicareUtilizationProfile?>? utilizationTask = null;

        if (tier >= VerificationTier.Standard)
        {
            exclusionTask = _exclusions.ScreenProviderAsync(npi, ct: ct);
            pecosTask = _pecos.GetEnrollmentStatusAsync(npi, ct);
            openPaymentsTask = _openPayments.GetPaymentSummaryAsync(npi, ct: ct);
            utilizationTask = _utilization.GetUtilizationProfileAsync(npi, ct: ct);
        }

        // ── Tier 3: FSMB (premium, only if configured) ──────────
        Task<FsmbLicenseVerification?>? fsmbTask = null;
        if (tier >= VerificationTier.Premium && _fsmb.IsConfigured)
        {
            fsmbTask = _fsmb.VerifyProviderAsync(npi, ct);
        }

        // ── Await all and assemble ───────────────────────────────
        // Each task is awaited independently so a failure in one source
        // does not leave other tasks unobserved.
        record.NppesData = await AwaitSafeAsync(nppesTask, "NPPES", npi);

        // Enrich taxonomy codes with Medicare crosswalk
        if (record.NppesData?.Taxonomies is { Count: > 0 })
        {
            await EnrichTaxonomiesAsync(record.NppesData.Taxonomies, ct);
        }

        if (exclusionTask != null)
            record.ExclusionScreening = await AwaitSafeAsync(exclusionTask, "LEIE/SAM", npi);

        if (pecosTask != null)
            record.PecosStatus = await AwaitSafeAsync(pecosTask, "PECOS", npi);

        if (openPaymentsTask != null)
            record.OpenPaymentsSummary = await AwaitSafeAsync(openPaymentsTask, "OpenPayments", npi);

        if (utilizationTask != null)
            record.UtilizationProfile = await AwaitSafeAsync(utilizationTask, "Utilization", npi);

        if (fsmbTask != null)
            record.FsmbVerification = await AwaitSafeAsync(fsmbTask, "FSMB", npi);

        // ── Calculate composite integrity score ──────────────────
        record.IntegrityScore = _scoreCalculator.Calculate(record);
        record.Status = DeriveStatus(record);
        record.LastVerifiedAt = DateTimeOffset.UtcNow;
        record.NextScheduledVerification = DateTimeOffset.UtcNow.Add(_options.ReverificationInterval);

        // NPI is public data published in the NPPES registry — intentionally logged for audit trail.
        _logger.LogInformation(
            "Verification complete for NPI {Npi}: Score={Score}, Rating={Rating}, Status={Status}",
            safeNpi, record.IntegrityScore.CompositeScore, record.IntegrityScore.Rating, record.Status);

        return record;
    }

    /// <summary>
    /// Batch re-verification for an entire provider network.
    /// Designed to run as a scheduled background job.
    /// </summary>
    public async IAsyncEnumerable<ProviderVerificationRecord> BatchVerifyAsync(
        IEnumerable<string> npis,
        VerificationTier tier = VerificationTier.Standard,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var semaphore = new SemaphoreSlim(_options.MaxConcurrentVerifications);

        var tasks = npis.Select(async npi =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                return await VerifyProviderAsync(npi, tier, ct);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        foreach (var task in tasks)
        {
            yield return await task;
        }
    }

    // ── Private helpers ──────────────────────────────────────────

    private async Task<T?> AwaitSafeAsync<T>(Task<T> task, string source, string npi)
    {
        try
        {
            return await task;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "{Source} data source failed for NPI {Npi}; continuing with partial result", source, SanitizeForLog(npi));
            return default;
        }
    }

    private async Task<NppesProviderData?> VerifyNppesAsync(string npi, CancellationToken ct)
    {
        var data = await _nppes.LookupByNpiAsync(npi, ct);
        if (data is null)
        {
            _logger.LogWarning("NPI {Npi} not found in NPPES registry", SanitizeForLog(npi));
        }
        return data;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    private async Task EnrichTaxonomiesAsync(List<NppesTaxonomy> taxonomies, CancellationToken ct)
    {
        foreach (var taxonomy in taxonomies)
        {
            try
            {
                var crosswalk = await _taxonomyCrosswalk.LookupTaxonomyAsync(taxonomy.Code, ct);
                if (crosswalk != null)
                {
                    taxonomy.MedicareProviderType = crosswalk.MedicareProviderType;
                    taxonomy.MedicareSpecialtyCode = crosswalk.MedicareSpecialtyCode;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Taxonomy crosswalk failed for code {Code}", taxonomy.Code);
            }
        }
    }

    private static VerificationStatus DeriveStatus(ProviderVerificationRecord record)
    {
        if (record.ExclusionScreening?.IsExcluded == true)
            return VerificationStatus.Excluded;

        if (record.NppesData is null)
            return VerificationStatus.Failed;

        if (record.NppesData.NpiStatus == NppesNpiStatus.Deactivated)
            return VerificationStatus.Expired;

        return record.IntegrityScore.Rating switch
        {
            IntegrityRating.Blocked => VerificationStatus.Excluded,
            IntegrityRating.Alert => VerificationStatus.ManualReviewRequired,
            IntegrityRating.Caution => VerificationStatus.VerifiedWithWarnings,
            IntegrityRating.Advisory => VerificationStatus.VerifiedWithWarnings,
            IntegrityRating.Clear => VerificationStatus.Verified,
            _ => VerificationStatus.Pending
        };
    }
}

public enum VerificationTier
{
    /// <summary>NPPES + NLM Crosswalk only. Instant, free.</summary>
    Basic = 0,

    /// <summary>+ LEIE/SAM + PECOS + Open Payments + Utilization. Free, may use cached bulk data.</summary>
    Standard = 1,

    /// <summary>+ FSMB license verification. Requires paid FSMB contract.</summary>
    Premium = 2
}

public class VerificationOptions
{
    public const string SectionName = "ProviderVerification";

    /// <summary>How often to re-verify providers. Default: 30 days.</summary>
    public TimeSpan ReverificationInterval { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Max parallel verifications during batch runs.</summary>
    public int MaxConcurrentVerifications { get; set; } = 10;

    /// <summary>NPPES API base URL.</summary>
    public string NppesApiBaseUrl { get; set; } = "https://npiregistry.cms.hhs.gov/api/";

    /// <summary>NPPES bulk file download URL (V2).</summary>
    public string NppesBulkDownloadUrl { get; set; } = "https://download.cms.gov/nppes/NPI_Files.html";

    /// <summary>NLM Clinical Tables API base URL.</summary>
    public string NlmClinicalTablesBaseUrl { get; set; } = "https://clinicaltables.nlm.nih.gov/api/";

    /// <summary>data.cms.gov SODA API base URL.</summary>
    public string CmsDataApiBaseUrl { get; set; } = "https://data.cms.gov/";

    /// <summary>Open Payments data API endpoint.</summary>
    public string OpenPaymentsApiBaseUrl { get; set; } = "https://openpaymentsdata.cms.gov/api/1/";

    /// <summary>OIG LEIE downloadable database URL.</summary>
    public string LeieDownloadUrl { get; set; } = "https://oig.hhs.gov/exclusions/downloadables/";

    /// <summary>SAM.gov API base URL (requires API key).</summary>
    public string SamGovApiBaseUrl { get; set; } = "https://api.sam.gov/entity-information/v3/";

    /// <summary>SAM.gov API key. Free registration at sam.gov.</summary>
    public string? SamGovApiKey { get; set; }

    /// <summary>FSMB API base URL (requires contract).</summary>
    public string? FsmbApiBaseUrl { get; set; }

    /// <summary>FSMB API credentials.</summary>
    public string? FsmbClientId { get; set; }
    public string? FsmbClientSecret { get; set; }

    /// <summary>Default verification tier when not specified.</summary>
    public VerificationTier DefaultTier { get; set; } = VerificationTier.Standard;

    /// <summary>
    /// Open Payments conflict-of-interest threshold.
    /// Providers receiving above this amount trigger an Advisory flag.
    /// </summary>
    public decimal OpenPaymentsConflictThreshold { get; set; } = 25_000m;
}
