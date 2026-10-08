namespace CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;

using CloudHealthOffice.ProviderVerificationEngine.Models;

/// <summary>
/// One row of a federal exclusion list (OIG LEIE or SAM.gov), normalized to a
/// common shape so one matcher screens both. Stored tenant-agnostically:
/// exclusion lists are public reference data, identical for every tenant.
/// </summary>
public class ExclusionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public ExclusionScreeningSource Source { get; set; }

    /// <summary>The sync run that loaded this row; superseded runs are deleted.</summary>
    public string SyncId { get; set; } = string.Empty;

    // ── Identity (as published) ───────────────────────────────────
    public string? LastName { get; set; }
    public string? FirstName { get; set; }
    public string? MiddleName { get; set; }
    public string? BusinessName { get; set; }

    /// <summary>10-digit NPI, or null when the list has none (LEIE uses 0000000000).</summary>
    public string? Npi { get; set; }

    public string? Upin { get; set; }
    public string? UeiSam { get; set; }
    public string? CageCode { get; set; }

    /// <summary>Date of birth as yyyyMMdd, or null when not published.</summary>
    public string? DobKey { get; set; }

    public string? Classification { get; set; }
    public string? General { get; set; }
    public string? Specialty { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }

    // ── Exclusion ─────────────────────────────────────────────────
    public string? ExclusionType { get; set; }
    public string? ExclusionProgram { get; set; }
    public string? ExcludingAgency { get; set; }
    public DateTime? ExclusionDate { get; set; }

    /// <summary>LEIE REINDATE, or SAM termination date (null when indefinite).</summary>
    public DateTime? EndDate { get; set; }

    public DateTime? WaiverDate { get; set; }
    public string? WaiverState { get; set; }

    /// <summary>SAM "Record Status" Active/Inactive; LEIE rows are always active.</summary>
    public bool IsActive { get; set; } = true;

    // ── Normalized match keys (indexed) ───────────────────────────
    public string? NormalizedLastName { get; set; }
    public string? NormalizedFirstName { get; set; }
    public string? NormalizedBusinessName { get; set; }

    /// <summary>Fill the normalized match keys from the published fields.</summary>
    public ExclusionRecord Normalize()
    {
        NormalizedLastName = NullIfEmpty(ExclusionNameNormalizer.NormalizePersonName(LastName, stripSuffixes: true));
        NormalizedFirstName = NullIfEmpty(ExclusionNameNormalizer.NormalizePersonName(FirstName));
        NormalizedBusinessName = NullIfEmpty(ExclusionNameNormalizer.NormalizeBusinessName(BusinessName));
        Npi = ExclusionNameNormalizer.NormalizeNpi(Npi);
        return this;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}

/// <summary>Sync bookkeeping for one local exclusion dataset (document id = source).</summary>
public class ExclusionSyncStatus
{
    public ExclusionScreeningSource Source { get; set; }

    /// <summary>Sync run whose rows are current.</summary>
    public string? ActiveSyncId { get; set; }

    public DateTimeOffset? LastSuccessfulSyncAt { get; set; }
    public int RecordCount { get; set; }
    public string? SourceUrl { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastError { get; set; }

    public string? LeaseHolder { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
}
