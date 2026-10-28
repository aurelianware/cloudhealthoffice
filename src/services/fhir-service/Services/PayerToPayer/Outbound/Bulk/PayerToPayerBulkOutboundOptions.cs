namespace FhirService.Services.PayerToPayer.Outbound.Bulk;

/// <summary>
/// Outbound bulk Payer-to-Payer (<c>Cms0057:PayerToPayerOutbound:Bulk</c>):
/// CHO, as the new payer, asks a prior payer for many members at once over the
/// PDex bulk operations. Off by default; the directory, credentials and
/// consent gate are the single-member exchange's.
/// </summary>
public sealed class PayerToPayerBulkOutboundOptions
{
    public const string SectionName = "Cms0057:PayerToPayerOutbound:Bulk";

    /// <summary>Serves <c>PayerToPayer/$bulk-initiate</c> and runs the background worker.</summary>
    public bool Enabled { get; set; }

    /// <summary>Most members one request may name (one <c>$bulk-member-match</c>).</summary>
    public int MaxMembersPerRequest { get; set; } = 500;

    /// <summary>How often the worker looks for jobs that are due.</summary>
    public TimeSpan WorkerInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Wait between polls when the peer gives no <c>Retry-After</c>.</summary>
    public TimeSpan DefaultRetryAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A peer's <c>Retry-After</c> is held between these.</summary>
    public TimeSpan MinRetryAfter { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>A job not finished this long after it started fails as timed out.</summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long one worker holds a job while it advances it.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Largest status or match answer read from a peer (default 16 MiB).</summary>
    public long MaxResponseBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>Largest single export file (default 64 MiB).</summary>
    public long MaxFileBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Most bytes one export may total (default 256 MiB). A request's files are
    /// held together while they are sorted by member; a larger export fails
    /// the job as too large, and its members are asked for in smaller requests.
    /// </summary>
    public long MaxTotalBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Most files one export manifest may list.</summary>
    public int MaxFiles { get; set; } = 1000;

    /// <summary>
    /// How long a finished job stays readable at its status URL. Expired jobs
    /// are removed; the members' imported records are not.
    /// </summary>
    public TimeSpan JobRetention { get; set; } = TimeSpan.FromDays(30);
}
