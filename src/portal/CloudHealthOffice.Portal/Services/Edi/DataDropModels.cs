namespace CloudHealthOffice.Portal.Services;

/// <summary>Portal projection of claims-service's CpcdsDropRun: one load of the tenant's file drop.</summary>
public class DataDropRunRecord
{
    public string Id { get; set; } = string.Empty;
    public string? SourceSystem { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Running, Succeeded, CompletedWithErrors or Failed.</summary>
    public string Status { get; set; } = string.Empty;
    public DateTime? NewestFileWrittenAt { get; set; }
    public List<DataDropRunFile> Files { get; set; } = new();
    public List<DataDropStep> Steps { get; set; } = new();
    public string? ArchivedTo { get; set; }
    public List<string> Notes { get; set; } = new();
}

public class DataDropRunFile
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Members, Coverages, Claims, Authorizations ...</summary>
    public string Kind { get; set; } = string.Empty;
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }

    /// <summary>The file as received, kept in the document repository; null when it was not archived.</summary>
    public string? DocumentId { get; set; }
}

public class DataDropStep
{
    public string Name { get; set; } = string.Empty;
    public List<string> Files { get; set; } = new();

    /// <summary>Succeeded, CompletedWithErrors or Failed.</summary>
    public string Status { get; set; } = string.Empty;
    public int? HttpStatus { get; set; }
    public string? ImportBatchId { get; set; }
    public Dictionary<string, decimal> Counts { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

/// <summary>Portal projection of claims-service's CpcdsFreshnessReport.</summary>
public class DataDropFreshnessReport
{
    /// <summary>NotConfigured, NeverLoaded, Fresh, Stale or Failing.</summary>
    public string Status { get; set; } = string.Empty;
    public DateTime AsOf { get; set; }
    public string? SourceSystem { get; set; }
    public string? LastRunId { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastRunStatus { get; set; }
    public int ConsecutiveFailedRuns { get; set; }
    public DateTime? NewestClaimExtractAt { get; set; }
    public double? ClaimExtractAgeHours { get; set; }
    public DateTime? LastClaimsLoadedAt { get; set; }
    public DateTime? LastMembersLoadedAt { get; set; }
    public int WindowDays { get; set; }
    public DataDropFreshnessWindow Window { get; set; } = new();
    public List<string> Issues { get; set; } = new();
}

public class DataDropFreshnessWindow
{
    public int Runs { get; set; }
    public int FailedRuns { get; set; }
    public int ClaimFilesLoaded { get; set; }
    public decimal ClaimsCreated { get; set; }
    public decimal ClaimsUpdated { get; set; }
    public decimal ClaimsUnchanged { get; set; }
    public decimal ClaimsRejected { get; set; }
    public decimal PaidAmountInFiles { get; set; }
    public decimal PaidAmountLoaded { get; set; }
    public decimal PaidAmountNotLoaded { get; set; }
    public decimal MembersCreated { get; set; }
    public decimal MembersUpdated { get; set; }
    public decimal ProvidersCreated { get; set; }
    public decimal ProvidersNotInNppes { get; set; }
    public decimal? UnmappedPlanCodesInLastMemberLoad { get; set; }
}

/// <summary>The drop settings claims-service takes and returns (durations in whole hours).</summary>
public class DataDropSettings
{
    public bool Enabled { get; set; }
    public string? SourceSystem { get; set; }

    /// <summary>Commercial, Medicare, Medicaid, Exchange, TRICARE or VA.</summary>
    public string LineOfBusiness { get; set; } = "Commercial";
    public Dictionary<string, string>? ColumnMap { get; set; }
    public bool ShadowAfterImport { get; set; }
    public int ExpectedEveryHours { get; set; } = 24;
    public int StaleGraceHours { get; set; } = 6;
}

public class DataDropSettingsView
{
    public string TenantId { get; set; } = string.Empty;
    public bool DropEnabledForDeployment { get; set; }
    public bool Enabled { get; set; }
    public bool Loading { get; set; }

    /// <summary>None, Configuration or Stored.</summary>
    public string Source { get; set; } = string.Empty;
    public string InboundFolder { get; set; } = string.Empty;
    public DataDropSettings? Settings { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>The tenant's hosted SFTP login, as tenant-service describes it.</summary>
public class DataDropSftpAccount
{
    public bool Exists { get; set; }
    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Username { get; set; } = string.Empty;
    public string InboundFolder { get; set; } = "/inbound";
    public List<string> ReadOnlyFolders { get; set; } = new();
    public List<string> KeyFingerprints { get; set; } = new();
}

/// <summary>The result of a change the backing service may refuse: the new state, or why not.</summary>
public sealed record DataDropChange<T>(T? Value, IReadOnlyList<string> Errors) where T : class
{
    public bool Succeeded => Value is not null && Errors.Count == 0;
    public static DataDropChange<T> Ok(T value) => new(value, Array.Empty<string>());
    public static DataDropChange<T> Refused(params string[] errors) => new(null, errors);
}
