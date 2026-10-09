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
    /// <summary>Required: the database the operator means to touch; must be the configured one.</summary>
    public string? ConfirmDatabase { get; init; }
    /// <summary>When given, the CSV may hold rows for these tenants only.</summary>
    public IReadOnlyCollection<string>? Tenants { get; init; }
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
/// <item>Refuses to run against a database other than the confirmed one, a CSV listed in another
///   environment, or (for execute) a database no guard-capable ar-service has started against
///   (<see cref="ArServiceCapabilities"/>).</item>
/// <item><c>CORRECTED_MANUALLY</c>: the application gets the sentinel posted id
///   <c>manual-{posting}-{index}</c>; no balance changes, and apply and void never touch it.
///   It is carried out even when the balance no longer exists (recorded as missing).</item>
/// <item><c>APPLY_CREDIT</c>: the balance gets the controller's own credit entry
///   (<c>cash-{posting}-{index}</c>, <see cref="CashPostingLedger.CreditEntry"/>) through the
///   service's version-checked balance repository; an entry already on the balance is not added again.</item>
/// <item>A posting is all-or-nothing: every legacy application needs a row and a decision, and
///   every row must match the live posting and balances, or the posting is refused.</item>
/// <item>Running again changes nothing: a reconciled posting with the same decisions is reported
///   as already reconciled.</item>
/// <item>Every step is audited append-only (<see cref="LegacyReconciliationAudit"/>,
///   <see cref="LegacyReconciliationRun"/>).</item>
/// </list>
/// </summary>
public sealed class LegacyPostingReconciler
{
    public const string CorrectedManually = "CORRECTED_MANUALLY";
    public const string ApplyCredit = "APPLY_CREDIT";
    public const string ReviewedAtFormat = "yyyy-MM-dd";
    private const int MaxConcurrencyAttempts = 5;

    private readonly TenantDatabases _databases;
    private readonly Action<string> _out;

    public LegacyPostingReconciler(TenantDatabases databases, Action<string> output)
    {
        _databases = databases;
        _out = output;
    }

    /// <summary>Test seam: runs just before each balance save.</summary>
    internal Func<ArBalance, Task>? BeforeBalanceSave { get; set; }

    /// <summary>Test seam: runs just before the posting is saved.</summary>
    internal Func<CashPosting, Task>? BeforePostingSave { get; set; }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public async Task<ReconcileResult> RunAsync(ReconcileOptions options)
    {
        _databases.Confirm(options.ConfirmDatabase);

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

        var rows = Csv.ParseWithHeader(new System.Text.UTF8Encoding(false).GetString(bytes), LegacyPostingLister.Header);
        CheckEnvironment(rows, options.Tenants);

        var mode = options.Execute ? "EXECUTE" : "DRY-RUN (nothing is changed)";
        _out($"Legacy cash posting reconciliation — {mode}");
        _out(_databases.Describe());
        var capabilities = await ArServiceCapabilities.ReadAsync(_databases.Base);
        CheckService(capabilities, options.Execute);

        var runId = options.Execute ? $"legacy-recon-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{sha[..12]}" : null;
        var result = new ReconcileResult { CsvSha256 = sha, Executed = options.Execute, RunId = runId };
        _out($"CSV {Path.GetFileName(options.CsvPath)} sha256={sha} rows={rows.Count} operator={options.Operator}");
        if (runId != null)
            _out($"Run id {runId}");

        var runs = _databases.Base.GetCollection<LegacyReconciliationRun>(LegacyReconciliationRun.Collection);
        if (options.Execute)
        {
            await runs.InsertOneAsync(new LegacyReconciliationRun
            {
                Id = runId!, State = RunState.Running, Operator = options.Operator,
                Host = _databases.Host, Database = _databases.BaseDatabaseName, UseTenantScoping = _databases.UseTenantScoping,
                CsvSha256 = sha, CsvFileName = Path.GetFileName(options.CsvPath), StartedAt = DateTime.UtcNow,
                LogFile = options.LogFile, ServiceBuild = capabilities?.Build
            });
        }

        async Task Record(PostingOutcome outcome)
        {
            Report(outcome, options.Execute);
            result.Postings.Add(outcome);
            if (runId != null)
                await runs.UpdateOneAsync(r => r.Id == runId,
                    LegacyReconciliationRun.Update.Push(r => r.Postings, outcome).Set(r => r.CurrentPosting, null));
        }

        // Rows without a tenant or posting id cannot be matched to anything.
        foreach (var (rowNumber, fields) in rows.Where(r => Key(r.Fields) == null))
        {
            await Record(new PostingOutcome
            {
                TenantId = Value(fields, "tenant_id"), PostingId = Value(fields, "posting_id"),
                Outcome = string.IsNullOrWhiteSpace(Value(fields, "decision")) ? OutcomeKind.Skipped : OutcomeKind.Refused,
                Reasons = { $"row {rowNumber}: tenant_id and posting_id are required" }
            });
        }

        foreach (var group in rows.Where(r => Key(r.Fields) != null).GroupBy(r => Key(r.Fields)!.Value))
        {
            if (runId != null)
                await runs.UpdateOneAsync(r => r.Id == runId,
                    LegacyReconciliationRun.Update.Set(r => r.CurrentPosting, $"{group.Key.Tenant}/{group.Key.Posting}"));
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
            await Record(outcome);
        }

        _out($"Summary: reconciled={result.Count(OutcomeKind.Reconciled)} alreadyReconciled={result.Count(OutcomeKind.AlreadyReconciled)} " +
             $"skipped={result.Count(OutcomeKind.Skipped)} refused={result.Count(OutcomeKind.Refused)} failed={result.Count(OutcomeKind.Failed)}" +
             (options.Execute ? string.Empty : " (dry-run: would be reconciled)"));

        if (runId != null)
            await runs.UpdateOneAsync(r => r.Id == runId,
                LegacyReconciliationRun.Update.Set(r => r.State, RunState.Finished).Set(r => r.FinishedAt, DateTime.UtcNow));
        return result;
    }

