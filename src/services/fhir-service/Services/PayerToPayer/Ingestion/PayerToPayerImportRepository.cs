using System.Collections.Concurrent;
using FhirService.Models.PayerToPayer;

namespace FhirService.Services.PayerToPayer.Ingestion;

/// <summary>
/// Durable store for member data imported from another payer.
///
/// It is deliberately SEPARATE from Cloud Health Office's authoritative member,
/// enrollment, claim, and provider stores. Source ownership is structural, not a
/// convention: an imported row physically cannot be read as a CHO-owned record,
/// so a prior payer's Patient or Coverage can never become CHO's own — the
/// question "did CHO originate this?" is answered by which store it lives in.
///
/// Writes are staged and then committed:
///   1. <see cref="StageAsync"/> writes each resource as THAT EXCHANGE's version
///      of it — rows are identified by (tenant, exchange, import key), so
///      staging never touches a version an earlier exchange already committed;
///   2. <see cref="CommitAsync"/> flips that exchange's single ledger entry to
///      Completed.
///
/// Reads return, for each import key, the version from the most recently
/// COMMITTED exchange. That gives both halves of the atomicity guarantee without
/// needing a multi-document transaction from the underlying store:
///   * an ingestion that dies part-way adds nothing visible, and
///   * it takes nothing away either — the member keeps the history a previous
///     exchange committed, and an updated resource supersedes the older version
///     only once the exchange carrying it commits.
/// Versioning per exchange also keeps "which exchange delivered what" answerable.
/// </summary>
public interface IPayerToPayerImportRepository
{
    /// <summary>Reads the import ledger entry for an exchange within a tenant.</summary>
    Task<PayerToPayerImportLedgerEntry?> GetLedgerAsync(
        string tenantId, string exchangeId, CancellationToken ct = default);

    /// <summary>
    /// Opens (or re-opens, on retry) the ledger entry for an exchange in the
    /// Staging state, clearing any previous failure.
    /// </summary>
    Task<PayerToPayerImportLedgerEntry> OpenLedgerAsync(
        PayerToPayerImportLedgerEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Writes this exchange's version of each resource. Returns how many differ
    /// from the version currently committed for that import key versus how many
    /// are byte-identical to it (a replay).
    /// </summary>
    Task<StageOutcome> StageAsync(
        IReadOnlyList<ImportedFhirResource> resources, CancellationToken ct = default);

    /// <summary>Marks the exchange's ledger entry Completed — the single write that publishes the import.</summary>
    Task CommitAsync(PayerToPayerImportLedgerEntry entry, CancellationToken ct = default);

    /// <summary>Records a failed ingestion attempt; nothing staged under it becomes visible.</summary>
    Task FailAsync(
        PayerToPayerImportLedgerEntry entry, PayerToPayerIngestionFailure failure, CancellationToken ct = default);

    /// <summary>
    /// The member's imported history within a tenant: one row per import key,
    /// taken from the most recently committed exchange. A version staged by an
    /// exchange that never committed is never returned, and never displaces a
    /// committed one.
    /// </summary>
    Task<IReadOnlyList<ImportedFhirResource>> GetImportedResourcesAsync(
        string tenantId, string memberId, CancellationToken ct = default);
}

/// <summary>How many staged resources were new versus unchanged replays.</summary>
public readonly record struct StageOutcome(int Written, int UnchangedDuplicates);

/// <summary>
/// In-process implementation, used when no durable store is configured — the same
/// Demo-mode posture the rest of fhir-service takes (see <c>DtrService</c>, which
/// falls back to in-memory when <c>MongoDb:ConnectionString</c> is absent).
///
/// LIMITATION, stated plainly: this does not survive a restart and is not shared
/// across instances. It exists so the ingestion workflow is exercisable and
/// testable end to end; a deployment that needs durability configures MongoDB and
/// gets <see cref="MongoPayerToPayerImportRepository"/> instead, with no change
/// to the workflow above it.
/// </summary>
public sealed class InMemoryPayerToPayerImportRepository : IPayerToPayerImportRepository
{
    private readonly ConcurrentDictionary<string, PayerToPayerImportLedgerEntry> _ledger = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ImportedFhirResource> _resources = new(StringComparer.Ordinal);

    public Task<PayerToPayerImportLedgerEntry?> GetLedgerAsync(
        string tenantId, string exchangeId, CancellationToken ct = default)
        => Task.FromResult(_ledger.TryGetValue(LedgerKey(tenantId, exchangeId), out var entry) ? entry : null);

