using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CloudHealthOffice.Infrastructure.Logging;

namespace CloudHealthOffice.BankReconciliation;

/// <summary>What a service's matcher decided for one bank transaction.</summary>
public sealed record MatchDecision(
    string Kind,
    ReconciliationStatus Status,
    string Reason,
    string? MatchedEntityType = null,
    IReadOnlyList<string>? MatchedEntityIds = null,
    IReadOnlyList<string>? Candidates = null)
{
    public static MatchDecision Ignore(string reason) => new("Other", ReconciliationStatus.Ignored, reason);
}

/// <summary>
/// A service's matching rules. <see cref="DecideAsync"/> may run more than once
/// for the same transaction after a crash, so anything it posts must be
/// idempotent (premium cash application is: its batch id is derived from the
/// transaction id).
/// </summary>
public interface IBankTransactionMatcher
{
    Task<MatchDecision> DecideAsync(BankTransaction transaction, CancellationToken ct = default);
}

public sealed class BankReconciliationSummary
{
    public int Matched { get; set; }
    public int Applied { get; set; }
    public int Suspense { get; set; }
    public int Returns { get; set; }
    public int Ignored { get; set; }

    /// <summary>Decided by an earlier run of the same file.</summary>
    public int AlreadyDecided { get; set; }

    /// <summary>Claimed by a run still in progress.</summary>
    public int InProgress { get; set; }

    /// <summary>Not decided: the statement is frozen by a supersession.</summary>
    public int Frozen { get; set; }

    public List<ReconciliationItem> Items { get; } = [];
}

/// <summary>
/// Decides each transaction of a statement once per service: claims an item
/// (Pending), asks the matcher, stores the decision, then publishes the event.
/// Re-running a statement skips decided lines and retries unpublished events.
/// </summary>
public sealed class BankReconciliationRunner
{
    private readonly IBankReconciliationStore _store;
    private readonly IBankReconciliationEventSink _events;
    private readonly BankReconciliationOptions _options;
    private readonly ILogger<BankReconciliationRunner> _logger;
    private readonly TimeProvider _clock;

