using System.Globalization;
using System.Text;
using PremiumBillingService.Models;

namespace PremiumBillingService.Edi;

/// <summary>
/// Parses a simple bank lockbox file (CSV) into one <see cref="RemittanceAdvice"/>
/// per check, for the same cash application as the 820.
///
/// The first row is a header naming these columns (any order, case-insensitive):
/// <c>batch, item, deposit_date, check_number, payer_id, payer_name, check_amount,
/// invoice_number, amount</c>. Each row is one remittance stub. Rows with the same
/// batch and item are one check; its check_amount must be the same on every row.
/// The trace number is <c>LBX-{batch}-{item}</c>, so re-uploading a file is refused
/// per check rather than posted twice. Dates are yyyy-MM-dd or MM/dd/yyyy.
/// </summary>
public static class LockboxCsvParser
{
    private static readonly string[] Required =
    {
        "batch", "item", "deposit_date", "check_number", "payer_id", "check_amount", "invoice_number", "amount"
    };

    public static IReadOnlyList<RemittanceAdvice> Parse(string content, string? sourceFileName = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new FormatException("The lockbox file is empty");

        var rows = ReadRows(content.TrimStart('﻿')).Where(r => r.Any(f => f.Length > 0)).ToList();
        if (rows.Count < 2)
            throw new FormatException("The lockbox file has no rows after the header");

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var missing = Required.Where(r => !header.Contains(r)).ToList();
        if (missing.Count > 0)
            throw new FormatException($"The lockbox header is missing: {string.Join(", ", missing)}");
        int Col(string name) => header.IndexOf(name);
        var payerNameCol = Col("payer_name");

        var checks = new Dictionary<(string Batch, string Item), RemittanceAdvice>();
        var order = new List<RemittanceAdvice>();
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var line = r + 1;
            string F(int col) => col >= 0 && col < row.Count ? row[col].Trim() : string.Empty;

            var batch = F(Col("batch"));
            var item = F(Col("item"));
            if (batch.Length == 0 || item.Length == 0)
                throw new FormatException($"Line {line}: batch and item are required");

            var checkAmount = Amount(F(Col("check_amount")), "check_amount", line);
            if (!checks.TryGetValue((batch, item), out var advice))
            {
                var payerId = F(Col("payer_id"));
                if (payerId.Length == 0)
                    throw new FormatException($"Line {line}: payer_id is required");
                advice = new RemittanceAdvice
                {
                    Source = RemittanceSource.Lockbox,
                    SourceFileName = sourceFileName,
                    TraceNumber = $"LBX-{batch}-{item}",
                    PayerId = payerId,
                    PayerName = payerNameCol >= 0 && F(payerNameCol).Length > 0 ? F(payerNameCol) : null,
                    PaymentMethod = "CHK",
                    CheckNumber = F(Col("check_number")) is { Length: > 0 } cn ? cn : null,
                    PaymentAmount = checkAmount,
                    PaymentDate = Date(F(Col("deposit_date")), line)
                };
                checks[(batch, item)] = advice;
                order.Add(advice);
            }
            else if (advice.PaymentAmount != checkAmount)
            {
                throw new FormatException($"Line {line}: check {batch}/{item} has check_amount {checkAmount} here and {advice.PaymentAmount} earlier");
            }

            var reference = F(Col("invoice_number"));
            advice.Items.Add(new RemittanceItem
            {
                LineNumber = advice.Items.Count + 1,
                Level = RemittanceLevel.Organization,
                ReferenceQualifier = "IK",
                Reference = reference.Length > 0 ? reference : null,
                Amount = Amount(F(Col("amount")), "amount", line)
            });
        }

        return order;
    }

    private static decimal Amount(string value, string column, int line)
    {
        var cleaned = value.Replace("$", string.Empty).Replace(",", string.Empty);
        if (!decimal.TryParse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
            throw new FormatException($"Line {line}: {column} '{value}' is not an amount");
        return amount;
    }

    private static DateTime Date(string value, int line)
    {
        if (DateTime.TryParseExact(value, new[] { "yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy", "yyyyMMdd" }, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
            return DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        throw new FormatException($"Line {line}: deposit_date '{value}' is not a date");
    }

    /// <summary>RFC 4180 rows: quoted fields may hold commas, quotes ("") and line breaks.</summary>
    private static IEnumerable<List<string>> ReadRows(string text)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    yield return row;
                    row = new List<string>();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }
        if (quoted)
            throw new FormatException("The lockbox file ends inside a quoted field");
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }
}
