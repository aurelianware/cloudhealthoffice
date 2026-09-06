namespace FhirService.Models.PayerToPayer;

/// <summary>
/// How Cloud Health Office treats a resource type arriving in a Payer-to-Payer
/// package. The buckets are deliberately explicit: nothing is silently dropped,
/// and CHO never claims to store a resource type its FHIR surface does not
/// actually serve.
/// </summary>
public enum ImportedResourceClass
{
    /// <summary>
    /// Member history CHO ingests and keeps: the financial/encounter record the
    /// Payer-to-Payer exchange exists to move.
    /// </summary>
    MemberHistory,

    /// <summary>
    /// Administrative context (Patient, Coverage, Organization, Practitioner,
    /// PractitionerRole, Provenance). Stored for reference resolution and
    /// traceability ONLY — it never becomes CHO's authoritative member identity,
    /// enrollment, or provider record.
    /// </summary>
    AdministrativeReference,

    /// <summary>
    /// USCDI clinical data (Condition, Observation, Procedure, MedicationRequest
    /// and the rest of <c>ClinicalResourceInventory</c>). Stored member-scoped
    /// and SERVED through CHO's Patient and Provider Access FHIR APIs — this is
    /// the class PAT-02 exists for. It stays source-attributed: an imported
    /// Condition is the prior payer's clinical assertion that CHO now serves, not
    /// a CHO-authored clinical record.
    /// </summary>
    ClinicalRecord,

    /// <summary>
    /// A resource type CHO's FHIR surface does not serve today. It is counted and
    /// named on the exchange, and the whole validated package is archived, so the
    /// data is neither lost nor misrepresented as ingested.
    /// </summary>
    Unsupported,

    // There is deliberately no `Rejected` member. A clinical resource the
    // payload validator refuses is never STAGED, so no row could ever carry that
    // classification — and an enum value no row can hold is one a future query
    // would filter on and silently get nothing back from. Refusals live where
    // they actually are: `PayerToPayerIngestionCounts.Rejected` and
    // `.RejectedReasons` on the exchange, plus the archived package.
}

/// <summary>Lifecycle of the durable ingestion that follows a successful exchange.</summary>
public enum PayerToPayerIngestionStatus
{
    /// <summary>No ingestion has been attempted for this exchange.</summary>
    NotStarted,

    /// <summary>Resources are being staged; nothing is visible to readers yet.</summary>
    Staging,

    /// <summary>All resources are staged and the package is archived; the commit has not landed.</summary>
    Staged,

    /// <summary>The import is committed and visible as the member's imported history.</summary>
    Completed,

    /// <summary>Ingestion failed; nothing from this attempt is visible. See the failure category.</summary>
    Failed,
}

/// <summary>Structured reason an ingestion did not complete.</summary>
public enum PayerToPayerIngestionFailure
{
    None,

    /// <summary>The ingestion context (tenant / member / exchange / source payer) was incomplete.</summary>
    InvalidContext,

    /// <summary>The package carried nothing CHO could stage.</summary>
    EmptyPackage,

    /// <summary>A resource could not be serialized for storage.</summary>
    UnreadableResource,

    /// <summary>The durable store rejected or failed the staging writes.</summary>
    StagingFailed,

    /// <summary>Staging succeeded but the commit did not land — the import stays invisible and is retryable.</summary>
    CommitFailed,
}

/// <summary>
/// The validated, exchange-bound context an ingestion runs under. Every field
/// comes from the Payer-to-Payer exchange Cloud Health Office itself drove —
/// never from the remote payer's Bundle — so a peer cannot redirect an import to
/// another tenant or member.
/// </summary>
public sealed class PayerToPayerIngestionContext
{
    public required string TenantId { get; init; }

    /// <summary>The CHO member the exchange resolved locally.</summary>
    public required string MemberId { get; init; }

    /// <summary>The payer the package came from.</summary>
    public required string SourcePayerId { get; init; }

    /// <summary>Directory key of the endpoint the package came from (opaque; never a URL).</summary>
    public string? SourceEndpointKey { get; init; }

    public required string ExchangeId { get; init; }

    /// <summary>The member id as the REMOTE payer knows them (source-side identity).</summary>
    public required string RemoteMemberId { get; init; }

    public DateTime ReceivedAtUtc { get; init; } = DateTime.UtcNow;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(MemberId)
        && !string.IsNullOrWhiteSpace(SourcePayerId)
        && !string.IsNullOrWhiteSpace(ExchangeId)
        && !string.IsNullOrWhiteSpace(RemoteMemberId);
}

/// <summary>
/// One resource imported from another payer, as CHO stores it.
///
/// Every row is bound to the tenant, the local member, the source payer, and the
/// exchange that produced it, and it keeps the resource's identity at the source.
/// Imported rows live apart from CHO-authoritative data: reading one can never be
/// mistaken for reading CHO's own member, enrollment, or claim record.
/// </summary>
public sealed class ImportedFhirResource
{
    /// <summary>
    /// Deterministic identity for the imported resource:
    /// tenant + local member + source payer + resource type + source resource id.
    /// A replay of the same package resolves to the same key (so it updates in
    /// place rather than duplicating), while the SAME source id from a DIFFERENT
    /// payer is a different key — two payers' resources are never merged.
    /// </summary>
    public required string ImportKey { get; init; }

