namespace CloudHealthOffice.ProviderVerificationEngine.DataSources;

using CloudHealthOffice.ProviderVerificationEngine.Models;

// ─────────────────────────────────────────────────────────────────
// Adapter Interfaces — one per public data source
// Each adapter handles its own HTTP client, retry policy,
// caching, and response normalization.
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// NPPES NPI Registry — free, no auth, real-time API + weekly bulk files.
/// Endpoint: https://npiregistry.cms.hhs.gov/api/
/// Bulk: https://download.cms.gov/nppes/NPI_Files.html (V2 as of 03/2026)
/// </summary>
public interface INppesAdapter
{
    /// <summary>Real-time single-NPI lookup via NPPES Read API v2.1.</summary>
    Task<NppesProviderData?> LookupByNpiAsync(string npi, CancellationToken ct = default);

    /// <summary>Search by name + state + taxonomy for fuzzy matching.</summary>
    Task<List<NppesProviderData>> SearchAsync(NppesSearchCriteria criteria, CancellationToken ct = default);

    /// <summary>
    /// Bulk sync from NPPES weekly dissemination file (V2).
    /// Downloads, decompresses, and upserts into local provider cache.
    /// Designed to run as a scheduled background job (e.g., Sunday 2 AM).
    /// </summary>
    Task<BulkSyncResult> BulkSyncAsync(CancellationToken ct = default);
}

public class NppesSearchCriteria
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? OrganizationName { get; set; }
    public string? TaxonomyDescription { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public int Limit { get; set; } = 20;
}

/// <summary>
/// OIG LEIE + SAM.gov exclusion screening.
/// LEIE: https://oig.hhs.gov/exclusions/ (downloadable + online search)
/// SAM:  https://sam.gov/content/exclusions (API requires registration)
/// </summary>
public interface IExclusionScreeningAdapter
{
    /// <summary>Screen a single provider against all exclusion sources.</summary>
    Task<ExclusionScreeningResult> ScreenProviderAsync(
        string npi,
        string? firstName = null,
        string? lastName = null,
        DateTimeOffset? dateOfBirth = null,
        CancellationToken ct = default);

    /// <summary>
    /// Batch screen — designed for monthly full-network re-screening.
    /// Runs against local LEIE/SAM database synced from bulk files.
    /// </summary>
    Task<List<ExclusionScreeningResult>> BatchScreenAsync(
        IEnumerable<ProviderScreeningRequest> providers,
        CancellationToken ct = default);

    /// <summary>
    /// Sync LEIE updated/reinstatement files (monthly) + SAM exclusions.
    /// </summary>
    Task<BulkSyncResult> SyncExclusionListsAsync(CancellationToken ct = default);
}

public class ProviderScreeningRequest
{
    public string Npi { get; set; } = string.Empty;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Ein { get; set; }
    public DateTimeOffset? DateOfBirth { get; set; }
}

/// <summary>
/// CMS PECOS Medicare FFS Public Provider Enrollment data.
/// Source: https://data.cms.gov/provider-characteristics/medicare-provider-supplier-enrollment
/// Updated monthly. Bulk CSV download; no real-time API.
/// </summary>
public interface IPecosAdapter
{
    Task<PecosEnrollmentStatus?> GetEnrollmentStatusAsync(string npi, CancellationToken ct = default);
    Task<List<PecosReassignment>> GetReassignmentsAsync(string npi, CancellationToken ct = default);
    Task<BulkSyncResult> SyncEnrollmentDataAsync(CancellationToken ct = default);
}

/// <summary>
/// CMS Open Payments — payments from drug/device companies to providers.
/// Search: https://openpaymentsdata.cms.gov/
/// Bulk:   https://download.cms.gov/openpayments/
/// API:    data.cms.gov SODA API (free, rate-limited)
/// </summary>
public interface IOpenPaymentsAdapter
{
    Task<OpenPaymentsSummary?> GetPaymentSummaryAsync(
        string npi,
        int? programYear = null,
        CancellationToken ct = default);

    Task<BulkSyncResult> SyncPaymentDataAsync(int programYear, CancellationToken ct = default);
}

/// <summary>
/// CMS Medicare Provider Utilization & Payment data.
/// Source: https://data.cms.gov/provider-summary-by-type-of-service
/// Includes: Physician/Supplier, Part D Prescriber, Inpatient/Outpatient.
/// Bulk CSV; SODA API available on data.cms.gov.
/// </summary>
public interface IMedicareUtilizationAdapter
{
    Task<MedicareUtilizationProfile?> GetUtilizationProfileAsync(
        string npi,
        int? calendarYear = null,
        CancellationToken ct = default);

    Task<PartDPrescribingSummary?> GetPartDProfileAsync(
        string npi,
        int? calendarYear = null,
        CancellationToken ct = default);

    Task<BulkSyncResult> SyncUtilizationDataAsync(int calendarYear, CancellationToken ct = default);
}

/// <summary>
/// NLM Clinical Tables API — enriches NPPES taxonomy codes with
/// Medicare provider type/specialty crosswalk. Free, no auth.
/// https://clinicaltables.nlm.nih.gov/apidoc/npi_org/v3/doc.html
/// Also supports FHIR ValueSet $expand.
/// </summary>
public interface INlmTaxonomyCrosswalkAdapter
{
    Task<TaxonomyCrosswalkResult?> LookupTaxonomyAsync(string taxonomyCode, CancellationToken ct = default);
    Task<List<TaxonomyCrosswalkResult>> SearchBySpecialtyAsync(string specialty, CancellationToken ct = default);
}

public class TaxonomyCrosswalkResult
{
    public string TaxonomyCode { get; set; } = string.Empty;
    public string? Classification { get; set; }
    public string? Specialization { get; set; }
    public string? MedicareProviderType { get; set; }
    public string? MedicareSpecialtyCode { get; set; }
}

/// <summary>
/// FSMB Physician Data Center — PAID TIER.
/// License verification, disciplinary actions, DEA, board certs.
/// API: https://github.com/fsmb/fcvs-api (REST, requires contract)
/// </summary>
public interface IFsmbAdapter
{
    Task<FsmbLicenseVerification?> VerifyProviderAsync(string npi, CancellationToken ct = default);
    Task<List<StateLicense>> GetLicensesAsync(string npi, CancellationToken ct = default);
    Task<List<DisciplinaryAction>> GetDisciplinaryActionsAsync(string npi, CancellationToken ct = default);
    bool IsConfigured { get; }
}

// ─────────────────────────────────────────────────────────────────
// Shared
// ─────────────────────────────────────────────────────────────────

public class BulkSyncResult
{
    public string Source { get; set; } = string.Empty;
    public int RecordsProcessed { get; set; }
    public int RecordsInserted { get; set; }
    public int RecordsUpdated { get; set; }
    public int RecordsSkipped { get; set; }
    public int Errors { get; set; }
    public TimeSpan Duration { get; set; }
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
}
