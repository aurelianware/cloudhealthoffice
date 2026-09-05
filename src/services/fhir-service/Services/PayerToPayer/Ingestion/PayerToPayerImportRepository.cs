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
///   1. <see cref="StageAsync"/> upserts resources by their deterministic import
///      key (so a replay updates in place instead of duplicating history), all
///      tied to the exchange;
///   2. <see cref="CommitAsync"/> flips that exchange's single ledger entry to
///      Completed.
/// Reads only ever return resources whose ledger entry is committed, so an
/// ingestion that dies part-way leaves the member's imported history untouched
/// rather than half-written. That is the atomicity guarantee available without
/// requiring a multi-document transaction from the underlying store.
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
    /// Upserts staged resources by import key. Returns how many were new versus
    /// already held with identical content (a replay).
    /// </summary>
    Task<StageOutcome> StageAsync(
        IReadOnlyList<ImportedFhirResource> resources, CancellationToken ct = default);

    /// <summary>Marks the exchange's ledger entry Completed — the single write that publishes the import.</summary>
    Task CommitAsync(PayerToPayerImportLedgerEntry entry, CancellationToken ct = default);

    /// <summary>Records a failed ingestion attempt; nothing staged under it becomes visible.</summary>
    Task FailAsync(
        PayerToPayerImportLedgerEntry entry, PayerToPayerIngestionFailure failure, CancellationToken ct = default);

    /// <summary>
    /// The member's committed imported history within a tenant. Resources staged
    /// by an exchange that never committed are not returned.
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
            var key = ResourceKey(resource.TenantId, resource.ImportKey);
            if (_resources.TryGetValue(key, out var existing)
                && string.Equals(existing.ContentHash, resource.ContentHash, StringComparison.Ordinal))
            {
                // Same resource, same content, from the same payer: a replay. The
                // row is still re-pointed at the latest exchange so the newest
                // receipt is traceable, but it is not counted as newly imported.
                unchanged++;
            }
            else
            {
                written++;
            }

            _resources[key] = resource;
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
        // Committed exchanges only: a staged-but-uncommitted (or failed) import is
        // invisible, which is what makes a partial ingestion harmless.
        var committed = _ledger.Values
            .Where(e => string.Equals(e.TenantId, tenantId, StringComparison.Ordinal)
                     && e.Status == PayerToPayerIngestionStatus.Completed)
            .Select(e => e.ExchangeId)
            .ToHashSet(StringComparer.Ordinal);

        var resources = _resources.Values
            .Where(r => string.Equals(r.TenantId, tenantId, StringComparison.Ordinal)
                     && string.Equals(r.MemberId, memberId, StringComparison.Ordinal)
                     && committed.Contains(r.ExchangeId))
            .OrderBy(r => r.ResourceType, StringComparer.Ordinal)
            .ThenBy(r => r.SourceResourceId, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<ImportedFhirResource>>(resources);
    }

    // Every key is tenant-prefixed, so one tenant's import can never be read or
    // overwritten through another tenant's context.
    private static string LedgerKey(string tenantId, string exchangeId) => $"{tenantId}|{exchangeId}";
    private static string ResourceKey(string tenantId, string importKey) => $"{tenantId}|{importKey}";
}