    public required string TenantId { get; init; }
    public required string MemberId { get; init; }
    public required string SourcePayerId { get; init; }
    public string? SourceEndpointKey { get; init; }

    /// <summary>Exchange that most recently staged this resource.</summary>
    public required string ExchangeId { get; init; }

    public required string ResourceType { get; init; }

    /// <summary>The resource's id at the source payer (its identity over there).</summary>
    public required string SourceResourceId { get; init; }

    /// <summary>The member id the source payer knows, for source-side traceability.</summary>
    public required string RemoteMemberId { get; init; }

    public ImportedResourceClass Classification { get; init; }

    /// <summary>The resource as FHIR JSON, exactly as validated (references normalized).</summary>
    public required string ResourceJson { get; init; }

    /// <summary>SHA-256 of the stored JSON — lets a replay tell "same again" from "changed".</summary>
    public required string ContentHash { get; init; }

    /// <summary>True when CHO rewrote intra-package references during ingestion.</summary>
    public bool ReferencesNormalized { get; init; }

    /// <summary>When the package was received from the source payer.</summary>
    public DateTime ReceivedAtUtc { get; init; }

    /// <summary>When CHO staged this row (CHO's own ingestion act, distinct from receipt).</summary>
    public DateTime IngestedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Per-exchange import ledger. It is the commit point: resources are staged
/// first, and only a ledger entry marked <see cref="PayerToPayerIngestionStatus.Completed"/>
/// makes them visible as the member's imported history. Because the flip is a
/// single-document write, a failure part-way through staging can never leave a
/// member with a half-imported package.
/// </summary>
public sealed class PayerToPayerImportLedgerEntry
{
    public required string ExchangeId { get; init; }
    public required string TenantId { get; init; }
    public required string MemberId { get; init; }
    public required string SourcePayerId { get; init; }

    public PayerToPayerIngestionStatus Status { get; set; } = PayerToPayerIngestionStatus.Staging;
    public PayerToPayerIngestionFailure Failure { get; set; } = PayerToPayerIngestionFailure.None;

    /// <summary>
    /// The validated package as received (FHIR JSON), archived verbatim. Resource
    /// types CHO does not serve are preserved here rather than discarded, so an
    /// import never loses member data it could not project.
    /// </summary>
    public string? ArchivedPackageJson { get; set; }

    public PayerToPayerIngestionCounts Counts { get; set; } = new();

    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Set when the PAT-02 clinical backfill re-projected this exchange's
    /// archived package. It keeps "these clinical rows arrived later, from the
    /// archive, not from the original ingestion" answerable after the counts
    /// have been brought up to date. Null on an exchange ingested after clinical
    /// serving existed.
    /// </summary>
    public DateTime? ClinicalBackfilledAtUtc { get; set; }
}

/// <summary>What an ingestion did, in numbers — the auditable, PHI-free summary.</summary>
public sealed class PayerToPayerIngestionCounts
{
    /// <summary>Resources in the validated package.</summary>
    public int Received { get; set; }

    /// <summary>Member-history resources stored.</summary>
    public int Persisted { get; set; }

    /// <summary>Administrative resources stored for reference/traceability only.</summary>
    public int AdministrativeReference { get; set; }

    /// <summary>USCDI clinical resources stored and served through Patient/Provider Access (PAT-02).</summary>
    public int Clinical { get; set; }

    /// <summary>Clinical resources refused by the payload validator — counted, never silently dropped.</summary>
    public int Rejected { get; set; }

    /// <summary>
    /// Why each rejected resource was refused: <c>"{ResourceType}:{reason}"</c>,
    /// sorted. Categories only — never the payload that was refused.
    /// </summary>
    public IReadOnlyList<string> RejectedReasons { get; set; } = Array.Empty<string>();

    /// <summary>Resources already held from this payer with identical content (replay).</summary>
    public int Duplicate { get; set; }

    /// <summary>Resources whose type CHO's FHIR surface does not serve.</summary>
    public int Unsupported { get; set; }

    /// <summary>The distinct unsupported resource type names, sorted — named, not just counted.</summary>
    public IReadOnlyList<string> UnsupportedTypes { get; set; } = Array.Empty<string>();

    /// <summary>Intra-package references rewritten to CHO's imported identities.</summary>
    public int ReferencesNormalized { get; set; }
}

/// <summary>Outcome of a durable ingestion.</summary>
public sealed class PayerToPayerIngestionResult
{
    public PayerToPayerIngestionStatus Status { get; init; }
    public PayerToPayerIngestionFailure Failure { get; init; }
    public PayerToPayerIngestionCounts Counts { get; init; } = new();
    public DateTime StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }

    /// <summary>True when this ingestion re-ran an exchange already committed.</summary>
    public bool IsReplay { get; init; }

    public bool Succeeded => Status == PayerToPayerIngestionStatus.Completed;

    public static PayerToPayerIngestionResult Failed(
        PayerToPayerIngestionFailure failure, DateTime startedAtUtc, PayerToPayerIngestionCounts? counts = null) =>
        new()
        {
            Status = PayerToPayerIngestionStatus.Failed,
            Failure = failure,
            Counts = counts ?? new PayerToPayerIngestionCounts(),
            StartedAtUtc = startedAtUtc,
        };
}