    /// <summary>The CSV was listed from this environment, and holds only the tenants asked for.</summary>
    private void CheckEnvironment(List<(int RowNumber, Dictionary<string, string> Fields)> rows, IReadOnlyCollection<string>? tenants)
    {
        var wrong = rows
            .Where(r => Value(r.Fields, "source_database") != _databases.BaseDatabaseName || Value(r.Fields, "source_host") != _databases.Host)
            .Select(r => $"{Value(r.Fields, "source_database")} on {Value(r.Fields, "source_host")}")
            .Distinct().ToList();
        if (wrong.Count > 0)
            throw new EnvironmentMismatchException(
                $"the CSV was listed from {string.Join("; ", wrong)}, not from database {_databases.BaseDatabaseName} on {_databases.Host}. " +
                "Nothing was changed. Reconcile a CSV only against the environment it was listed from.");

        if (tenants is { Count: > 0 })
        {
            var others = rows.Select(r => Csv.Unguard(Value(r.Fields, "tenant_id").Trim()))
                .Where(t => !tenants.Contains(t)).Distinct().ToList();
            if (others.Count > 0)
                throw new EnvironmentMismatchException(
                    $"the CSV has rows for tenant(s) {string.Join(", ", others)}, outside --tenant {string.Join(",", tenants)}. Nothing was changed.");
        }
    }

