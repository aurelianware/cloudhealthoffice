using System.Security.Cryptography;
using CloudHealthOffice.BankReconciliation.Bai2;
using CloudHealthOffice.FieldProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CloudHealthOffice.Infrastructure.Logging;

namespace CloudHealthOffice.BankReconciliation;

/// <summary>Configuration section <c>BankReconciliation</c>.</summary>
public sealed class BankReconciliationOptions
{
    public const string SectionName = "BankReconciliation";

    /// <summary>The service name recorded on files and items (set at registration).</summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>Largest file accepted (default 20 MB).</summary>
    public int MaxFileBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>
    /// Account suffixes (last four characters) this service reconciles; empty
    /// (default) reconciles every account in the file. Transactions of other
    /// accounts are recorded as Ignored.
    /// </summary>
    public List<string> AccountSuffixes { get; set; } = [];

    /// <summary>How long a Pending item stays claimed before another run may decide it again.</summary>
    public int PendingLeaseMinutes { get; set; } = 10;

    /// <summary>
    /// Bank-specific credit type codes (e.g. 9xx custom codes), consulted
    /// before the standard ranges (100–399 credit, 400–699 debit).
    /// </summary>
    public List<string> CreditTypeCodes { get; set; } = [];

    /// <summary>Bank-specific debit type codes, consulted before the standard ranges.</summary>
    public List<string> DebitTypeCodes { get; set; } = [];

    /// <summary>
    /// Interim reports (02 as-of-date modifier 1 or 3). Reconcile (default):
    /// their lines are decided, and the final report's copy of a line (same
    /// account, type, bank reference, amount and date) is then recorded as
    /// Ignored instead of being decided again. Skip: interim lines are Ignored
    /// and only final reports are reconciled.
    /// </summary>
    public InterimReportHandling InterimReports { get; set; } = InterimReportHandling.Reconcile;

    /// <summary>
    /// Bank references that do not identify a transaction ("NONREF" and the
    /// like). Lines carrying one, an all-zero reference or one shorter than 4
    /// characters are never treated as the same transaction as another line.
    /// </summary>
    public List<string> PlaceholderBankReferences { get; set; } = ["NONREF", "NONE", "N/A", "NA", "UNKNOWN"];

    internal bool IsPlaceholderReference(string? reference)
    {
        var r = reference?.Trim() ?? string.Empty;
        return r.Length < 4 || r.All(c => c == '0') || PlaceholderBankReferences.Contains(r, StringComparer.OrdinalIgnoreCase);
    }

    internal Bai2ParseOptions ParseOptions() => new() { CreditTypeCodes = CreditTypeCodes, DebitTypeCodes = DebitTypeCodes };
}

public enum InterimReportHandling
{
    Reconcile,
    Skip,
}

public enum BankStatementIngestStatus
{
    /// <summary>New, accepted file.</summary>
    Ingested,

    /// <summary>The same bytes were received before; the earlier outcome stands (reconciliation resumes).</summary>
    Duplicate,

    /// <summary>Not valid BAI2, a control total mismatch, or a different file reusing the bank's file id.</summary>
    Rejected,
}

public sealed class BankStatementIngestResult
{
    public required BankStatementIngestStatus Status { get; init; }

    /// <summary>The stored file record (without its content).</summary>
    public required BankStatementFile Statement { get; init; }

    public IReadOnlyList<BankTransaction> Transactions { get; init; } = [];

    public IReadOnlyList<string> Errors => Statement.Errors;

    /// <summary>True when the file's transactions can be reconciled (accepted now or earlier).</summary>
    public bool IsAccepted => Statement.Status == BankStatementStatus.Accepted;

    /// <summary>The file was replaced by a corrected one (approved supersession): nothing in it is reconciled any more.</summary>
    public bool IsSuperseded { get; init; }
}

/// <summary>
/// Receives a BAI2 file: stores it append-only (encrypted), parses it, checks
/// every control total and stores its transactions. Idempotent by file hash
/// and by the bank's file id.
/// </summary>
public sealed class BankStatementIngestion
{
    private readonly IBankReconciliationStore _store;
    private readonly IFieldProtector _protector;
    private readonly BankReconciliationOptions _options;
    private readonly ILogger<BankStatementIngestion> _logger;
    private readonly TimeProvider _clock;