    public BankReconciliationRunner(
        IBankReconciliationStore store,
        IBankReconciliationEventSink events,
        IOptions<BankReconciliationOptions> options,
        ILogger<BankReconciliationRunner> logger,
        TimeProvider? clock = null)
    {
        _store = store;
        _events = events;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<BankReconciliationSummary> RunAsync(IReadOnlyList<BankTransaction> transactions, IBankTransactionMatcher matcher, CancellationToken ct = default)
    {
        var summary = new BankReconciliationSummary();
        foreach (var tx in transactions.OrderBy(t => t.LineNumber))
        {
            var item = await DecideOneAsync(tx, matcher, summary, ct);
            if (item is null) continue;
            summary.Items.Add(item);
        }
        return summary;
    }

    private async Task<ReconciliationItem?> DecideOneAsync(BankTransaction tx, IBankTransactionMatcher matcher, BankReconciliationSummary summary, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var id = ReconciliationItem.IdFor(_options.ServiceName, tx.Id);
        var claim = NewItem(tx, id, now);

        // A statement under supersession is not decided (checked again by the
        // matchers right before they post anything).
        if ((await _store.GetSupersessionAsync(tx.TenantId, tx.StatementId, ct))?.Freezes == true)
        {
            summary.Frozen++;
            return null;
        }

        if (!await _store.TryCreateItemAsync(claim, ct))
        {
            var existing = await _store.GetItemAsync(tx.TenantId, id, ct);
            if (existing is null) return null;
            if (existing.Status != ReconciliationStatus.Pending)
            {
                summary.AlreadyDecided++;
                if (existing.EventPublishedAt is null)
                    await PublishAsync(existing, BankReconciliationEvent.Decided, ct);
                return existing;
            }
            if (existing.CreatedAt > now.AddMinutes(-Math.Max(1, _options.PendingLeaseMinutes)))
            {
                summary.InProgress++;
                return existing;
            }
            // An earlier run claimed it and stopped: take the claim over.
            existing.CreatedAt = now;
            if (!await _store.TryUpdateItemAsync(existing, existing.Version, ct))
            {
                summary.InProgress++;
                return null;
            }
            claim = existing;
        }

        MatchDecision decision;
        if (_options.AccountSuffixes.Count > 0 && !_options.AccountSuffixes.Contains(tx.AccountSuffix, StringComparer.OrdinalIgnoreCase))
            decision = MatchDecision.Ignore($"Account {tx.MaskedAccount} is not reconciled by this service.");
        else if (tx.GroupStatus == "4")
            decision = MatchDecision.Ignore("Test-only group (02 group status 4).");
        else if (tx.GroupStatus is "2" or "3")
            decision = new MatchDecision("Other", ReconciliationStatus.Suspense,
                $"The bank sent this line as a {(tx.GroupStatus == "2" ? "deletion" : "correction")} (02 group status {tx.GroupStatus}); a person must review it.");
        else if (_options.InterimReports == InterimReportHandling.Skip && tx.AsOfDateModifier is "1" or "3")
            decision = MatchDecision.Ignore($"Interim report (02 as-of-date modifier {tx.AsOfDateModifier}); only final reports are reconciled.");
        else if (claim.DedupeKey is not null
                 && await _store.FindDecidedByDedupeKeyAsync(tx.TenantId, _options.ServiceName, claim.DedupeKey, tx.Id, ct) is { } earlier)
            decision = IsInterimFinalPair(earlier, claim)
                // The intraday report and the final report of the same day carry the same line.
                ? MatchDecision.Ignore($"The same bank transaction (account, type, bank reference, amount, date) was already reconciled as {earlier.Id}, from the {(IsInterim(earlier.AsOfDateModifier) ? "interim" : "final")} report.")
                // Same reference, amount and date but not an interim/final pair: may be
                // genuine repeated cash. A person decides; never dropped.
                : new MatchDecision("Other", ReconciliationStatus.Suspense,
                    $"PossibleDuplicate: another line with the same account, type, bank reference, amount and date was already reconciled ({earlier.Id}); confirm whether this is the same money.",
                    Candidates: [$"ReconciliationItem:{earlier.Id}"]);
        else
        {
            try
            {
                decision = await matcher.DecideAsync(tx, ct);
            }
            catch (StatementFrozenException)
            {
                // A supersession was requested while this run was deciding: give
                // the claim up (lease expired) so nothing holds the statement.
                claim.CreatedAt = DateTime.MinValue;
                claim.Reason = "Not decided: the statement was frozen by a supersession request";
                await _store.TryUpdateItemAsync(claim, claim.Version, ct);
                summary.Frozen++;
                return null;
            }
        }

        claim.Kind = decision.Kind;
        claim.Status = decision.Status;
        claim.Reason = decision.Reason;
        claim.MatchedEntityType = decision.MatchedEntityType;
        claim.MatchedEntityIds = [.. decision.MatchedEntityIds ?? []];
        claim.Candidates = [.. decision.Candidates ?? []];
        if (!await _store.TryUpdateItemAsync(claim, claim.Version, ct))
        {
            _logger.LogWarning("Reconciliation item {ItemId} changed while it was being decided", LogSanitizer.Sanitize(id));
            return null;
        }

        switch (claim.Status)
        {
            case ReconciliationStatus.Matched: summary.Matched++; break;
            case ReconciliationStatus.Applied: summary.Applied++; break;
            case ReconciliationStatus.Suspense: summary.Suspense++; break;
            case ReconciliationStatus.Return: summary.Returns++; break;
            default: summary.Ignored++; break;
        }

        if (claim.IsOpen)
            _logger.LogWarning("Bank line {ItemId} ({Kind}, type {TypeCode}, {Amount} {Currency}) is waiting for review: {Status}",
                LogSanitizer.Sanitize(claim.Id), LogSanitizer.Sanitize(claim.Kind), LogSanitizer.Sanitize(claim.TypeCode), claim.Amount, LogSanitizer.Sanitize(claim.Currency), claim.Status);

        await PublishAsync(claim, BankReconciliationEvent.Decided, ct);
        return claim;
    }

    private static bool IsInterim(string? modifier) => modifier is "1" or "3";

    private static bool IsFinal(string? modifier) => modifier is "2" or "4" or null or "";

    /// <summary>One line from an interim report and one from a final report, in different statements.</summary>
    private static bool IsInterimFinalPair(ReconciliationItem earlier, ReconciliationItem current) =>
        earlier.StatementId != current.StatementId
        && ((IsInterim(earlier.AsOfDateModifier) && IsFinal(current.AsOfDateModifier))
            || (IsFinal(earlier.AsOfDateModifier) && IsInterim(current.AsOfDateModifier)));

    /// <summary>
    /// Refuses work on a statement that is being, or has been, replaced by a
    /// corrected file: its lines will be decided from the corrected copy.
    /// </summary>
    /// <exception cref="InvalidOperationException">The statement has a supersession.</exception>
    public async Task EnsureNotSupersededAsync(string tenantId, string statementId, CancellationToken ct = default)
    {
        if (await _store.GetSupersessionAsync(tenantId, statementId, ct) is { Freezes: true } s)
            throw new StatementFrozenException(
                $"Statement {statementId} is {(s.Status == SupersessionStatus.Approved ? "superseded" : "being superseded")} by a corrected bank file; work its lines from the corrected statement.");
    }

    /// <summary>
    /// Closes an open (Suspense or Return) item: <see cref="ReconciliationStatus.Resolved"/>
    /// with what it was matched to, or <see cref="ReconciliationStatus.Dismissed"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such item for this tenant and service.</exception>
    /// <exception cref="InvalidOperationException">The item is not open, or changed meanwhile.</exception>
    public async Task<ReconciliationItem> ResolveAsync(
        string tenantId, string itemId, bool dismiss, string note, string actor,
        string? matchedEntityType = null, IReadOnlyList<string>? matchedEntityIds = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(note)) throw new ArgumentException("A note saying why is required.");
        var item = await _store.GetItemAsync(tenantId, itemId, ct);
        if (item is null || item.Service != _options.ServiceName) throw new KeyNotFoundException($"Reconciliation item {itemId} not found");
        if (!item.IsOpen) throw new InvalidOperationException($"Reconciliation item {itemId} is {item.Status}, not open");
        await EnsureNotSupersededAsync(tenantId, item.StatementId, ct);

        item.Status = dismiss ? ReconciliationStatus.Dismissed : ReconciliationStatus.Resolved;
        item.ResolutionNote = note.Trim();
        item.ResolvedBy = actor;
        item.ResolvedAt = _clock.GetUtcNow().UtcDateTime;
        if (matchedEntityType is not null)
        {
            item.MatchedEntityType = matchedEntityType;
            item.MatchedEntityIds = [.. matchedEntityIds ?? []];
        }
        if (!await _store.TryUpdateItemAsync(item, item.Version, ct))
            throw new InvalidOperationException($"Reconciliation item {itemId} changed meanwhile; reload it");

        await PublishAsync(item, BankReconciliationEvent.ResolvedType, ct);
        return item;
    }