    /// <summary>
    /// Execute needs a guard-capable ar-service on this database: the build that writes balance
    /// versions and posted ids itself, refuses unreconciled legacy postings, and ignores unknown
    /// elements. An older build would fail to read what the tool writes, and could overwrite a
    /// credit with an unconditional balance save.
    /// </summary>
    private void CheckService(ArServiceCapabilities? capabilities, bool execute)
    {
        var missing = capabilities == null
            ? ArServiceCapabilities.Current.ToList()
            : ArServiceCapabilities.Current.Except(capabilities.Capabilities).ToList();
        if (missing.Count == 0)
        {
            _out($"ar-service: build {capabilities!.Build} on {capabilities.Host}, last started {capabilities.LastStartedAt:u}, capabilities {string.Join(", ", capabilities.Capabilities)}");
            return;
        }
        var message = capabilities == null
            ? $"no ar-service capability marker in {_databases.BaseDatabaseName}.{ArServiceCapabilities.Collection}: no ar-service build with the legacy posting guard has started against this database"
            : $"the ar-service capability marker (build {capabilities.Build}, last started {capabilities.LastStartedAt:u}) lacks {string.Join(", ", missing)}";
        if (execute)
            throw new EnvironmentMismatchException(
                $"{message}. Deploy the ar-service build with the guard first, then run again. Nothing was changed.");
        _out($"WARNING: {message}. --execute will be refused until it is deployed.");
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
            if (DateTime.TryParseExact(reviewedAts[0], ReviewedAtFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                reviewedAt = parsed;
            else
                reasons.Add($"reviewed_at '{reviewedAts[0]}' must be a date written {ReviewedAtFormat} (for example 2026-10-08); a spreadsheet may have reformatted it");
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
                await ConfirmEarlierRunAsync(audits, posting, runId!);
            outcome.Outcome = OutcomeKind.AlreadyReconciled;
            reasons.Add($"reconciled on {posting.LegacyReconciliation.ReconciledAt:u} by {posting.LegacyReconciliation.ReconciledBy} (CSV {posting.LegacyReconciliation.CsvSha256}, run {posting.LegacyReconciliation.RunId}); nothing to do");
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

        return await ExecuteAsync(outcome, tenantId, posting, plan, live, postings, balances, audits, database,
            options, sha, runId!, reviewer, reviewedAt, ticket, note);
    }

    private async Task<PostingOutcome> ExecuteAsync(
        PostingOutcome outcome, string tenantId, CashPosting posting, List<Planned> plan, Dictionary<string, ArBalance> live,
        IMongoCollection<CashPosting> postings, IMongoCollection<ArBalance> balances, IMongoCollection<LegacyReconciliationAudit> audits,
        IMongoDatabase database, ReconcileOptions options, string sha, string runId,
        string reviewer, DateTime? reviewedAt, string ticket, string note)
    {
        var reasons = outcome.Reasons;
        var now = DateTime.UtcNow;
        var ids = plan.ToDictionary(s => s.Index, s => LegacyReconciliationAudit.IdFor(runId, tenantId, posting.Id, s.Index));
        var runs = _databases.Base.GetCollection<LegacyReconciliationRun>(LegacyReconciliationRun.Collection);

        // Write-ahead: the intent is durable before any balance or posting changes. Inserted, never replaced.
        await audits.InsertManyAsync(plan.Select(step => new LegacyReconciliationAudit
        {
            Id = ids[step.Index], RunId = runId, State = AuditState.Intended, Operator = options.Operator, At = now, CsvSha256 = sha,
            Host = _databases.Host, Database = database.DatabaseNamespace.DatabaseName,
            TenantId = tenantId, PostingId = posting.Id, PostingNumber = posting.PostingNumber,
            ApplicationIndex = step.Index, ArBalanceId = step.Application.ArBalanceId,
            BalanceMissing = !live.ContainsKey(step.Application.ArBalanceId),
            Decision = step.Decision == LegacyReconciliationDecision.ApplyCredit ? ApplyCredit : CorrectedManually,
            Amount = step.Application.AmountApplied, PostedEntryId = step.PostedEntryId,
            Before = live.TryGetValue(step.Application.ArBalanceId, out var b) ? BalanceSnapshot.Of(b) : null,
            Reviewer = reviewer, ReviewedAt = reviewedAt?.ToString(ReviewedAtFormat, CultureInfo.InvariantCulture), Ticket = ticket, Note = note,
            Events = { AuditEvent.Of(AuditEvent.Intended, step.Decision == LegacyReconciliationDecision.ApplyCredit
                ? $"credit {step.Application.AmountApplied} as {step.PostedEntryId}"
                : $"mark corrected manually as {step.PostedEntryId}{(live.ContainsKey(step.Application.ArBalanceId) ? "" : " (balance missing)")}") }
        }));

        var u = Builders<LegacyReconciliationAudit>.Update;
        Task Append(IEnumerable<Planned> steps, AuditEvent ev, UpdateDefinition<LegacyReconciliationAudit>? also = null)
        {
            var update = also == null ? u.Push(a => a.Events, ev) : u.Combine(u.Push(a => a.Events, ev), also);
            return audits.UpdateManyAsync(Builders<LegacyReconciliationAudit>.Filter.In(a => a.Id, steps.Select(s => ids[s.Index])), update);
        }

        try
        {
            var latest = live;
            var creditedThisRun = new HashSet<int>();
            var credits = plan.Where(p => p.Decision == LegacyReconciliationDecision.ApplyCredit).ToList();
            if (credits.Count > 0)
            {
                // The service's own repository: a save of a stale balance fails and is retried on a fresh read.
                var repository = new MongoArBalanceRepository(database, TenantDatabases.AccessorFor(tenantId), NullLogger<MongoArBalanceRepository>.Instance);
                for (var attempt = 1; ; attempt++)
                {
                    var fresh = await ReadBalancesAsync(balances, tenantId, posting.Id, plan, reasons);
                    latest = fresh;
                    if (reasons.Count > 0)
                    {
                        await Append(plan, AuditEvent.Of(AuditEvent.Failed, string.Join("; ", reasons)), u.Set(a => a.State, AuditState.Failed));
                        return Refuse(outcome);
                    }

                    var toCredit = credits
                        .Where(s => fresh[s.Application.ArBalanceId].PostingEntries.All(e => e.EntryId != s.PostedEntryId))
                        .GroupBy(s => s.Application.ArBalanceId)
                        .ToList();
                    string? saving = null;
                    try
                    {
                        foreach (var group in toCredit)
                        {
                            saving = group.Key;
                            var balance = fresh[group.Key];
                            foreach (var step in group)
                                CashPostingLedger.Post(balance, CashPostingLedger.CreditEntry(posting, step.Index, options.Operator, now,
                                    $"Legacy reconciliation ({ticket}){(string.IsNullOrEmpty(step.Application.Memo) ? "" : ": " + step.Application.Memo)}"), posting.PayerType);
                            balance.LastUpdatedAt = now;
                            await Append(group, AuditEvent.Of(AuditEvent.CreditIntended,
                                $"saving balance {balance.Id} over version {balance.Version} (attempt {attempt})", BalanceSnapshot.Of(balance)));

                            if (BeforeBalanceSave != null)
                                await BeforeBalanceSave(balance);
                            await repository.UpdateAsync(balance);

                            // Durable as soon as the balance is saved, before anything else can fail.
                            var after = BalanceSnapshot.Of(balance);
                            await Append(group, AuditEvent.Of(AuditEvent.Credited, $"balance {balance.Id} saved at version {balance.Version}", after),
                                u.Set(a => a.Credited, true).Set(a => a.After, after));
                            await runs.UpdateOneAsync(r => r.Id == runId, LegacyReconciliationRun.Update.Push(r => r.Actions, new RunAction
                            {
                                At = DateTime.UtcNow, TenantId = tenantId, PostingId = posting.Id, ArBalanceId = balance.Id,
                                EntryIds = group.Select(s => s.PostedEntryId).ToList(), Amount = group.Sum(s => s.Application.AmountApplied), After = after
                            }));
                            foreach (var step in group)
                                creditedThisRun.Add(step.Index);
                        }
                        break;
                    }
                    catch (ArConcurrencyException) when (attempt < MaxConcurrencyAttempts)
                    {
                        _out($"  balance {saving} changed concurrently; re-reading (attempt {attempt + 1})");
                        await Append(credits.Where(s => s.Application.ArBalanceId == saving),
                            AuditEvent.Of(AuditEvent.ConcurrencyRetry, $"balance {saving} changed concurrently; attempt {attempt + 1}"));
                    }
                }

                var already = credits.Where(s => !creditedThisRun.Contains(s.Index)).ToList();
                if (already.Count > 0)
                    await Append(already, AuditEvent.Of(AuditEvent.AlreadyOnBalance, "credit entry already on the balance; not credited again"));
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
                ReconciledBy = options.Operator, ReconciledAt = now, CsvSha256 = sha, RunId = runId,
                Applications = plan.Select(p => new LegacyApplicationDecision
                {
                    ApplicationIndex = p.Index, ArBalanceId = p.Application.ArBalanceId, Amount = p.Application.AmountApplied,
                    Decision = p.Decision, PostedEntryId = p.PostedEntryId, BalanceMissing = !latest.ContainsKey(p.Application.ArBalanceId)
                }).ToList()
            };
            posting.LastUpdatedAt = now;
            if (BeforePostingSave != null)
                await BeforePostingSave(posting);
            var f = Builders<CashPosting>.Filter;
            var saved = await postings.ReplaceOneAsync(
                f.Eq(p => p.Id, posting.Id) & f.Eq(p => p.TenantId, tenantId) & f.Eq(p => p.LastUpdatedAt, readAt) & f.Eq(p => p.Status, readStatus),
                posting);
            if (saved.MatchedCount == 0)
            {
                await Append(plan, AuditEvent.Of(AuditEvent.PostingSaveConflict,
                    "the posting changed while it was being reconciled and was not saved; credits made are recognised by entry id on a re-run"),
                    u.Set(a => a.State, AuditState.Failed));
                outcome.Outcome = OutcomeKind.Failed;
                reasons.Add("the posting changed while it was being reconciled and was not saved; any credit already made is recognised by its entry id, so run again");
                return outcome;
            }

            foreach (var step in plan)
            {
                var completed = u.Set(a => a.State, AuditState.Completed).Set(a => a.CompletedAt, DateTime.UtcNow);
                if (!creditedThisRun.Contains(step.Index))
                    completed = completed.Set(a => a.After,
                        latest.TryGetValue(step.Application.ArBalanceId, out var balance) ? BalanceSnapshot.Of(balance) : null);
                await Append([step], AuditEvent.Of(AuditEvent.PostingSaved, $"posting saved; PostedEntryId := {step.PostedEntryId}"), completed);
            }
        }
        catch (Exception ex)
        {
            try
            {
                await Append(plan, AuditEvent.Of(AuditEvent.Failed, $"{ex.GetType().Name}: {ex.Message}"), u.Set(a => a.State, AuditState.Failed));
            }
            catch
            {
                // The original error is what matters; the run record and log carry it.
            }
            throw;
        }

        var creditCount = plan.Count(p => p.Decision == LegacyReconciliationDecision.ApplyCredit);
        outcome.Outcome = OutcomeKind.Reconciled;
        reasons.Add($"reconciled {plan.Count} application(s): {creditCount} credited, {plan.Count - creditCount} marked corrected manually");
        return outcome;
    }

    /// <summary>
    /// Reads every balance the plan touches. A missing balance refuses an <c>APPLY_CREDIT</c> but
    /// not a <c>CORRECTED_MANUALLY</c>, which changes no balance; it is left out of the result.
    /// </summary>
    private async Task<Dictionary<string, ArBalance>> ReadBalancesAsync(
        IMongoCollection<ArBalance> balances, string tenantId, string postingId, List<Planned> plan, List<string> reasons)
    {
        var result = new Dictionary<string, ArBalance>(StringComparer.Ordinal);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in plan)
        {
            var id = step.Application.ArBalanceId;
            if (!result.TryGetValue(id, out var balance) && !missing.Contains(id))
            {
                balance = await balances.Find(b => b.Id == id && b.TenantId == tenantId).FirstOrDefaultAsync();
                if (balance == null)
                    missing.Add(id);
                else
                    result[id] = balance;
            }
            if (balance == null)
            {
                if (step.Decision == LegacyReconciliationDecision.ApplyCredit)
                    reasons.Add($"application {step.Index}: AR balance {id} not found; {ApplyCredit} needs it ({CorrectedManually} can be recorded without it)");
                continue;
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
        var head = $"app {step.Index} -> balance {step.Application.ArBalanceId} (GL {step.Application.GlAccountId}) amount {step.Application.AmountApplied} " +
                   $"{(step.Decision == LegacyReconciliationDecision.ApplyCredit ? ApplyCredit : CorrectedManually)}: ";
        if (!balances.TryGetValue(step.Application.ArBalanceId, out var balance))
            return head + $"{verb}mark corrected manually, no credit; PostedEntryId := {step.PostedEntryId}; balance NOT FOUND (recorded as missing)";

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
        return head + $"{action}; balance before [{before}] after [{after}]";
    }

    /// <summary>
    /// The posting was saved by an earlier run that stopped before completing its audit records:
    /// those records gain an event saying so (their history is kept).
    /// </summary>
    private static async Task ConfirmEarlierRunAsync(IMongoCollection<LegacyReconciliationAudit> audits, CashPosting posting, string runId)
    {
        var earlierRun = posting.LegacyReconciliation?.RunId;
        if (earlierRun == null)
            return;
        var f = Builders<LegacyReconciliationAudit>.Filter;
        await audits.UpdateManyAsync(
            f.Eq(a => a.RunId, earlierRun) & f.Eq(a => a.PostingId, posting.Id) & f.Eq(a => a.TenantId, posting.TenantId) & f.Eq(a => a.State, AuditState.Intended),
            Builders<LegacyReconciliationAudit>.Update
                .Push(a => a.Events, AuditEvent.Of(AuditEvent.ConfirmedByLaterRun, $"run {runId} found the posting reconciled by this run"))
                .Set(a => a.State, AuditState.Completed)
                .Set(a => a.CompletedAt, posting.LegacyReconciliation!.ReconciledAt));
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
        if (!decimal.TryParse(Csv.Unguard(csv.Trim()), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
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
