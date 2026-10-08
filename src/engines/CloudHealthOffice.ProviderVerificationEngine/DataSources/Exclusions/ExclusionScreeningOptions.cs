namespace CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;

/// <summary>
/// Configuration for real exclusion screening (OIG LEIE + SAM.gov).
/// Section: <c>ProviderVerification:ExclusionScreening</c>.
/// <para>
/// When neither source is enabled the service keeps the placeholder adapter,
/// which reports every provider as NOT screened. Secrets (the SAM.gov API key)
/// come from configuration / Key Vault only and are never logged.
/// </para>
/// </summary>
public class ExclusionScreeningOptions
{
    public const string SectionName = "ProviderVerification:ExclusionScreening";

    public LeieOptions Leie { get; set; } = new();

    public SamOptions Sam { get; set; } = new();

    /// <summary>
    /// A local dataset (LEIE file, SAM extract) older than this never reports
    /// "screened": the provider is reported NOT screened so stale data cannot
    /// read as clear. Default 35 days (LEIE publishes monthly).
    /// </summary>
    public TimeSpan StalenessWindow { get; set; } = TimeSpan.FromDays(35);

    /// <summary>Re-download a local dataset once its last successful sync is this old. Default 1 day.</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How often the sync worker checks whether a dataset is due. Default 1 hour.</summary>
    public TimeSpan SyncCheckInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// After a failed sync, wait this long before trying again. Default 4
    /// hours — SAM.gov keys may allow only ~10 requests per day.
    /// </summary>
    public TimeSpan SyncRetryDelay { get; set; } = TimeSpan.FromHours(4);

    /// <summary>Lease held by the pod running a sync so replicas do not download concurrently.</summary>
    public TimeSpan SyncLeaseDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>HTTP timeout for bulk file downloads. Default 10 minutes.</summary>
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A new dataset with fewer records than this fraction of the previous
    /// sync is rejected (truncated / wrong file) and the previous data kept.
    /// </summary>
    public double MinimumRetainedFraction { get; set; } = 0.5;

    /// <summary>Mongo database for the tenant-agnostic exclusion collections. Null → <c>MongoDb:DatabaseName</c>.</summary>
    public string? MongoDatabaseName { get; set; }

    public string RecordsCollectionName { get; set; } = "exclusion_list_records";

    public string SyncStatusCollectionName { get; set; } = "exclusion_list_sync_status";
}

public class LeieOptions
{
    /// <summary>Enable OIG LEIE screening (local copy of the monthly UPDATED.csv).</summary>
    public bool Enabled { get; set; }

    /// <summary>Full LEIE database CSV.</summary>
    public string DownloadUrl { get; set; } = "https://oig.hhs.gov/exclusions/downloadables/UPDATED.csv";

    /// <summary>A downloaded file with fewer rows is rejected (the real file has ~80k).</summary>
    public int MinimumRecordCount { get; set; } = 10_000;
}

public enum SamScreeningMode
{
    /// <summary>Daily public exclusions extract (zipped CSV) synced into the local store. Default.</summary>
    Extract,

    /// <summary>Live per-provider calls to the SAM Exclusions API. Rate-limited per key.</summary>
    Api
}

public class SamOptions
{
    /// <summary>Enable SAM.gov exclusion screening.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Extract (default): download the public exclusions extract on a schedule
    /// and screen locally — one API call per sync, scales to whole networks.
    /// Api: one live call (or more) per provider screened; SAM keys are
    /// rate-limited (as low as ~10 requests/day for personal keys).
    /// </summary>
    public SamScreeningMode Mode { get; set; } = SamScreeningMode.Extract;

    /// <summary>
    /// SAM.gov API key (expires every 90 days). Secret: supply via Key Vault /
    /// environment, never in a committed file. Falls back to
    /// <c>ProviderVerification:SamGovApiKey</c>.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// SAM.gov Extracts download API. The API key is appended as
    /// <c>api_key</c>; the response is the zipped CSV extract.
    /// </summary>
    public string ExtractDownloadUrl { get; set; } =
        "https://api.sam.gov/data-services/v1/extracts?fileType=EXCLUSION";

    /// <summary>An extract with fewer rows is rejected (the real file has well over 100k).</summary>
    public int MinimumRecordCount { get; set; } = 10_000;

    /// <summary>Exclusions API base URL (Api mode); the version segment is appended.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.sam.gov/entity-information/";

    /// <summary>Exclusions API version segment (Api mode).</summary>
    public string ApiVersion { get; set; } = "v4";

    /// <summary>Per-request timeout (Api mode).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Retries after a 429 / 5xx / timeout before reporting NOT screened (Api mode).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay for exponential backoff; a Retry-After header wins when present.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound on a locally computed (exponential) backoff wait.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Longest server-requested Retry-After the screener will wait. The
    /// header is always honoured exactly; a longer request fails the source
    /// (reported NOT screened) rather than retrying early. Default 2 minutes.
    /// </summary>
    public TimeSpan MaxServerRetryDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Page size requested from the Exclusions API (Api mode). SAM caps this at 10.</summary>
    public int PageSize { get; set; } = 10;

    // -- Exclusions API request contract (Api mode) --------------------
    // Defaults follow the published v4 Exclusions API as best known
    // (npi, exclusionName, page/size). They are configurable because the
    // contract could not be verified against the live API from this
    // codebase; see docs/architecture/integrity-score-consumption.md.

    /// <summary>Pagination: zero-based page index (default) or record offset.</summary>
    public SamPaginationStyle PaginationStyle { get; set; } = SamPaginationStyle.PageSize;

    /// <summary>Page index (PageSize style) or offset (StartLength style) parameter, e.g. "page" or "start".</summary>
    public string PageParameter { get; set; } = "page";

    /// <summary>Page-size parameter, e.g. "size" or "length".</summary>
    public string SizeParameter { get; set; } = "size";

    public string NpiParameter { get; set; } = "npi";

    /// <summary>Single name filter (default) or separate first/last/entity name filters.</summary>
    public SamNameSearchStyle NameSearchStyle { get; set; } = SamNameSearchStyle.ExclusionName;

    public string ExclusionNameParameter { get; set; } = "exclusionName";
    public string FirstNameParameter { get; set; } = "firstName";
    public string LastNameParameter { get; set; } = "lastName";
    public string EntityNameParameter { get; set; } = "entityName";

    /// <summary>
    /// Maximum pages fetched per query (Api mode). If more results exist than
    /// this allows, the source reports NOT screened rather than a partial clear.
    /// </summary>
    public int MaxPages { get; set; } = 5;
}

public enum SamPaginationStyle
{
    /// <summary>Zero-based page index + page size (page=0&amp;size=10).</summary>
    PageSize,

    /// <summary>Record offset + page length (start=0&amp;length=10).</summary>
    StartLength
}

public enum SamNameSearchStyle
{
    /// <summary>One combined name filter ("exclusionName=John Doe").</summary>
    ExclusionName,

    /// <summary>Separate filters ("firstName=John&amp;lastName=Doe", "entityName=Acme").</summary>
    NameParts
}
