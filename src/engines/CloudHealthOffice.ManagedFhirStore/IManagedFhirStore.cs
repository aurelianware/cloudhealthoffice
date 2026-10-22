namespace CloudHealthOffice.ManagedFhirStore;

/// <summary>
/// A cloud provider's managed FHIR R4 server that CHO publishes projections
/// into (ADR 023). CHO's own services stay the system of record (ADR 008);
/// the managed store holds a copy so the plan can use its cloud's native
/// FHIR tooling — streaming to a warehouse, de-identification, ML — on CHO
/// data without an extract.
///
/// Writes are idempotent upserts at CHO-assigned ids, so replaying an event
/// converges on the same resource. Resources travel as FHIR JSON; projection
/// (domain model to FHIR) happens in the owning service, never here.
/// </summary>
public interface IManagedFhirStore
{
    /// <summary>Provider key for logs and status: <c>gcp-healthcare</c>, <c>in-memory</c>.</summary>
    string ProviderName { get; }

    /// <summary>Create or replace <paramref name="resourceType"/>/<paramref name="id"/> (FHIR update-as-create).</summary>
    Task<ManagedFhirWriteResult> UpsertAsync(string resourceType, string id, string resourceJson, CancellationToken ct = default);

    /// <summary>Read a resource; null when it does not exist or was deleted.</summary>
    Task<string?> ReadAsync(string resourceType, string id, CancellationToken ct = default);

    /// <summary>Delete a resource. Deleting one that does not exist succeeds.</summary>
    Task DeleteAsync(string resourceType, string id, CancellationToken ct = default);

    /// <summary>Start a store-level NDJSON export to <see cref="ManagedFhirExportRequest.DestinationUri"/>.</summary>
    Task<ManagedFhirExportJob> StartExportAsync(ManagedFhirExportRequest request, CancellationToken ct = default);

    /// <summary>Status of an export started by <see cref="StartExportAsync"/>.</summary>
    Task<ManagedFhirExportJob> GetExportStatusAsync(string jobId, CancellationToken ct = default);
}

/// <summary>Outcome of a write. A rejected write carries the store's OperationOutcome when it returned one.</summary>
public sealed record ManagedFhirWriteResult(int StatusCode, string? VersionId, string? OperationOutcomeJson)
{
    public bool Succeeded => StatusCode is >= 200 and < 300;
}

public sealed record ManagedFhirExportRequest(
    string DestinationUri,
    IReadOnlyList<string>? ResourceTypes = null,
    DateTimeOffset? Since = null);

public enum ManagedFhirExportState
{
    Running,
    Succeeded,
    Failed
}

public sealed record ManagedFhirExportJob(
    string JobId,
    ManagedFhirExportState State,
    string? Error = null,
    long? ResourceCount = null);

/// <summary>The managed store answered with an error that is not a per-resource rejection.</summary>
public sealed class ManagedFhirStoreException : Exception
{
    public ManagedFhirStoreException(string message, int statusCode, string? operationOutcomeJson = null)
        : base(message)
    {
        StatusCode = statusCode;
        OperationOutcomeJson = operationOutcomeJson;
    }

    public int StatusCode { get; }

    public string? OperationOutcomeJson { get; }

    /// <summary>Throttling or a server-side failure: the same request may succeed later.</summary>
    public bool IsTransient => StatusCode is 408 or 429 or >= 500;
}
