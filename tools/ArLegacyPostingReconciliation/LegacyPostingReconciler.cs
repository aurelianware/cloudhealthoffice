using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ArService.Ledger;
using ArService.Models;
using ArService.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace ArLegacyPostingReconciliation;

public sealed class ReconcileOptions
{
    public required string CsvPath { get; init; }
    public bool Execute { get; init; }
    /// <summary>Required with <see cref="Execute"/>: the SHA-256 of the reviewed CSV.</summary>
    public string? ExpectedSha256 { get; init; }
    public required string Operator { get; init; }
    public string? LogFile { get; init; }
}

public sealed class ReconcileResult
{
    public required string CsvSha256 { get; init; }
    public required bool Executed { get; init; }
    public string? RunId { get; init; }
    public List<PostingOutcome> Postings { get; } = new();

    public int Count(OutcomeKind kind) => Postings.Count(p => p.Outcome == kind);
}

/// <summary>The CSV given with --execute is not the one that was reviewed.</summary>
public sealed class CsvHashMismatchException(string expected, string actual)
    : Exception($"CSV SHA-256 is {actual}, not the reviewed {expected}. Nothing was changed.");

/// <summary>
/// Carries out finance's decisions on legacy cash postings from the reviewed CSV
/// (<see cref="LegacyPostingLister"/> output with decision, reviewer and ticket filled in).
/// <list type="bullet">
/// <item>Dry-run unless <see cref="ReconcileOptions.Execute"/> is set with the CSV's SHA-256.</item>
/// <item><c>CORRECTED_MANUALLY</c>: the application gets the sentinel posted id
///   <c>manual-{posting}-{index}</c>; no balance changes, and apply and void never touch it.</item>
/// <item><c>APPLY_CREDIT</c>: the balance gets the controller's own credit entry
///   (<c>cash-{posting}-{index}</c>, <see cref="CashPostingLedger.CreditEntry"/>) through the
///   service's version-checked balance repository; an entry already on the balance is not added again.</item>
/// <item>A posting is all-or-nothing: every legacy application needs a row and a decision, and
///   every row must match the live posting and balances, or the posting is refused.</item>
/// <item>Running again changes nothing: a reconciled posting with the same decisions is reported
///   as already reconciled.</item>
/// </list>
/// </summary>
public sealed class LegacyPostingReconciler
{
    public const string CorrectedManually = "CORRECTED_MANUALLY";
    public const string ApplyCredit = "APPLY_CREDIT";
    private const int MaxConcurrencyAttempts = 5;

    private static readonly string[] RequiredColumns =
    [
        "tenant_id", "posting_id", "posting_number", "posting_status", "posting_amount", "applied_amount",
        "application_index", "ar_balance_id", "gl_account_id", "amount_applied",
        "decision", "reviewer", "reviewed_at", "ticket", "note"
    ];

    private readonly TenantDatabases _databases;
    private readonly Action<string> _out;