    public BankStatementIngestion(
        IBankReconciliationStore store,
        IFieldProtector protector,
        IOptions<BankReconciliationOptions> options,
        ILogger<BankStatementIngestion> logger,
        TimeProvider? clock = null)
    {
        _store = store;
        _protector = protector;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public const string ContentField = "bai2Content";

    public async Task<BankStatementIngestResult> IngestAsync(string tenantId, byte[] bytes, string? fileName, string? receivedBy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var id = BankStatementFile.IdFor(tenantId, sha);

        // Same bytes again: report the stored outcome. Its transactions are
        // returned so reconciliation resumes where an earlier run stopped.
        if (await _store.GetFileAsync(tenantId, id, ct) is { } existing)
            return await DuplicateAsync(existing, ct);

        var content = System.Text.Encoding.ASCII.GetString(bytes);
        var record = new BankStatementFile
        {
            Id = id,
            TenantId = tenantId,
            Sha256 = sha,
            FileName = CleanStoredText(fileName),
            ByteSize = bytes.LongLength,
            ReceivedByService = _options.ServiceName,
            ReceivedBy = receivedBy,
            ReceivedAt = _clock.GetUtcNow().UtcDateTime,
            ProtectedContent = _protector.Protect(content, new FieldProtectionContext(tenantId, id, ContentField)) ?? string.Empty,
        };

        Bai2File? parsed = null;
        try
        {
            parsed = Bai2Parser.Parse(content, _options.ParseOptions());
        }
        catch (Bai2FormatException ex)
        {
            record.Status = BankStatementStatus.Rejected;
            record.Errors = ex.Errors.Take(100).ToList();
        }

        List<BankTransaction> transactions = [];
        if (parsed is not null)
        {
            Describe(record, parsed);
            var earlier = await _store.ListAcceptedByBankFileKeyAsync(tenantId, parsed.BankFileKey, ct);
            var latest = earlier.LastOrDefault();
            var supersession = latest is null ? null : await _store.GetSupersessionAsync(tenantId, latest.Id, ct);
            if (latest is not null && supersession is not { Status: SupersessionStatus.Approved, DismissalsComplete: true })
            {
                // Kept for audit under its own id, not the file's hash id: once a
                // supersession is approved, the same corrected bytes can be sent again.
                record.Id = $"{id}-conflict-{record.ReceivedAt.Ticks}";
                record.ProtectedContent = _protector.Protect(content, new FieldProtectionContext(tenantId, record.Id, ContentField)) ?? string.Empty;
                record.Status = BankStatementStatus.Rejected;
                record.Errors = [$"The bank file id {CleanStoredText(parsed.FileId)} (sender {CleanStoredText(parsed.SenderId)}, created {parsed.CreationDate:yyyy-MM-dd}) was already used by a different file ({latest.Id}). Ask the bank for a new file id, or have two people supersede {latest.Id} (POST bank-statements/{latest.Id}/supersede, then /approve) before sending the corrected file."];
            }
            else
            {
                record.Status = BankStatementStatus.Accepted;
                record.BankFileKeyBase = parsed.BankFileKey;
                // The first file with a bank id keeps it; a corrected one after an approved supersession is #2, #3...
                record.BankFileKey = earlier.Count == 0 ? parsed.BankFileKey : $"{parsed.BankFileKey}#{earlier.Count + 1}";
                transactions = ToTransactions(tenantId, id, parsed);
                if (supersession is not null)
                {
                    supersession.ReplacedByStatementId = id;
                    await _store.TryUpdateSupersessionAsync(supersession, SupersessionStatus.Approved, ct);
                }
            }
        }

        if (!await _store.TryInsertFileAsync(record, ct))
        {
            // Lost a race with the same file (same id) or with another file of the same bank id.
            if (await _store.GetFileAsync(tenantId, id, ct) is { } raced)
                return await DuplicateAsync(raced, ct);
            record.Status = BankStatementStatus.Rejected;
            record.BankFileKey = null;
            record.BankFileKeyBase = null;
            record.Errors = ["A different file with the same bank file id was received at the same time."];
            transactions = [];
            if (!await _store.TryInsertFileAsync(record, ct) && await _store.GetFileAsync(tenantId, id, ct) is { } again)
                return await DuplicateAsync(again, ct);
        }

        if (record.Status == BankStatementStatus.Accepted)
            await _store.InsertTransactionsAsync(transactions, ct);

        _logger.Log(record.Status == BankStatementStatus.Accepted ? LogLevel.Information : LogLevel.Warning,
            "BAI2 file {StatementId} for tenant {TenantId}: {Status}, {Groups} group(s), {Accounts} account(s), {Transactions} transaction(s), {ErrorCount} error(s)",
            LogSanitizer.Sanitize(id), LogSanitizer.Sanitize(tenantId), record.Status, record.GroupCount, record.AccountCount, record.TransactionCount, record.Errors.Count);

        return new BankStatementIngestResult
        {
            Status = record.Status == BankStatementStatus.Accepted ? BankStatementIngestStatus.Ingested : BankStatementIngestStatus.Rejected,
            Statement = WithoutContent(record),
            Transactions = transactions,
        };
    }

    private async Task<BankStatementIngestResult> DuplicateAsync(BankStatementFile existing, CancellationToken ct)
    {
        // Frozen as soon as a supersession is requested, not only once approved:
        // nothing on the statement may be decided while it is being replaced.
        var superseded = existing.Status == BankStatementStatus.Accepted
                         && (await _store.GetSupersessionAsync(existing.TenantId, existing.Id, ct))?.Freezes == true;
        return await DuplicateResultAsync(existing, superseded, ct);
    }

    private async Task<BankStatementIngestResult> DuplicateResultAsync(BankStatementFile existing, bool superseded, CancellationToken ct) => new()
    {
        Status = BankStatementIngestStatus.Duplicate,
        Statement = WithoutContent(existing),
        IsSuperseded = superseded,
        Transactions = existing.Status == BankStatementStatus.Accepted && !superseded
            ? await _store.GetTransactionsAsync(existing.TenantId, existing.Id, ct)
            : [],
    };

    private static void Describe(BankStatementFile record, Bai2File parsed)
    {
        record.SenderId = parsed.SenderId;
        record.ReceiverId = parsed.ReceiverId;
        record.CreationDate = parsed.CreationDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        record.FileIdNumber = parsed.FileId;
        record.GroupCount = parsed.Groups.Count;
        record.AccountCount = parsed.Groups.Sum(g => g.Accounts.Count);
        record.TransactionCount = parsed.AllTransactions.Count();
        record.ControlTotal = parsed.ControlTotal;
        record.MaskedAccounts = parsed.Groups.SelectMany(g => g.Accounts).Select(a => a.MaskedAccountNumber).Distinct().ToList();
    }

    private static List<BankTransaction> ToTransactions(string tenantId, string statementId, Bai2File parsed)
    {
        var list = new List<BankTransaction>();
        for (var g = 0; g < parsed.Groups.Count; g++)
        {
            var group = parsed.Groups[g];
            for (var a = 0; a < group.Accounts.Count; a++)
            {
                var account = group.Accounts[a];
                var suffix = account.AccountNumber.Trim() is { Length: >= 4 } n ? n[^4..] : account.AccountNumber.Trim();
                foreach (var t in account.Transactions)
                {
                    list.Add(new BankTransaction
                    {
                        Id = $"{statementId}-{g}-{a}-{t.Index}",
                        TenantId = tenantId,
                        StatementId = statementId,
                        LineNumber = t.LineNumber,
                        GroupStatus = group.GroupStatus,
                        AsOfDate = group.AsOfDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                        AsOfDateModifier = group.AsOfDateModifier,
                        Currency = account.Currency,
                        MaskedAccount = account.MaskedAccountNumber,
                        AccountSuffix = suffix,
                        TypeCode = t.TypeCode,
                        Direction = t.Direction,
                        AmountMinor = t.Amount,
                        Amount = Bai2Parser.ToDecimal(t.Amount, account.Currency),
                        FundsType = t.Availability.FundsType,
                        ValueDate = t.Availability.ValueDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                        BankReference = t.BankReference,
                        CustomerReference = t.CustomerReference,
                        Text = t.Text,
                    });
                }
            }
        }
        return list;
    }

    private static BankStatementFile WithoutContent(BankStatementFile f) => f.WithoutContent();

    /// <summary>
    /// A bank-file value made safe to store and show: control characters
    /// removed, length capped at 128. For log lines use
    /// <see cref="LogSanitizer.Sanitize(string?, int)"/>.
    /// </summary>
    private static string CleanStoredText(string? value) =>
        value is null ? string.Empty : new string(value.Where(c => !char.IsControl(c)).Take(128).ToArray());
}
