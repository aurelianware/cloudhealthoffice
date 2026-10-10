using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private static readonly ReconciliationStatus[] ActedOn =
        [ReconciliationStatus.Applied, ReconciliationStatus.Matched, ReconciliationStatus.Resolved, ReconciliationStatus.Pending];

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

    /// <exception cref="KeyNotFoundException">No such statement.</exception>
    /// <exception cref="InvalidOperationException">It cannot be superseded (see the message).</exception>
    public async Task<BankStatementSupersession> RequestAsync(string tenantId, string statementId, string reason, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required.");
        var file = await SupersedableAsync(tenantId, statementId, ct);

        var supersession = new BankStatementSupersession
        {
            Id = BankStatementSupersession.IdFor(tenantId, statementId),
            TenantId = tenantId,
            StatementId = statementId,
            BankFileKeyBase = file.BankFileKeyBase ?? string.Empty,
            Status = SupersessionStatus.Requested,
            Reason = reason.Trim(),
            RequestedBy = actor,
            RequestedAt = _clock.GetUtcNow().UtcDateTime,
        };
        if (!await _store.TryCreateSupersessionAsync(supersession, ct))
            throw new InvalidOperationException($"Statement {statementId} already has a supersession request.");

        _logger.LogWarning("Supersession of bank statement {StatementId} requested by {Actor}", LogText.Clean(statementId), LogText.Clean(actor));
        return supersession;
    }

    /// <exception cref="KeyNotFoundException">No request for this statement.</exception>
    /// <exception cref="InvalidOperationException">Same person as the request, already approved, or lines were acted on meanwhile.</exception>
    public async Task<BankStatementSupersession> ApproveAsync(string tenantId, string statementId, string actor, CancellationToken ct = default)
    {
        var supersession = await _store.GetSupersessionAsync(tenantId, statementId, ct)
                           ?? throw new KeyNotFoundException($"No supersession request for statement {statementId}");
        if (supersession.Status != SupersessionStatus.Requested)
            throw new InvalidOperationException($"The supersession of {statementId} is already {supersession.Status}.");
        if (string.Equals(supersession.RequestedBy, actor, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A second person must approve: the requester cannot approve their own supersession.");

        await SupersedableAsync(tenantId, statementId, ct);

        supersession.Status = SupersessionStatus.Approved;
        supersession.ApprovedBy = actor;
        supersession.ApprovedAt = _clock.GetUtcNow().UtcDateTime;
        if (!await _store.TryUpdateSupersessionAsync(supersession, SupersessionStatus.Requested, ct))
            throw new InvalidOperationException($"The supersession of {statementId} changed meanwhile; reload it.");

        // The statement's open lines are replaced by the corrected file's.
        foreach (var item in (await _store.ListItemsByStatementAsync(tenantId, statementId, ct)).Where(i => i.IsOpen))
        {
            item.Status = ReconciliationStatus.Dismissed;
            item.ResolvedBy = actor;
            item.ResolvedAt = supersession.ApprovedAt;
            item.ResolutionNote = $"Statement superseded by a corrected bank file: {supersession.Reason}";
            await _store.TryUpdateItemAsync(item, item.Version, ct);
        }

        _logger.LogWarning("Supersession of bank statement {StatementId} approved by {Actor}", LogText.Clean(statementId), LogText.Clean(actor));
        return supersession;
    }

    public Task<BankStatementSupersession?> GetAsync(string tenantId, string statementId, CancellationToken ct = default) =>
        _store.GetSupersessionAsync(tenantId, statementId, ct);

    private async Task<BankStatementFile> SupersedableAsync(string tenantId, string statementId, CancellationToken ct)
    {
        var file = await _store.GetFileAsync(tenantId, statementId, ct)
                   ?? throw new KeyNotFoundException($"Bank statement {statementId} not found");
        if (file.Status != BankStatementStatus.Accepted || file.BankFileKeyBase is null)
            throw new InvalidOperationException("Only an accepted statement can be superseded; a rejected file does not hold its bank file id.");

        var latest = (await _store.ListAcceptedByBankFileKeyAsync(tenantId, file.BankFileKeyBase, ct)).LastOrDefault();
        if (latest?.Id != file.Id)
            throw new InvalidOperationException($"Statement {statementId} was already replaced by {latest?.Id}.");

        var actedOn = (await _store.ListItemsByStatementAsync(tenantId, statementId, ct)).Where(i => ActedOn.Contains(i.Status)).ToList();
        if (actedOn.Count > 0)
            throw new InvalidOperationException(
                $"{actedOn.Count} line(s) of statement {statementId} were already applied, matched, settled or resolved " +
                $"({string.Join(", ", actedOn.Take(5).Select(i => i.Id))}); it cannot be superseded. Ask the bank for a corrected file with a new file id and reverse what was posted.");
        return file;
    }
}
