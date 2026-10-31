using ClaimsService.Models;

namespace ClaimsService.Import.Cpcds.Drop;

/// <summary>
/// The CPCDS file drop: a folder per tenant that the plan's core (or its
/// SFTP upload) writes daily extracts into, loaded without anyone calling
/// the import endpoints. Off unless <see cref="Enabled"/>, and only for the
/// tenants listed. See docs/integration/CPCDS-FILE-DROP-IMPORTER.md.
/// </summary>
public sealed class CpcdsDropOptions
{
    public const string SectionName = "CpcdsDrop";

    public bool Enabled { get; set; }

    /// <summary>
    /// The drop root. Each tenant's files arrive in <c>{Root}/{tenantId}/inbound</c>.
    /// Any storage the pod can mount works the same: a volume behind an SFTP
    /// server, Azure Blob (SFTP-enabled account, mounted with the blob CSI
    /// driver), GCS (gcsfuse) or a plain persistent volume.
    /// </summary>
    public string Root { get; set; } = "/drops";

    /// <summary>How often the worker looks for files.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A file is loaded only once it has not changed for this long, so a
    /// transfer still in progress is not read half-written.
    /// </summary>
    public TimeSpan QuietPeriod { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The tenants whose drops are loaded, by tenant id. A folder for any other tenant is ignored.</summary>
    public Dictionary<string, CpcdsDropTenantOptions> Tenants { get; set; } = new(StringComparer.Ordinal);
}

public sealed class CpcdsDropTenantOptions
{
    /// <summary>The core the extracts come from, e.g. <c>qnxt</c>; recorded on each claim.</summary>
    public string? SourceSystem { get; set; }

    public LineOfBusiness LineOfBusiness { get; set; } = LineOfBusiness.Commercial;

    /// <summary>CPCDS column name to the extract's header, for extracts that name columns differently.</summary>
    public Dictionary<string, string>? ColumnMap { get; set; }

    /// <summary>
    /// After each claim file loads, shadow-adjudicate its batch (only when
    /// shadow adjudication is also enabled for the tenant). See
    /// docs/integration/SHADOW-ADJUDICATION.md.
    /// </summary>
    public bool ShadowAfterImport { get; set; }
}