    public LegacyPostingReconciler(TenantDatabases databases, Action<string> output)
    {
        _databases = databases;
        _out = output;
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public async Task<ReconcileResult> RunAsync(ReconcileOptions options)
    {
        var bytes = await File.ReadAllBytesAsync(options.CsvPath);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (options.Execute)
        {
            if (string.IsNullOrWhiteSpace(options.ExpectedSha256))
                throw new ArgumentException("--execute requires --sha256 <hash of the reviewed CSV>");
            if (!string.Equals(options.ExpectedSha256.Trim(), sha, StringComparison.OrdinalIgnoreCase))
                throw new CsvHashMismatchException(options.ExpectedSha256.Trim(), sha);
            if (string.IsNullOrWhiteSpace(options.Operator))
                throw new ArgumentException("--execute requires --operator <your name or id>");
        }

        var rows = Csv.ParseWithHeader(new System.Text.UTF8Encoding(false).GetString(bytes));
        var missing = RequiredColumns.Where(c => rows.Count > 0 && !rows[0].Fields.ContainsKey(c)).ToList();
        if (rows.Count == 0)
            missing = [];
        if (missing.Count > 0)
            throw new FormatException($"CSV lacks column(s): {string.Join(", ", missing)}");

        var runId = options.Execute ? $"legacy-recon-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{sha[..12]}" : null;
        var result = new ReconcileResult { CsvSha256 = sha, Executed = options.Execute, RunId = runId };
        var mode = options.Execute ? "EXECUTE" : "DRY-RUN (nothing is changed)";
        _out($"Legacy cash posting reconciliation — {mode}");
        _out($"CSV {Path.GetFileName(options.CsvPath)} sha256={sha} rows={rows.Count} operator={options.Operator}");
        if (runId != null)
            _out($"Run id {runId}");

        LegacyReconciliationRun? run = null;
        var runs = _databases.Base.GetCollection<LegacyReconciliationRun>(LegacyReconciliationRun.Collection);
        if (options.Execute)
        {
            run = new LegacyReconciliationRun
            {
                Id = runId!, Operator = options.Operator, CsvSha256 = sha,
                CsvFileName = Path.GetFileName(options.CsvPath), StartedAt = DateTime.UtcNow, LogFile = options.LogFile
            };
            await runs.InsertOneAsync(run);
        }

        // Rows without a tenant or posting id cannot be matched to anything.
        foreach (var (rowNumber, fields) in rows.Where(r => Key(r.Fields) == null))
        {
            var outcome = new PostingOutcome
            {
                TenantId = Value(fields, "tenant_id"), PostingId = Value(fields, "posting_id"),
                Outcome = string.IsNullOrWhiteSpace(Value(fields, "decision")) ? OutcomeKind.Skipped : OutcomeKind.Refused,
                Reasons = { $"row {rowNumber}: tenant_id and posting_id are required" }
            };
            Report(outcome, options.Execute);
            result.Postings.Add(outcome);
        }

        foreach (var group in rows.Where(r => Key(r.Fields) != null).GroupBy(r => Key(r.Fields)!.Value))
        {
            PostingOutcome outcome;
            try
            {
                outcome = await ReconcilePostingAsync(group.Key.Tenant, group.Key.Posting, group.ToList(), options, sha, runId);
            }
            catch (Exception ex)
            {
                outcome = new PostingOutcome
                {
                    TenantId = group.Key.Tenant, PostingId = group.Key.Posting, Outcome = OutcomeKind.Failed,
                    Reasons = { $"{ex.GetType().Name}: {ex.Message} — nothing double-posts on a re-run; run again after fixing the cause" }
                };
            }
            Report(outcome, options.Execute);
            result.Postings.Add(outcome);
        }

        _out($"Summary: reconciled={result.Count(OutcomeKind.Reconciled)} alreadyReconciled={result.Count(OutcomeKind.AlreadyReconciled)} " +
             $"skipped={result.Count(OutcomeKind.Skipped)} refused={result.Count(OutcomeKind.Refused)} failed={result.Count(OutcomeKind.Failed)}" +
             (options.Execute ? string.Empty : " (dry-run: would be reconciled)"));

        if (run != null)
        {
            run.FinishedAt = DateTime.UtcNow;
            run.Postings = result.Postings;
            await runs.ReplaceOneAsync(r => r.Id == run.Id, run);
        }
        return result;
    }

    private void Report(PostingOutcome outcome, bool executed)
    {
        var label = outcome.Outcome == OutcomeKind.AlreadyReconciled ? "ALREADY_RECONCILED" : outcome.Outcome.ToString().ToUpperInvariant();
        if (!executed && outcome.Outcome == OutcomeKind.Reconciled)
            label = "WOULD RECONCILE";
        _out($"  => {outcome.TenantId}/{outcome.PostingId}: {label}");
        foreach (var reason in outcome.Reasons)
            _out($"     - {reason}");
    }

    private sealed record Planned(
        int Index, CashApplication Application, LegacyReconciliationDecision Decision, string PostedEntryId,
        string Reviewer, string Ticket);

    private async Task<PostingOutcome> ReconcilePostingAsync(
        string tenantId, string postingId, List<(int RowNumber, Dictionary<string, string> Fields)> rows,
        ReconcileOptions options, string sha, string? runId)
    {
        var outcome = new PostingOutcome { TenantId = tenantId, PostingId = postingId };
        var reasons = outcome.Reasons;
        _out(string.Empty);
        _out($"POSTING {tenantId}/{postingId} ({rows.Count} row(s), CSV rows {string.Join(",", rows.Select(r => r.RowNumber))})");

        // ── The decisions themselves ──────────────────────────────────────
        var decided = rows.Where(r => !string.IsNullOrWhiteSpace(Value(r.Fields, "decision"))).ToList();
        if (decided.Count == 0)
        {
            outcome.Outcome = OutcomeKind.Skipped;
            reasons.Add("no decision in the CSV");
            return outcome;
        }
        if (decided.Count != rows.Count)
            reasons.Add($"rows {string.Join(",", rows.Except(decided).Select(r => r.RowNumber))} have no decision: decide every application of a posting, or none");

        var decisions = new Dictionary<int, (LegacyReconciliationDecision Decision, Dictionary<string, string> Fields, int RowNumber)>();
        foreach (var (rowNumber, fields) in decided)
        {
            var decision = Value(fields, "decision").Trim().ToUpperInvariant() switch
            {
                CorrectedManually => LegacyReconciliationDecision.CorrectedManually,
                ApplyCredit => LegacyReconciliationDecision.ApplyCredit,
                _ => (LegacyReconciliationDecision?)null
            };
            if (decision == null)
                reasons.Add($"row {rowNumber}: decision '{Value(fields, "decision")}' is not {CorrectedManually} or {ApplyCredit}");
            if (string.IsNullOrWhiteSpace(Value(fields, "reviewer")))
                reasons.Add($"row {rowNumber}: reviewer is required");
            if (string.IsNullOrWhiteSpace(Value(fields, "ticket")))
                reasons.Add($"row {rowNumber}: ticket is required");
            if (!int.TryParse(Value(fields, "application_index"), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                reasons.Add($"row {rowNumber}: application_index '{Value(fields, "application_index")}' is not a number");
            else if (decisions.ContainsKey(index))
                reasons.Add($"row {rowNumber}: application {index} appears more than once");
            else if (decision != null)
                decisions[index] = (decision.Value, fields, rowNumber);
        }

        var reviewers = decided.Select(r => Value(r.Fields, "reviewer").Trim()).Where(v => v.Length > 0).Distinct().ToList();
        var tickets = decided.Select(r => Value(r.Fields, "ticket").Trim()).Where(v => v.Length > 0).Distinct().ToList();
        var reviewedAts = decided.Select(r => Value(r.Fields, "reviewed_at").Trim()).Where(v => v.Length > 0).Distinct().ToList();
        if (reviewers.Count > 1) reasons.Add($"one posting has several reviewers ({string.Join(", ", reviewers)})");
        if (tickets.Count > 1) reasons.Add($"one posting has several tickets ({string.Join(", ", tickets)})");
        if (reviewedAts.Count > 1) reasons.Add($"one posting has several reviewed_at values ({string.Join(", ", reviewedAts)})");
        DateTime? reviewedAt = null;
        if (reviewedAts.Count == 1)
        {
            if (DateTime.TryParse(reviewedAts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                reviewedAt = parsed;
            else
                reasons.Add($"reviewed_at '{reviewedAts[0]}' is not a date");
        }
        var note = string.Join(" | ", decided.Select(r => Value(r.Fields, "note").Trim()).Where(v => v.Length > 0).Distinct());
        if (reasons.Count > 0)
            return Refuse(outcome);
        var reviewer = reviewers[0];
        var ticket = tickets[0];

        // ── The live posting ──────────────────────────────────────────────
        var database = _databases.ForTenant(tenantId);
        var postings = database.GetCollection<CashPosting>(LegacyPostingLister.CashPostings);
        var balances = database.GetCollection<ArBalance>(LegacyPostingLister.ArBalances);
        var audits = database.GetCollection<LegacyReconciliationAudit>(LegacyReconciliationAudit.Collection);

        var posting = await postings.Find(p => p.Id == postingId && p.TenantId == tenantId).FirstOrDefaultAsync();
        if (posting == null)
        {
            reasons.Add("posting not found for this tenant");
            return Refuse(outcome);
        }

        if (posting.LegacyReconciliation?.Status == LegacyReconciliationStatus.Reconciled)
        {
            var recorded = posting.LegacyReconciliation.Applications.ToDictionary(a => a.ApplicationIndex, a => a.Decision);
            var same = recorded.Count == decisions.Count && decisions.All(d => recorded.TryGetValue(d.Key, out var r) && r == d.Value.Decision);
            if (!same)
            {
                reasons.Add($"already reconciled on {posting.LegacyReconciliation.ReconciledAt:u} by {posting.LegacyReconciliation.ReconciledBy} with different decisions; a reconciled posting is not changed by this tool");
                return Refuse(outcome);
            }
            if (options.Execute)
                await CompleteLeftoverAuditsAsync(audits, posting);
            outcome.Outcome = OutcomeKind.AlreadyReconciled;
            reasons.Add($"reconciled on {posting.LegacyReconciliation.ReconciledAt:u} by {posting.LegacyReconciliation.ReconciledBy} (CSV {posting.LegacyReconciliation.CsvSha256}); nothing to do");
            return outcome;
        }

        if (!CashPostingLedger.IsLegacy(posting))
        {
            reasons.Add($"not a legacy posting (status {posting.Status}; posted ids: {string.Join(",", posting.Applications.Select(a => a.PostedEntryId ?? "-"))}): it is not reconciled by this tool");
            return Refuse(outcome);
        }

        var first = decided[0].Fields;
        Expect(reasons, "posting_number", Value(first, "posting_number"), posting.PostingNumber);
        Expect(reasons, "posting_status", Value(first, "posting_status"), posting.Status.ToString());
        ExpectMoney(reasons, "posting_amount", Value(first, "posting_amount"), posting.Amount);
        ExpectMoney(reasons, "applied_amount", Value(first, "applied_amount"), posting.AppliedAmount);
        foreach (var (_, fields) in decided.Skip(1))
        {
            if (Value(fields, "posting_number") != Value(first, "posting_number")
                || Value(fields, "posting_amount") != Value(first, "posting_amount")
                || Value(fields, "applied_amount") != Value(first, "applied_amount")
                || Value(fields, "posting_status") != Value(first, "posting_status"))
                reasons.Add("rows of this posting disagree on its number, status or amounts");
        }
        var total = posting.Applications.Sum(a => a.AmountApplied);
        if (total != posting.AppliedAmount)
            reasons.Add($"live AppliedAmount {posting.AppliedAmount} is not the sum of its applications {total}");
        if (posting.Applications.Any(a => a.AmountApplied < 0))
            reasons.Add("live posting has a negative application");

        var legacy = CashPostingLedger.LegacyApplications(posting).ToDictionary(x => x.Index, x => x.Application);
        foreach (var index in legacy.Keys.Where(i => !decisions.ContainsKey(i)))
            reasons.Add($"application {index} needs a decision but has no row");
        foreach (var index in decisions.Keys.Where(i => !legacy.ContainsKey(i)))
            reasons.Add($"row {decisions[index].RowNumber}: application {index} is not a legacy application of this posting");
        foreach (var (index, (_, fields, rowNumber)) in decisions.Where(d => legacy.ContainsKey(d.Key)))
        {
            var application = legacy[index];
            Expect(reasons, $"row {rowNumber} ar_balance_id", Value(fields, "ar_balance_id"), application.ArBalanceId);
            Expect(reasons, $"row {rowNumber} gl_account_id", Value(fields, "gl_account_id"), application.GlAccountId);
            ExpectMoney(reasons, $"row {rowNumber} amount_applied", Value(fields, "amount_applied"), application.AmountApplied);
        }
        if (reasons.Count > 0)
            return Refuse(outcome);

        var plan = decisions.OrderBy(d => d.Key).Select(d => new Planned(
            d.Key, legacy[d.Key], d.Value.Decision,
            d.Value.Decision == LegacyReconciliationDecision.ApplyCredit
                ? CashPostingLedger.CreditEntryId(posting.Id, d.Key)
                : CashPostingLedger.ManualEntryId(posting.Id, d.Key),
            reviewer, ticket)).ToList();

        // ── The live balances ─────────────────────────────────────────────
        var live = await ReadBalancesAsync(balances, tenantId, posting.Id, plan, reasons);
        if (reasons.Count > 0)
            return Refuse(outcome);

        _out($"  {posting.PostingNumber} status {posting.Status} amount {posting.Amount} applied {posting.AppliedAmount} payer {posting.PayerType}/{posting.PayerReferenceId}; reviewer {reviewer}, ticket {ticket}");
        var preview = Clone(live);
        foreach (var step in plan)
            _out("  " + Describe(step, posting, preview, options.Operator, verb: options.Execute ? "" : "WOULD "));

        if (!options.Execute)
        {
            outcome.Outcome = OutcomeKind.Reconciled;
            reasons.Add($"dry-run: would reconcile {plan.Count} application(s)");
            return outcome;
        }

        // ── Execute ───────────────────────────────────────────────────────
        var now = DateTime.UtcNow;
        var records = new Dictionary<int, LegacyReconciliationAudit>();
        foreach (var step in plan)
        {
            var record = new LegacyReconciliationAudit
            {
                Id = LegacyReconciliationAudit.IdFor(tenantId, posting.Id, step.Index),
                RunId = runId!, State = AuditState.Intended, Operator = options.Operator, At = now, CsvSha256 = sha,
                TenantId = tenantId, PostingId = posting.Id, PostingNumber = posting.PostingNumber,
                ApplicationIndex = step.Index, ArBalanceId = step.Application.ArBalanceId,
                Decision = step.Decision == LegacyReconciliationDecision.ApplyCredit ? ApplyCredit : CorrectedManually,
                Amount = step.Application.AmountApplied, PostedEntryId = step.PostedEntryId,
                Before = BalanceSnapshot.Of(live[step.Application.ArBalanceId]),
                Reviewer = reviewer, ReviewedAt = reviewedAt?.ToString("o"), Ticket = ticket, Note = note
            };
            // Write-ahead: the intent is durable before any balance or posting changes.
            await audits.ReplaceOneAsync(a => a.Id == record.Id, record, new ReplaceOptions { IsUpsert = true });
            records[step.Index] = record;
        }

        var credits = plan.Where(p => p.Decision == LegacyReconciliationDecision.ApplyCredit).ToList();
        if (credits.Count > 0)
        {
            // The service's own repository: a save of a stale balance fails and is retried on a fresh read.
            var repository = new MongoArBalanceRepository(database, TenantDatabases.AccessorFor(tenantId), NullLogger<MongoArBalanceRepository>.Instance);
            for (var attempt = 1; ; attempt++)
            {
                var fresh = await ReadBalancesAsync(balances, tenantId, posting.Id, plan, reasons);
                if (reasons.Count > 0)
                    return Refuse(outcome);
                var changed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var step in plan)
                {
                    var balance = fresh[step.Application.ArBalanceId];
                    records[step.Index].Before = BalanceSnapshot.Of(balance);
                    records[step.Index].Credited = false;
                    if (step.Decision == LegacyReconciliationDecision.ApplyCredit
                        && balance.PostingEntries.All(e => e.EntryId != step.PostedEntryId))
                    {
                        CashPostingLedger.Post(balance, CashPostingLedger.CreditEntry(posting, step.Index, options.Operator, now,
                            $"Legacy reconciliation ({ticket}){(string.IsNullOrEmpty(step.Application.Memo) ? "" : ": " + step.Application.Memo)}"), posting.PayerType);
                        changed.Add(balance.Id);
                        records[step.Index].Credited = true;
                    }
                    records[step.Index].After = BalanceSnapshot.Of(balance);
                }
                try
                {
                    foreach (var balance in fresh.Values.Where(b => changed.Contains(b.Id)))
                    {
                        balance.LastUpdatedAt = now;
                        await repository.UpdateAsync(balance);
                    }
                    // The saved version is one higher than the snapshot taken before the save.
                    foreach (var step in plan.Where(s => changed.Contains(s.Application.ArBalanceId)))
                        records[step.Index].After!.Version = fresh[step.Application.ArBalanceId].Version;
                    break;
                }
                catch (ArConcurrencyException) when (attempt < MaxConcurrencyAttempts)
                {
                    _out($"  balance changed concurrently; re-reading (attempt {attempt + 1})");
                }
            }
        }
        else
        {
            foreach (var step in plan)
                records[step.Index].After = BalanceSnapshot.Of(live[step.Application.ArBalanceId]);
        }

        // The posting: posted ids and the reconciliation record, saved only if nobody changed it since it was read.
        var readAt = posting.LastUpdatedAt;
        var readStatus = posting.Status;
        foreach (var step in plan)
        {
            step.Application.PostedEntryId = step.PostedEntryId;
            step.Application.PostedAt = now;
        }
        posting.LegacyReconciliation = new LegacyPostingReconciliation
        {
            Status = LegacyReconciliationStatus.Reconciled,
            ReviewedBy = reviewer, ReviewedAt = reviewedAt, Ticket = ticket, Note = note.Length > 0 ? note : null,
            ReconciledBy = options.Operator, ReconciledAt = now, CsvSha256 = sha,
            Applications = plan.Select(p => new LegacyApplicationDecision
            {
                ApplicationIndex = p.Index, ArBalanceId = p.Application.ArBalanceId, Amount = p.Application.AmountApplied,
                Decision = p.Decision, PostedEntryId = p.PostedEntryId
            }).ToList()
        };
        posting.LastUpdatedAt = now;
        var f = Builders<CashPosting>.Filter;
        var saved = await postings.ReplaceOneAsync(
            f.Eq(p => p.Id, posting.Id) & f.Eq(p => p.TenantId, tenantId) & f.Eq(p => p.LastUpdatedAt, readAt) & f.Eq(p => p.Status, readStatus),
            posting);
        if (saved.MatchedCount == 0)
        {
            outcome.Outcome = OutcomeKind.Failed;
            reasons.Add("the posting changed while it was being reconciled and was not saved; any credit already made is recognised by its entry id, so run again");
            return outcome;
        }

        foreach (var record in records.Values)
        {
            record.State = AuditState.Completed;
            record.CompletedAt = DateTime.UtcNow;
            await audits.ReplaceOneAsync(a => a.Id == record.Id, record);
        }

        outcome.Outcome = OutcomeKind.Reconciled;
        reasons.Add($"reconciled {plan.Count} application(s): {credits.Count} credited, {plan.Count - credits.Count} marked corrected manually");
        return outcome;
    }

    private async Task<Dictionary<string, ArBalance>> ReadBalancesAsync(
        IMongoCollection<ArBalance> balances, string tenantId, string postingId, List<Planned> plan, List<string> reasons)
    {
        var result = new Dictionary<string, ArBalance>(StringComparer.Ordinal);
        foreach (var step in plan)
        {
            var id = step.Application.ArBalanceId;
            if (!result.TryGetValue(id, out var balance))
            {
                balance = await balances.Find(b => b.Id == id && b.TenantId == tenantId).FirstOrDefaultAsync();
                if (balance == null)
                {
                    reasons.Add($"application {step.Index}: AR balance {id} not found");
                    continue;
                }
                result[id] = balance;
            }
            if (!string.Equals(balance.GlAccountId, step.Application.GlAccountId, StringComparison.Ordinal))
                reasons.Add($"application {step.Index}: AR balance {id} belongs to GL account {balance.GlAccountId}, not {step.Application.GlAccountId}");
            var creditId = CashPostingLedger.CreditEntryId(postingId, step.Index);
            if (step.Decision == LegacyReconciliationDecision.CorrectedManually && balance.PostingEntries.Any(e => e.EntryId == creditId))
                reasons.Add($"application {step.Index}: AR balance {id} already holds credit entry {creditId}; marking it corrected manually would leave that credit on top of the manual correction — investigate");
        }
        return result;
    }

    private static string Describe(Planned step, CashPosting posting, Dictionary<string, ArBalance> balances, string postedBy, string verb)
    {
        var balance = balances[step.Application.ArBalanceId];
        var before = BalanceSnapshot.Of(balance);
        string action;
        if (step.Decision == LegacyReconciliationDecision.CorrectedManually)
        {
            action = $"{verb}mark corrected manually, no credit; PostedEntryId := {step.PostedEntryId}";
        }
        else if (balance.PostingEntries.Any(e => e.EntryId == step.PostedEntryId))
        {
            action = $"{verb}record credit entry {step.PostedEntryId}, already on the balance (earlier interrupted run): not credited again";
        }
        else
        {
            CashPostingLedger.Post(balance, CashPostingLedger.CreditEntry(posting, step.Index, postedBy, DateTime.UtcNow), posting.PayerType);
            action = $"{verb}credit {step.Application.AmountApplied} as entry {step.PostedEntryId}";
        }
        var after = BalanceSnapshot.Of(balance);
        return $"app {step.Index} -> balance {step.Application.ArBalanceId} (GL {step.Application.GlAccountId}) amount {step.Application.AmountApplied} " +
               $"{(step.Decision == LegacyReconciliationDecision.ApplyCredit ? ApplyCredit : CorrectedManually)}: {action}; " +
               $"balance before [{before}] after [{after}]";
    }

    private static async Task CompleteLeftoverAuditsAsync(IMongoCollection<LegacyReconciliationAudit> audits, CashPosting posting)
    {
        // The posting was saved but the run stopped before its audit records were completed.
        var leftovers = await audits.Find(a => a.PostingId == posting.Id && a.TenantId == posting.TenantId && a.State == AuditState.Intended).ToListAsync();
        foreach (var record in leftovers)
        {
            record.State = AuditState.Completed;
            record.CompletedAt = posting.LegacyReconciliation?.ReconciledAt ?? DateTime.UtcNow;
            await audits.ReplaceOneAsync(a => a.Id == record.Id, record);
        }
    }

    private static PostingOutcome Refuse(PostingOutcome outcome)
    {
        outcome.Outcome = OutcomeKind.Refused;
        return outcome;
    }

    private static void Expect(List<string> reasons, string column, string csv, string live)
    {
        if (!string.Equals(Csv.Unguard(csv.Trim()), live, StringComparison.Ordinal))
            reasons.Add($"{column}: CSV has '{csv}', live data has '{live}'");
    }

    private static void ExpectMoney(List<string> reasons, string column, string csv, decimal live)
    {
        if (!decimal.TryParse(csv.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            reasons.Add($"{column}: CSV value '{csv}' is not an amount");
        else if (value != live)
            reasons.Add($"{column}: CSV has {value}, live data has {live}");
    }

    private static (string Tenant, string Posting)? Key(Dictionary<string, string> fields)
    {
        var tenant = Csv.Unguard(Value(fields, "tenant_id").Trim());
        var posting = Csv.Unguard(Value(fields, "posting_id").Trim());
        return tenant.Length == 0 || posting.Length == 0 ? null : (tenant, posting);
    }

    private static string Value(Dictionary<string, string> fields, string column) =>
        fields.TryGetValue(column, out var value) ? value : string.Empty;

    private static Dictionary<string, ArBalance> Clone(Dictionary<string, ArBalance> balances) =>
        balances.ToDictionary(b => b.Key, b => JsonSerializer.Deserialize<ArBalance>(JsonSerializer.Serialize(b.Value))!, StringComparer.Ordinal);
}
