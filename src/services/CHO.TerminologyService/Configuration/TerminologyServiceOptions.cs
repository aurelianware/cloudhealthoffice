namespace CHO.TerminologyService.Configuration;

/// <summary>
/// Configuration for the CHO Terminology Service.
/// Loaded from appsettings.json / environment variables / Azure Key Vault.
/// </summary>
public class TerminologyServiceOptions
{
    public const string SectionName = "TerminologyService";

    /// <summary>MongoDB connection string (defaults to CHO's existing MongoDB pod)</summary>
    public string MongoConnectionString { get; set; } = "mongodb://mongodb-0.mongodb-headless:27017";

    /// <summary>MongoDB database name</summary>
    public string MongoDatabaseName { get; set; } = "cho_terminology";

    /// <summary>In-memory cache duration for translation lookups (minutes)</summary>
    public int CacheMinutes { get; set; } = 15;

    /// <summary>Maximum batch size for $batch-translate</summary>
    public int MaxBatchSize { get; set; } = 500;

    /// <summary>
    /// Auto-load maps on startup from these file paths.
    /// Useful for Docker volumes mounting map files.
    /// </summary>
    public List<AutoLoadMap> AutoLoadMaps { get; set; } = new();
}

public class AutoLoadMap
{
    /// <summary>File path (mounted volume)</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Format: RF2 or CSV</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>Map name identifier</summary>
    public string MapName { get; set; } = string.Empty;

    /// <summary>Version string</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Source system URI</summary>
    public string SourceSystem { get; set; } = string.Empty;

    /// <summary>Target system URI</summary>
    public string TargetSystem { get; set; } = string.Empty;
}
