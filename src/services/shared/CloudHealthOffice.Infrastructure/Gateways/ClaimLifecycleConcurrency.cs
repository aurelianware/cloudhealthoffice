using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// A claim-lifecycle write (transmission, inbound attachment receipt or
/// remittance receipt) based on a stale read: another write changed the
/// document's revision since it was read. Nothing was written; 409 Conflict at
/// an API boundary. Background callers re-read and re-apply
/// (<see cref="ClaimLifecycleWrites.SaveWithRetryAsync{T}"/>).
/// </summary>
public sealed class ClaimLifecycleConcurrencyException : ConcurrencyConflictException
{
    public ClaimLifecycleConcurrencyException(
        string documentKind, string id, long expectedRevision, Exception? inner = null)
        : base($"{documentKind} {id} was changed by another write (expected revision {expectedRevision}); re-read it and retry.", inner)
    {
        DocumentKind = documentKind;
        Id = id;
        ExpectedRevision = expectedRevision;
    }

    public string DocumentKind { get; }
    public string Id { get; }
    public long ExpectedRevision { get; }
}

/// <summary>
/// Re-read and re-apply for claim-lifecycle writers that are background
/// consumers (277CA, 835, outbox dispatch) or that record an outcome that
/// already happened (a gateway submit, a remittance post). The caller applies
/// its change to the record it read and passes the same change as
/// <c>reapply</c>; on a conflict the change is applied again to a fresh copy,
/// so the other write is kept. <c>reapply</c> returns false when the fresh copy
/// needs nothing (already there, or a later state wins).
/// </summary>
internal static class ClaimLifecycleWrites
{
    public const int MaxAttempts = 4;

    /// <summary>
    /// How long a <c>Transmitting</c> transmission counts as being sent by
    /// another request. Well above one gateway send with its retries (Stedi:
    /// 30 s timeout, 3 attempts); after it the sender is taken to have crashed
    /// and the claim may be sent again.
    /// </summary>
    public static readonly TimeSpan InFlightLease = TimeSpan.FromMinutes(10);

    /// <summary>
    /// True while another request is sending this transmission. A record
    /// written before <see cref="ClaimTransmissionRecord.TransmittingSinceUtc"/>
    /// existed uses its submit time.
    /// </summary>
    public static bool IsInFlight(ClaimTransmissionRecord record, DateTimeOffset now) =>
        record.Status == GatewayClaimTransmissionStatus.Transmitting
        && now - (record.TransmittingSinceUtc ?? record.SubmittedAtUtc) < InFlightLease;

    /// <summary>
    /// Marks <paramref name="record"/> as being sent now and saves it, so a
    /// concurrent submit of the same key sees it in flight. Returns the current
    /// copy instead when another request claimed the send first (a revision
    /// conflict); the caller then must not send.
    /// </summary>
    public static async Task<ClaimTransmissionRecord?> TryClaimSendAsync(
        IClaimTransmissionStore store, ClaimTransmissionRecord record, DateTimeOffset now, CancellationToken ct)
    {
        record.Status = GatewayClaimTransmissionStatus.Transmitting;
        record.TransmittingSinceUtc = now;
        try
        {
            await store.SaveAsync(record, ct).ConfigureAwait(false);
            return null;
        }
        catch (ClaimLifecycleConcurrencyException)
        {
            return await store.GetByTenantAndIdAsync(record.TenantId, record.TransmissionId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Transmission {record.TransmissionId} vanished during a submit.");
        }
    }

    /// <returns>The record as last written, or the fresh copy when nothing was left to write.</returns>
    public static async Task<T> SaveWithRetryAsync<T>(
        T record,
        Func<T, Task> save,
        Func<Task<T?>> reread,
        Func<T, bool> reapply,
        ILogger? logger = null)
        where T : class
    {
        var current = record;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await save(current).ConfigureAwait(false);
                return current;
            }
            catch (ClaimLifecycleConcurrencyException ex) when (attempt < MaxAttempts)
            {
                logger?.LogInformation(
                    "Concurrent {DocumentKind} write; re-applying on a fresh copy (attempt {Attempt})",
                    ex.DocumentKind, attempt);
                var fresh = await reread().ConfigureAwait(false);
                if (fresh is null)
                {
                    throw;
                }

                if (!reapply(fresh))
                {
                    return fresh;
                }

                current = fresh;
            }
        }
    }

    /// <summary>
    /// Saves a gateway submit outcome (after the 837 was sent, so it must not
    /// be dropped); on a conflict re-applies it with <see cref="ReapplySubmitOutcome"/>.
    /// </summary>
    public static Task<ClaimTransmissionRecord> SaveSubmitOutcomeAsync(
        IClaimTransmissionStore store,
        ClaimTransmissionRecord outcome,
        ILogger? logger,
        CancellationToken ct) =>
        SaveWithRetryAsync(
            outcome,
            t => store.SaveAsync(t, ct),
            () => store.GetByTenantAndIdAsync(outcome.TenantId, outcome.TransmissionId, ct),
            fresh => ReapplySubmitOutcome(outcome, fresh),
            logger);

    /// <summary>
    /// The outcome of a gateway submit, applied to a fresh copy of the
    /// transmission. A submit outcome never replaces a completed submit or a
    /// 277CA result that landed in between (status does not move backwards);
    /// identifiers the fresh copy lacks are still filled in.
    /// </summary>
    public static bool ReapplySubmitOutcome(ClaimTransmissionRecord outcome, ClaimTransmissionRecord fresh)
    {
        if (GatewayClaimTransmissionStatuses.PreventsDuplicateSubmit(fresh.Status))
        {
            var changed = false;
            if (fresh.SubmissionId is null && outcome.SubmissionId is not null)
            {
                fresh.SubmissionId = outcome.SubmissionId;
                changed = true;
            }

            if (fresh.ExternalTransactionId is null && outcome.ExternalTransactionId is not null)
            {
                fresh.ExternalTransactionId = outcome.ExternalTransactionId;
                changed = true;
            }

            if (fresh.CompletedAtUtc is null && outcome.CompletedAtUtc is not null)
            {
                fresh.CompletedAtUtc = outcome.CompletedAtUtc;
                changed = true;
            }

            return changed;
        }

        fresh.Status = outcome.Status;
        fresh.SubmissionId = outcome.SubmissionId;
        fresh.ExternalTransactionId = outcome.ExternalTransactionId;
        fresh.CompletedAtUtc = outcome.CompletedAtUtc;
        fresh.RetryCount = outcome.RetryCount;
        fresh.ErrorCategory = outcome.ErrorCategory;
        fresh.ErrorMessage = outcome.ErrorMessage;
        return true;
    }
}
