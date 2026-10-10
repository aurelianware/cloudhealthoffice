using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ArService.Gl;

/// <summary>Control totals of an extract: an ERP load checks them before it accepts the file.</summary>
public sealed class GlExtractControl
{
    public string TenantId { get; init; } = string.Empty;
    public string Period { get; init; } = string.Empty;
    public int EntryCount { get; init; }
    public int LineCount { get; init; }
    public decimal TotalDebit { get; init; }
    public decimal TotalCredit { get; init; }

    /// <summary>SHA-256 (hex) of the CSV header and data rows (UTF-8, LF line ends): the same for the CSV and JSON forms.</summary>
    public string Sha256 { get; init; } = string.Empty;
}

/// <summary>One extract line (an entry line with its entry's fields).</summary>
public sealed class GlExtractRow
{
    public string EntryId { get; init; } = string.Empty;
    public string EntryDate { get; init; } = string.Empty;
    public string Period { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public string SourceReference { get; init; } = string.Empty;
    public string? ReversesEntryId { get; init; }
    public int LineNumber { get; init; }
    public string Role { get; init; } = string.Empty;
    public string AccountNumber { get; init; } = string.Empty;
    public string Debit { get; init; } = string.Empty;
    public string Credit { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
}

public sealed class GlExtract
{
    public GlExtractControl Control { get; init; } = new();
    public IReadOnlyList<GlExtractRow> Rows { get; init; } = Array.Empty<GlExtractRow>();

    /// <summary>Header, rows, then a <c>CONTROL</c> trailer row (not part of the hash).</summary>
    public string Csv { get; init; } = string.Empty;
}

/// <summary>
/// The GL extract of one period for the client's ERP: every journal entry line of the
/// period, deterministic (ordered by entry date, entry id, line number; invariant culture;
/// amounts with two decimals; no generation timestamp), with control totals. The same
/// journal always yields the same bytes and the same SHA-256.
/// </summary>
public static class GlExtractBuilder
{
    public const string Header = "entry_id,entry_date,period,kind,source_type,source_reference,reverses_entry_id,line_number,role,account_number,debit,credit,memo";

    public static GlExtract Build(string tenantId, string period, IEnumerable<GlJournalEntry> entries)
    {
        var ordered = entries
            .Where(e => e.TenantId == tenantId && e.Period == period)
            .OrderBy(e => e.EntryDate).ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
        var rows = ordered
            .SelectMany(e => e.Lines.OrderBy(l => l.LineNumber).Select(l => new GlExtractRow
            {
                EntryId = e.Id,
                EntryDate = e.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Period = e.Period,
                Kind = e.Kind.ToString(),
                SourceType = e.SourceType,
                SourceReference = e.SourceReference,
                ReversesEntryId = e.ReversesEntryId,
                LineNumber = l.LineNumber,
                Role = l.Role.ToString(),
                AccountNumber = l.AccountNumber,
                Debit = Money(l.Debit),
                Credit = Money(l.Credit),
                Memo = l.Memo ?? string.Empty,
            }))
            .ToList();

        var body = new StringBuilder();
        body.Append(Header).Append('\n');
        foreach (var r in rows)
        {
            body.Append(string.Join(",", new[]
            {
                Field(r.EntryId), r.EntryDate, r.Period, r.Kind, Field(r.SourceType), Field(r.SourceReference), Field(r.ReversesEntryId ?? string.Empty),
                r.LineNumber.ToString(CultureInfo.InvariantCulture), r.Role, Field(r.AccountNumber), r.Debit, r.Credit, Field(r.Memo),
            })).Append('\n');
        }
        var bodyText = body.ToString();
        var totalDebit = ordered.Sum(e => e.Lines.Sum(l => l.Debit));
        var totalCredit = ordered.Sum(e => e.Lines.Sum(l => l.Credit));
        var control = new GlExtractControl
        {
            TenantId = tenantId,
            Period = period,
            EntryCount = ordered.Count,
            LineCount = rows.Count,
            TotalDebit = totalDebit,
            TotalCredit = totalCredit,
            Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bodyText))).ToLowerInvariant(),
        };
        var csv = bodyText + string.Join(",",
            "CONTROL", period, control.EntryCount.ToString(CultureInfo.InvariantCulture), control.LineCount.ToString(CultureInfo.InvariantCulture),
            Money(totalDebit), Money(totalCredit), control.Sha256) + "\n";
        return new GlExtract { Control = control, Rows = rows, Csv = csv };
    }

    private static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>RFC 4180 quoting; a leading formula character is neutralised for spreadsheet safety.</summary>
    private static string Field(string value)
    {
        var v = value;
        if (v.Length > 0 && "=+-@".Contains(v[0]))
            v = "'" + v;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
