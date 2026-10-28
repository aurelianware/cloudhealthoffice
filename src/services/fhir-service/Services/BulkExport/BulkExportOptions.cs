namespace FhirService.Services.BulkExport;

/// <summary>
/// Bulk Data <c>$export</c> (G-34, owner decision Q-18).
///
/// <code>
///   BulkExport:Enabled                    false by default: kick-off answers 501
///   BulkExport:FileLifetime               how long a completed export's files are served (24:00:00)
///   BulkExport:PurgeAfter                 when files are deleted however they ended (2.00:00:00)
///   BulkExport:MaxResourcesPerFile        NDJSON lines per file before a new one starts (10000)
///   BulkExport:PollInterval               how often the worker looks for work (00:00:05)
///   BulkExport:MatchGroupLifetime         how long a $bulk-member-match Group can be read and exported (30.00:00:00)
///   BulkExport:MaxMembersPerMatch         MemberBundles one $bulk-member-match may carry (500)
///   BulkExport:DocumentStore:Provider     Azure | Gcs | S3 | InMemory: where the files are written,
///   BulkExport:DocumentStore:...          with that provider's settings, as for DocumentStore:*
/// </code>
///
/// The files are member data, so they live in their own private store (Q-18:
/// an unversioned bucket, so a deleted export is gone), never in the documents
/// bucket, and are only ever served back through fhir-service to the caller
/// who started the export.
/// </summary>
public sealed class BulkExportOptions
{
    public const string SectionName = "BulkExport";

    public bool Enabled { get; set; }

    public TimeSpan FileLifetime { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan PurgeAfter { get; set; } = TimeSpan.FromDays(2);

    public int MaxResourcesPerFile { get; set; } = 10_000;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A job claimed by a worker that stopped is picked up again after this long.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long the Groups a PDex <c>$bulk-member-match</c> answers with can be
    /// read and exported (<c>Group/{id}/$davinci-data-export</c>). The consents
    /// the match recorded are judged again whenever the export runs.
    /// </summary>
    public TimeSpan MatchGroupLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>The members one <c>$bulk-member-match</c> may name; a larger request is split by the payer.</summary>
    public int MaxMembersPerMatch { get; set; } = 500;

    /// <summary>The store's container (bucket prefix) for export files.</summary>
    public const string Container = "bulk-exports";

    /// <summary>The resource types a Provider Access export can contain, in the order they are written.</summary>
    public static readonly IReadOnlyList<string> SupportedTypes =
        ["Patient", "Coverage", "ExplanationOfBenefit", "Encounter", "Claim"];

    /// <summary>
    /// The resource types a Payer-to-Payer export can contain: what the
    /// single-member <c>$member-data-export</c> sends (the member, coverage,
    /// claims within five years and prior authorizations, as
    /// ExplanationOfBenefit), in the order they are written.
    /// </summary>
    public static readonly IReadOnlyList<string> PayerToPayerTypes =
        ["Patient", "Coverage", "ExplanationOfBenefit"];

    /// <summary>The types an export at <paramref name="level"/> can contain.</summary>
    public static IReadOnlyList<string> TypesFor(Models.BulkExportLevel level)
        => level == Models.BulkExportLevel.PayerToPayer ? PayerToPayerTypes : SupportedTypes;

    public void Validate()
    {
        if (FileLifetime <= TimeSpan.Zero || PurgeAfter < FileLifetime)
            throw new InvalidOperationException("BulkExport:FileLifetime must be positive and no longer than BulkExport:PurgeAfter.");
        if (MaxResourcesPerFile <= 0)
            throw new InvalidOperationException("BulkExport:MaxResourcesPerFile must be positive.");
        if (MatchGroupLifetime <= TimeSpan.Zero || MaxMembersPerMatch <= 0)
            throw new InvalidOperationException("BulkExport:MatchGroupLifetime and BulkExport:MaxMembersPerMatch must be positive.");
        if (PollInterval <= TimeSpan.Zero || LeaseDuration <= TimeSpan.Zero)
            throw new InvalidOperationException("BulkExport:PollInterval and BulkExport:LeaseDuration must be positive.");
    }
}
