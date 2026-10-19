using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CloudHealthOffice.Infrastructure.Logging;

namespace CloudHealthOffice.BankReconciliation;

/// <summary>
/// The audited path for a corrected bank file that reuses the bank's file
/// id: one person asks (with a reason), a second person approves, and only
/// then is the corrected file accepted. It is refused once any line of the
/// statement was applied, matched or settled, by any service, because
/// that cash has already been acted on.
/// </summary>
public sealed class BankStatementSupersessionService
{
    /// <summary>Statuses that mean a line was acted on: the statement can no longer be superseded.</summary>
    private static readonly ReconciliationStatus[] ActedOn =
        [ReconciliationStatus.Applied, ReconciliationStatus.Matched, ReconciliationStatus.Resolved];

    private readonly IBankReconciliationStore _store;
    private readonly BankReconciliationOptions _options;
    private readonly ILogger<BankStatementSupersessionService> _logger;
    private readonly TimeProvider _clock;

    public BankStatementSupersessionService(
        IBankReconciliationStore store,
        IOptions<BankReconciliationOptions> options,
        ILogger<BankStatementSupersessionService> logger,
        TimeProvider? clock = null)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Step 1: ask, with a reason. Freezes the statement until approved, withdrawn or rejected.</summary>
    /// <exception cref="KeyNotFoundException">No such statement.</exception>
    /// <exception cref="InvalidOperationException">It cannot be superseded (see the message).</exception>
    public async Task<BankStatementSupersession> RequestAsync(string tenantId, string statementId, string reason, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required.");
        // A run deciding lines right now may continue; the freeze stops it at its commit point.
        var file = await SupersedableAsync(tenantId, statementId, refuseActiveClaims: false, ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        var entry = new SupersessionHistoryEntry { Status = SupersessionStatus.Requested, By = actor, At = now, Reason = reason.Trim() };

        var existing = await _store.GetSupersessionAsync(tenantId, statementId, ct);
        if (existing is not null)
        {
            if (existing.Freezes)
                throw new InvalidOperationException($"Statement {statementId} already has a {existing.Status} supersession.");

            // A withdrawn or rejected request may be asked again; its history is kept.
            var previous = existing.Status;
            existing.Status = SupersessionStatus.Requested;
            existing.Reason = entry.Reason!;
            existing.RequestedBy = actor;
            existing.RequestedAt = now;
            existing.ApprovedBy = null;
            existing.ApprovedAt = null;
            existing.DismissalsComplete = false;
            existing.ClosedBy = null;
            existing.ClosedAt = null;
            existing.CloseReason = null;
            existing.History.Add(entry);
            if (!await _store.TryUpdateSupersessionAsync(existing, previous, ct))
                throw new InvalidOperationException($"The supersession of {statementId} changed meanwhile; reload it.");
            Log("requested", statementId, actor);
            return existing;
        }

        var supersession = new BankStatementSupersession
        {
            Id = BankStatementSupersession.IdFor(tenantId, statementId),
            TenantId = tenantId,
            StatementId = statementId,
            BankFileKeyBase = file.BankFileKeyBase ?? string.Empty,
            Status = SupersessionStatus.Requested,
            Reason = entry.Reason!,
            RequestedBy = actor,
            RequestedAt = now,
            History = [entry],
        };
        if (!await _store.TryCreateSupersessionAsync(supersession, ct))
            throw new InvalidOperationException($"Statement {statementId} already has a supersession request.");

        Log("requested", statementId, actor);
        return supersession;
    }

    /// <summary>
    /// Step 2, by a second person. Checks that no line was acted on <b>before</b>
    /// changing anything, saves the approval, then dismisses the open lines.
    /// The dismissal is idempotent: if it is interrupted, approving again
    /// finishes it. The corrected file is accepted only once it is complete.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No request for this statement.</exception>
    /// <exception cref="InvalidOperationException">Same person as the request, not open, or lines were acted on.</exception>
    public async Task<BankStatementSupersession> ApproveAsync(string tenantId, string statementId, string actor, CancellationToken ct = default)
    {
        var supersession = await _store.GetSupersessionAsync(tenantId, statementId, ct)
                           ?? throw new KeyNotFoundException($"No supersession request for statement {statementId}");

        if (supersession.Status == SupersessionStatus.Approved)
        {
            if (supersession.DismissalsComplete) return supersession;
            // An earlier approval stopped part-way: finish it.
            return await CompleteDismissalsAsync(supersession, ct);
        }
        if (supersession.Status != SupersessionStatus.Requested)
            throw new InvalidOperationException($"The supersession of {statementId} is {supersession.Status}; request it again first.");
        if (string.Equals(supersession.RequestedBy, actor, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A second person must approve: the requester cannot approve their own supersession.");

        // Validate everything first: nothing is dismissed unless the whole approval can go through.
        await SupersedableAsync(tenantId, statementId, refuseActiveClaims: true, ct);

        var now = _clock.GetUtcNow().UtcDateTime;
        supersession.Status = SupersessionStatus.Approved;
        supersession.ApprovedBy = actor;
        supersession.ApprovedAt = now;
        supersession.History.Add(new SupersessionHistoryEntry { Status = SupersessionStatus.Approved, By = actor, At = now });
        if (!await _store.TryUpdateSupersessionAsync(supersession, SupersessionStatus.Requested, ct))
            throw new InvalidOperationException($"The supersession of {statementId} changed meanwhile; reload it.");
        Log("approved", statementId, actor);

        return await CompleteDismissalsAsync(supersession, ct);
    }

    /// <summary>
    /// Takes a Requested supersession back: by the requester (Withdrawn) or by
    /// a second person (Rejected), with a reason. The statement is unfrozen.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No request for this statement.</exception>
    /// <exception cref="InvalidOperationException">It is not Requested.</exception>
    public async Task<BankStatementSupersession> WithdrawOrRejectAsync(string tenantId, string statementId, string reason, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required.");
        var supersession = await _store.GetSupersessionAsync(tenantId, statementId, ct)
                           ?? throw new KeyNotFoundException($"No supersession request for statement {statementId}");
        if (supersession.Status != SupersessionStatus.Requested)
            throw new InvalidOperationException($"The supersession of {statementId} is {supersession.Status}; only a Requested one can be withdrawn or rejected.");

        var status = string.Equals(supersession.RequestedBy, actor, StringComparison.OrdinalIgnoreCase)
            ? SupersessionStatus.Withdrawn
            : SupersessionStatus.Rejected;
        var now = _clock.GetUtcNow().UtcDateTime;
        supersession.Status = status;
        supersession.ClosedBy = actor;
        supersession.ClosedAt = now;
        supersession.CloseReason = reason.Trim();
        supersession.History.Add(new SupersessionHistoryEntry { Status = status, By = actor, At = now, Reason = reason.Trim() });
        if (!await _store.TryUpdateSupersessionAsync(supersession, SupersessionStatus.Requested, ct))
            throw new InvalidOperationException($"The supersession of {statementId} changed meanwhile; reload it.");

        Log(status == SupersessionStatus.Withdrawn ? "withdrawn" : "rejected", statementId, actor);
        return supersession;
    }

    public Task<BankStatementSupersession?> GetAsync(string tenantId, string statementId, CancellationToken ct = default) =>
        _store.GetSupersessionAsync(tenantId, statementId, ct);

    private async Task<BankStatementSupersession> CompleteDismissalsAsync(BankStatementSupersession supersession, CancellationToken ct)
    {
        var now = supersession.ApprovedAt ?? _clock.GetUtcNow().UtcDateTime;
        foreach (var line in await _store.ListItemsByStatementAsync(supersession.TenantId, supersession.StatementId, ct))
            await DismissAsync(supersession, line.Id, now, ct);

        supersession.DismissalsComplete = true;
        if (!await _store.TryUpdateSupersessionAsync(supersession, SupersessionStatus.Approved, ct))
            throw new InvalidOperationException($"The supersession of {supersession.StatementId} changed meanwhile; approve again to finish.");
        return supersession;
    }

    /// <summary>Dismisses one line if it is open or an abandoned claim; a no-op for lines already dismissed or ignored.</summary>
    private async Task DismissAsync(BankStatementSupersession supersession, string itemId, DateTime now, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var item = await _store.GetItemAsync(supersession.TenantId, itemId, ct);
            if (item is null) return;
            var abandoned = item.Status == ReconciliationStatus.Pending && !IsActiveClaim(item);
            if (!item.IsOpen && !abandoned) return;

            item.Status = ReconciliationStatus.Dismissed;
            item.ResolvedBy = supersession.ApprovedBy;
            item.ResolvedAt = now;
            item.ResolutionNote = $"Statement superseded by a corrected bank file: {supersession.Reason}";
            if (await _store.TryUpdateItemAsync(item, item.Version, ct)) return;
        }
        throw new InvalidOperationException($"Line {itemId} kept changing while the supersession was being completed; approve again to finish.");
    }

    private bool IsActiveClaim(ReconciliationItem item) =>
        item.CreatedAt > _clock.GetUtcNow().UtcDateTime.AddMinutes(-Math.Max(1, _options.PendingLeaseMinutes));

    private void Log(string what, string statementId, string actor) =>
        _logger.LogWarning("Supersession of bank statement {StatementId} {What} by {Actor}",
            LogSanitizer.Sanitize(statementId), what, LogSanitizer.Sanitize(actor));

    private async Task<BankStatementFile> SupersedableAsync(string tenantId, string statementId, bool refuseActiveClaims, CancellationToken ct)
    {
        var file = await _store.GetFileAsync(tenantId, statementId, ct)
                   ?? throw new KeyNotFoundException($"Bank statement {statementId} not found");
        if (file.Status != BankStatementStatus.Accepted || file.BankFileKeyBase is null)
            throw new InvalidOperationException("Only an accepted statement can be superseded; a rejected file does not hold its bank file id.");

        var latest = (await _store.ListAcceptedByBankFileKeyAsync(tenantId, file.BankFileKeyBase, ct)).LastOrDefault();
        if (latest?.Id != file.Id)
            throw new InvalidOperationException($"Statement {statementId} was already replaced by {latest?.Id}.");

        var items = await _store.ListItemsByStatementAsync(tenantId, statementId, ct);
        var actedOn = items.Where(i => ActedOn.Contains(i.Status)).ToList();
        if (actedOn.Count > 0)
            throw new InvalidOperationException(
                $"{actedOn.Count} line(s) of statement {statementId} were already applied, matched, settled or resolved " +
                $"({string.Join(", ", actedOn.Take(5).Select(i => i.Id))}); it cannot be superseded. Ask the bank for a corrected file with a new file id and reverse what was posted.");
        var deciding = items.Where(i => i.Status == ReconciliationStatus.Pending && IsActiveClaim(i)).ToList();
        if (refuseActiveClaims && deciding.Count > 0)
            throw new InvalidOperationException(
                $"{deciding.Count} line(s) of statement {statementId} are being decided by a reconciliation run right now; try again in a few minutes.");
        return file;
    }
}
