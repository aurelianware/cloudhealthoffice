namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers;

/// <summary>
/// Configuration for the canonical payer reference service, bound from
/// <c>PayerReference</c>. Synchronization against an external directory is
/// opt-in so hosts can start without live vendor credentials.
/// </summary>
public sealed class PayerReferenceOptions
{
    public const string SectionName = "PayerReference";

    public const string SourceStedi = "stedi";
    public const string SourceSeed = "seed";

    /// <summary>
    /// Persistence backend: <c>InMemory</c> (default, CI/dev) or <c>Mongo</c>
    /// when an <c>IMongoClient</c> is registered.
    /// </summary>
    public string Store { get; set; } = "InMemory";

    /// <summary>
    /// When true (default), load deterministic synthetic payers if the store
    /// is empty so CI and local hosts do not need a live directory sync.
    /// </summary>
    public bool SeedSyntheticPayers { get; set; } = true;

    /// <summary>Mongo collection names used when <see cref="Store"/> is Mongo.</summary>
    public string MongoCollectionName { get; set; } = "payer_references";

    public string MongoOverrideCollectionName { get; set; } = "payer_tenant_overrides";

    public string MongoSyncStatusCollectionName { get; set; } = "payer_sync_status";

    public string MongoDatabaseName { get; set; } = "CloudHealthOffice";

    public PayerDirectorySyncOptions Sync { get; set; } = new();

    public bool UseMongo =>
        string.Equals(Store, "Mongo", StringComparison.OrdinalIgnoreCase);
}

public sealed class PayerDirectorySyncOptions
{
    /// <summary>When false (default), the hosted refresh does not contact the vendor.</summary>
    public bool Enabled { get; set; }

    /// <summary>When true, attempt a sync as the host starts. Default false.</summary>
    public bool OnStartup { get; set; }

    /// <summary>Interval between periodic refreshes when <see cref="Enabled"/> is true.</summary>
    public int IntervalHours { get; set; } = 24;

    public int PageSize { get; set; } = 100;

    /// <summary>
    /// When true, a development/admin endpoint may trigger an on-demand sync.
    /// Default true in the options object; the controller also requires
    /// Development or this flag.
    /// </summary>
    public bool AllowOnDemandSync { get; set; } = true;
}
