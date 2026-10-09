using System.Globalization;
using ArService.Ledger;
using ArService.Models;
using MongoDB.Driver;

namespace ArLegacyPostingReconciliation;

/// <summary>
/// Lists every legacy cash posting (<see cref="CashPostingLedger.IsLegacy"/>) as one CSV row
/// per application that needs a decision. Read-only: it issues finds and nothing else.
/// </summary>
public sealed class LegacyPostingLister
{
    public const string CashPostings = "cash_postings";
    public const string ArBalances = "ar_balances";

    /// <summary>The value written for who applied a legacy posting: the old apply recorded no actor.</summary>
    public const string AppliedByNotRecorded = "NOT_RECORDED";

    public static readonly string[] Header =
    [
        "tenant_id", "posting_id", "posting_number", "receipt_date", "posting_status",
        "payer_type", "payer_reference_id", "payer_name",
        "posting_amount", "applied_amount", "unapplied_amount",
        "application_index", "ar_balance_id", "gl_account_id", "account_number", "balance_period",
        "amount_applied", "application_memo",
        "balance_found", "balance_closing_balance", "balance_manual_adjustment_credits", "balance_has_cash_entry",
        "posting_created_at", "posting_created_by", "posting_last_updated_at", "applied_by",
        // Filled in by finance:
        "decision", "reviewer", "reviewed_at", "ticket", "note"
    ];

    private readonly TenantDatabases _databases;

    public LegacyPostingLister(TenantDatabases databases) => _databases = databases;

    /// <summary>Rows for every legacy posting, optionally only for the given tenants.</summary>
    public async Task<List<string[]>> ListAsync(IReadOnlyCollection<string>? tenants = null)
    {
        var rows = new List<string[]>();
        foreach (var database in await _databases.AllAsync())
        {
            var postings = database.GetCollection<CashPosting>(CashPostings);
            var balances = database.GetCollection<ArBalance>(ArBalances);
            var f = Builders<CashPosting>.Filter;
            var filter = f.In(p => p.Status, new[] { CashPostingStatus.PartiallyApplied, CashPostingStatus.Applied });
            if (tenants is { Count: > 0 })
                filter &= f.In(p => p.TenantId, tenants);

            var found = await postings.Find(filter).ToListAsync();
            foreach (var posting in found
                         .Where(CashPostingLedger.IsLegacy)
                         .OrderBy(p => p.TenantId, StringComparer.Ordinal)
                         .ThenBy(p => p.ReceiptDate)
                         .ThenBy(p => p.PostingNumber, StringComparer.Ordinal))
            {
                foreach (var (application, index) in CashPostingLedger.LegacyApplications(posting))
                {
                    var balance = await balances.Find(b => b.Id == application.ArBalanceId && b.TenantId == posting.TenantId).FirstOrDefaultAsync();
                    rows.Add(Row(posting, application, index, balance));
                }
            }
        }
        return rows;
    }

    private static string[] Row(CashPosting posting, CashApplication application, int index, ArBalance? balance)
    {
        var entryId = CashPostingLedger.CreditEntryId(posting.Id, index);
        return
        [
            posting.TenantId, posting.Id, posting.PostingNumber, Date(posting.ReceiptDate), posting.Status.ToString(),
            posting.PayerType.ToString(), posting.PayerReferenceId, posting.PayerName ?? string.Empty,
            Money(posting.Amount), Money(posting.AppliedAmount), Money(posting.UnappliedAmount),
            index.ToString(CultureInfo.InvariantCulture), application.ArBalanceId, application.GlAccountId,
            balance?.AccountNumber ?? string.Empty, balance == null ? string.Empty : Date(balance.Period),
            Money(application.AmountApplied), application.Memo ?? string.Empty,
            balance == null ? "false" : "true",
            balance == null ? string.Empty : Money(balance.ClosingBalance),
            balance == null ? string.Empty : Money(balance.PostingEntries
                .Where(e => e.Source == ArPostingSource.ManualAdjustment).Sum(e => e.CreditAmount)),
            balance == null ? string.Empty : (balance.PostingEntries.Any(e => e.EntryId == entryId) ? "true" : "false"),
            Timestamp(posting.CreatedAt), posting.CreatedBy ?? string.Empty, Timestamp(posting.LastUpdatedAt),
            AppliedByNotRecorded,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty
        ];
    }

    public static async Task WriteCsvAsync(TextWriter writer, IEnumerable<string[]> rows)
    {
        await writer.WriteAsync(Csv.Line(Header) + "\n");
        foreach (var row in rows)
            await writer.WriteAsync(Csv.Line(row) + "\n");
    }

    public static string Money(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Date(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Timestamp(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