    /// <summary>Publishes the item's event and records that it was delivered. A failing sink is logged; the next run retries.</summary>
    public async Task PublishAsync(ReconciliationItem item, string type, CancellationToken ct = default)
    {
        if (item.Status is ReconciliationStatus.Ignored or ReconciliationStatus.Pending)
            return;
        try
        {
            await _events.PublishAsync(BankReconciliationEvent.From(item, type, _clock.GetUtcNow().UtcDateTime), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Bank reconciliation event for {ItemId} was not delivered; it is retried on the next run", LogSanitizer.Sanitize(item.Id));
            return;
        }
        if (type != BankReconciliationEvent.Decided) return;
        item.EventPublishedAt = _clock.GetUtcNow().UtcDateTime;
        if (!await _store.TryUpdateItemAsync(item, item.Version, ct))
            _logger.LogInformation("Reconciliation item {ItemId} changed before its event was marked delivered", LogSanitizer.Sanitize(item.Id));
    }

    private ReconciliationItem NewItem(BankTransaction tx, string id, DateTime now) => new()
    {
        Id = id,
        TenantId = tx.TenantId,
        Service = _options.ServiceName,
        TransactionId = tx.Id,
        StatementId = tx.StatementId,
        TypeCode = tx.TypeCode,
        Direction = tx.Direction,
        AmountMinor = tx.AmountMinor,
        Amount = tx.Amount,
        Currency = tx.Currency,
        AsOfDate = tx.AsOfDate,
        MaskedAccount = tx.MaskedAccount,
        BankReference = tx.BankReference,
        CustomerReference = tx.CustomerReference,
        DedupeKey = _options.IsPlaceholderReference(tx.BankReference)
            ? null
            : $"{tx.AccountSuffix}|{tx.TypeCode}|{tx.BankReference!.Trim().ToUpperInvariant()}|{tx.AmountMinor}|{tx.AsOfDate:yyyyMMdd}",
        AsOfDateModifier = tx.AsOfDateModifier,
        Status = ReconciliationStatus.Pending,
        Reason = "Deciding",
        CreatedAt = now,
    };
}

/// <summary>Reference tokens a bank line may carry: its references and the words of its text.</summary>
public static class BankReferenceTokens
{
    public static IReadOnlyList<string> From(BankTransaction tx, int max = 12)
    {
        var tokens = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            foreach (var part in value.Split([' ', ',', ';', ':', '/', '*', '\\', '|', '=', '#'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var t = part.Trim('.', '-', '(', ')', '"', '\'');
                if (t.Length is >= 4 and <= 40 && t.Any(char.IsDigit) && !tokens.Contains(t, StringComparer.OrdinalIgnoreCase))
                    tokens.Add(t);
                if (tokens.Count >= max) return;
            }
        }
        Add(tx.CustomerReference);
        Add(tx.BankReference);
        Add(tx.Text);
        return tokens;
    }
}

/// <summary>The statement has a Requested or Approved supersession: its lines may not be worked or decided.</summary>
public sealed class StatementFrozenException : InvalidOperationException
{
    public StatementFrozenException(string message) : base(message) { }
}

/// <summary>
/// The freeze check matchers call at their commit point (right before they
/// post cash or record a settlement), so a run that started before a
/// supersession request cannot act on the statement after it.
/// </summary>
public sealed class BankStatementFreeze
{
    private readonly IBankReconciliationStore _store;

    public BankStatementFreeze(IBankReconciliationStore store) => _store = store;

    /// <exception cref="StatementFrozenException">The statement has a Requested or Approved supersession.</exception>
    public async Task EnsureNotFrozenAsync(string tenantId, string statementId, CancellationToken ct = default)
    {
        if (await _store.GetSupersessionAsync(tenantId, statementId, ct) is { Freezes: true } s)
            throw new StatementFrozenException(
                $"Statement {statementId} is {(s.Status == SupersessionStatus.Approved ? "superseded" : "being superseded")} by a corrected bank file; nothing is posted from it.");
    }
}
