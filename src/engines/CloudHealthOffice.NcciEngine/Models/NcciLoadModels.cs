namespace CloudHealthOffice.NcciEngine.Models;

/// <summary>
/// Which CMS quarterly file a load came from.
/// </summary>
public enum NcciCmsFileKind
{
    /// <summary>Procedure-to-procedure (Column 1 / Column 2) edit table.</summary>
    Ptp,

    /// <summary>Medically Unlikely Edit table.</summary>
    Mue,
}

/// <summary>
/// Ledger row written once per (tenant, quarter, file kind, setting) CMS
/// load. The SHA-256 makes a re-run of the same file a no-op, and makes a
/// corrected re-publication of the same quarter visible as a reload.
/// </summary>
public class NcciLoadRecord
{
    /// <summary>Stable key: "{TenantId}:{Quarter}:{FileKind}:{Setting}".</summary>
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    /// <summary>CMS quarter label, e.g. "2026Q4".</summary>
    public string Quarter { get; set; } = string.Empty;

    public NcciCmsFileKind FileKind { get; set; }

    /// <summary>One of <see cref="Domain.NcciSettings"/>.</summary>
    public string Setting { get; set; } = string.Empty;

    /// <summary>Part label for a table CMS splits across files (the practitioner PTP table ships as f1–f4).</summary>
    public string? Part { get; set; }

    /// <summary>File name as supplied by the operator (informational only).</summary>
    public string? FileName { get; set; }

    /// <summary>Lower-case hex SHA-256 of the file bytes.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Rows parsed and upserted.</summary>
    public int RowsLoaded { get; set; }

    /// <summary>Non-blank lines after the first data row that the parser could not read.</summary>
    public int RowsRejected { get; set; }

    /// <summary>Rows from earlier quarters ended at this quarter's start because this file no longer lists them.</summary>
    public int RowsExpired { get; set; }

    /// <summary>Rows from an earlier load of the same quarter and slot removed because this corrected file omits them.</summary>
    public int RowsDeleted { get; set; }

    public DateTime LoadedAt { get; set; }

    /// <summary>Token subject of the caller that triggered the load, when known.</summary>
    public string? LoadedBy { get; set; }

    public static string MakeId(string tenantId, string quarter, NcciCmsFileKind kind, string setting, string? part = null)
        => string.IsNullOrEmpty(part)
            ? $"{tenantId}:{quarter}:{kind}:{setting}"
            : $"{tenantId}:{quarter}:{kind}:{setting}:{part}";
}

/// <summary>
/// One CMS file to load.
/// </summary>
public sealed class NcciLoadRequest
{
    public required string TenantId { get; init; }

    /// <summary>CMS quarter label, "YYYYQn" (e.g. "2026Q4"). Rows without their own dates take the quarter's first day.</summary>
    public required string Quarter { get; init; }

    public required NcciCmsFileKind FileKind { get; init; }

    /// <summary>One of <see cref="Domain.NcciSettings"/>.</summary>
    public required string Setting { get; init; }

    /// <summary>
    /// Optional part label ("f1".."f4") when CMS splits one table across
    /// several files; each part is ledgered separately. MUE tables are one
    /// file per setting, and a load with a part does not expire codes the
    /// file omits (another part may list them).
    /// </summary>
    public string? Part { get; init; }

    public string? FileName { get; init; }

    public string? LoadedBy { get; init; }

    /// <summary>Reload even when the ledger shows this exact file was already loaded.</summary>
    public bool Force { get; init; }
}

/// <summary>
/// Outcome of one CMS file load.
/// </summary>
public sealed class NcciLoadResult
{
    public required string Quarter { get; init; }
    public required NcciCmsFileKind FileKind { get; init; }
    public required string Setting { get; init; }
    public required string Sha256 { get; init; }

    /// <summary>True when the ledger already held this exact file and nothing was written.</summary>
    public bool AlreadyLoaded { get; init; }

    public int RowsLoaded { get; init; }
    public int RowsRejected { get; init; }
    public int RowsExpired { get; init; }
    public int RowsDeleted { get; init; }

    /// <summary>First few parser rejections (line number and reason), for the operator.</summary>
    public IReadOnlyList<string> Rejections { get; init; } = [];
}