    public Task<PayerToPayerImportLedgerEntry> OpenLedgerAsync(
        PayerToPayerImportLedgerEntry entry, CancellationToken ct = default)
    {
        entry.Status = PayerToPayerIngestionStatus.Staging;
        entry.Failure = PayerToPayerIngestionFailure.None;
        entry.CompletedAtUtc = null;
        _ledger[LedgerKey(entry.TenantId, entry.ExchangeId)] = entry;
        return Task.FromResult(entry);
    }

    public Task<StageOutcome> StageAsync(
        IReadOnlyList<ImportedFhirResource> resources, CancellationToken ct = default)
    {
        var written = 0;
        var unchanged = 0;

        foreach (var resource in resources)
        {
            // Compare against what is COMMITTED for this import key, not against
            // whatever another in-flight exchange happens to have staged.
            var committed = LatestCommitted(resource.TenantId, resource.MemberId, resource.ImportKey);
            if (committed is not null
                && string.Equals(committed.ContentHash, resource.ContentHash, StringComparison.Ordinal))
                unchanged++;
            else
                written++;

            // This exchange's own version. Re-staging the same exchange (a retry)
            // overwrites its own row and nothing else.
            _resources[ResourceKey(resource.TenantId, resource.ExchangeId, resource.ImportKey)] = resource;
        }

        return Task.FromResult(new StageOutcome(written, unchanged));
    }

    public Task CommitAsync(PayerToPayerImportLedgerEntry entry, CancellationToken ct = default)
    {
        entry.Status = PayerToPayerIngestionStatus.Completed;
        entry.Failure = PayerToPayerIngestionFailure.None;
        entry.CompletedAtUtc = DateTime.UtcNow;
        _ledger[LedgerKey(entry.TenantId, entry.ExchangeId)] = entry;
        return Task.CompletedTask;
    }

    public Task FailAsync(
        PayerToPayerImportLedgerEntry entry, PayerToPayerIngestionFailure failure, CancellationToken ct = default)
    {
        entry.Status = PayerToPayerIngestionStatus.Failed;
        entry.Failure = failure;
        _ledger[LedgerKey(entry.TenantId, entry.ExchangeId)] = entry;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ImportedFhirResource>> GetImportedResourcesAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        var resources = CommittedVersions(tenantId, memberId)
            .OrderBy(r => r.ResourceType, StringComparer.Ordinal)
            .ThenBy(r => r.SourceResourceId, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<ImportedFhirResource>>(resources);
    }

    /// <summary>
    /// One row per import key for the member: the version from the most recently
    /// committed exchange. Uncommitted versions are invisible and never displace
    /// a committed one.
    /// </summary>
    private IEnumerable<ImportedFhirResource> CommittedVersions(string tenantId, string memberId)
    {
        var committedAt = _ledger.Values
            .Where(e => string.Equals(e.TenantId, tenantId, StringComparison.Ordinal)
                     && e.Status == PayerToPayerIngestionStatus.Completed)
            .ToDictionary(e => e.ExchangeId, e => e.CompletedAtUtc ?? e.StartedAtUtc, StringComparer.Ordinal);

        return _resources.Values
            .Where(r => string.Equals(r.TenantId, tenantId, StringComparison.Ordinal)
                     && string.Equals(r.MemberId, memberId, StringComparison.Ordinal)
                     && committedAt.ContainsKey(r.ExchangeId))
            .GroupBy(r => r.ImportKey, StringComparer.Ordinal)
            // Deterministic winner: latest commit, then latest ingest, then id.
            .Select(g => g
                .OrderByDescending(r => committedAt[r.ExchangeId])
                .ThenByDescending(r => r.IngestedAtUtc)
                .ThenBy(r => r.ExchangeId, StringComparer.Ordinal)
                .First());
    }

    private ImportedFhirResource? LatestCommitted(string tenantId, string memberId, string importKey)
        => CommittedVersions(tenantId, memberId)
            .FirstOrDefault(r => string.Equals(r.ImportKey, importKey, StringComparison.Ordinal));

    // Every key is tenant-prefixed, so one tenant's import can never be read or
    // overwritten through another tenant's context.
    private static string LedgerKey(string tenantId, string exchangeId) => $"{tenantId}|{exchangeId}";

    // A row is ONE EXCHANGE's version of one imported resource.
    private static string ResourceKey(string tenantId, string exchangeId, string importKey)
        => $"{tenantId}|{exchangeId}|{importKey}";
}
