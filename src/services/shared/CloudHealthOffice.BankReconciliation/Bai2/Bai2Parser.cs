using System.Globalization;

namespace CloudHealthOffice.BankReconciliation.Bai2;

/// <summary>
/// Parses a BAI2 (Cash Management Balance Reporting Specification, version 2)
/// file: 01 file header, 02 group header, 03 account identifier and summary,
/// 16 transaction detail, 88 continuation, 49 account trailer, 98 group
/// trailer, 99 file trailer.
///
/// <para>
/// Fields are comma-separated and a record ends at <c>/</c>, except the 16
/// text field, which runs to the end of the record and may hold commas and
/// slashes. An 88 continues the record before it: a 16's text continues with
/// a space between the pieces; any other record continues with its next field.
/// Amounts carry no decimal point; the currency's minor units are implied
/// (2 for USD).
/// </para>
///
/// <para>
/// Every control total and record count (49 account, 98 group, 99 file) is
/// checked, along with the record order. Any mismatch rejects the whole file
/// with <see cref="Bai2FormatException"/>, so a file is reconciled completely
/// or not at all.
/// </para>
/// </summary>
public static class Bai2Parser
{
    private sealed record Physical(int Line, string Code, string Body);

    private sealed class Logical(Physical first)
    {
        public Physical First { get; } = first;
        public List<Physical> Continuations { get; } = [];
        public int PhysicalCount => 1 + Continuations.Count;
        public string Code => First.Code;
        public int Line => First.Line;
    }

    public static Bai2File Parse(string content) => Parse(content, null);

    /// <param name="options">Bank-specific type codes (e.g. 9xx custom codes) that are credits or debits.</param>
    public static Bai2File Parse(string content, Bai2ParseOptions? options)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new Bai2FormatException(["The file is empty."]);

        var physical = SplitPhysical(content.TrimStart('﻿'));
        var errors = new List<string>();
        var records = Assemble(physical, errors);
        if (errors.Count > 0) throw new Bai2FormatException(errors);

        var file = Build(records, errors);
        if (errors.Count > 0 || file is null) throw new Bai2FormatException(errors.Count > 0 ? errors : ["The file is not BAI2."]);
        if (options is not null)
            foreach (var t in file.AllTransactions)
                t.Direction = Bai2TypeCodes.DirectionOf(t.TypeCode, options);
        return file;
    }

    // ── Physical records ──────────────────────────────────────────────

    private static List<Physical> SplitPhysical(string content)
    {
        IEnumerable<string> lines;
        if (content.Contains('\n') || content.Contains('\r'))
        {
            lines = content.Split('\n').Select(l => l.TrimEnd('\r'));
        }
        else
        {
            // No line breaks: fixed-length physical records (01 field 7), else
            // one record per '/' that is followed by the next record's code.
            var length = FixedRecordLength(content);
            lines = length > 0
                ? Enumerable.Range(0, (content.Length + length - 1) / length).Select(i => content.Substring(i * length, Math.Min(length, content.Length - i * length)))
                : SplitOnRecordDelimiter(content);
        }

        var result = new List<Physical>();
        var lineNumber = 0;
        foreach (var raw in lines)
        {
            lineNumber++;
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var comma = line.IndexOf(',');
            var code = comma < 0 ? line.TrimEnd('/') : line[..comma];
            var body = comma < 0 ? string.Empty : line[(comma + 1)..];
            result.Add(new Physical(lineNumber, code.Trim(), body));
        }
        return result;
    }

    private static readonly System.Text.RegularExpressions.Regex RecordStart =
        new(@"/(?=(01|02|03|16|88|49|98|99),)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits a file with neither line breaks nor a record length at each '/'
    /// that ends a record: one followed by a record code and a comma. A 16
    /// text may contain '/', but not that sequence.
    /// </summary>
    private static IEnumerable<string> SplitOnRecordDelimiter(string content)
    {
        var start = 0;
        foreach (System.Text.RegularExpressions.Match m in RecordStart.Matches(content))
        {
            yield return content[start..(m.Index + 1)];
            start = m.Index + 1;
        }
        yield return content[start..];
    }

    private static int FixedRecordLength(string content)
    {
        if (!content.StartsWith("01,", StringComparison.Ordinal)) return 0;
        var fields = content.Split(',', 9);
        return fields.Length > 6 && int.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is > 0 and <= 2000
            ? n
            : 0;
    }

    private static List<Logical> Assemble(List<Physical> physical, List<string> errors)
    {
        var records = new List<Logical>();
        foreach (var p in physical)
        {
            if (p.Code == "88")
            {
                if (records.Count == 0)
                    errors.Add($"Line {p.Line}: an 88 continuation record has no record to continue.");
                else
                    records[^1].Continuations.Add(p);
                continue;
            }
            if (p.Code is not ("01" or "02" or "03" or "16" or "49" or "98" or "99"))
            {
                errors.Add($"Line {p.Line}: unknown record code '{Truncate(p.Code)}'.");
                continue;
            }
            records.Add(new Logical(p));
        }
        return records;
    }

    // ── Structure ─────────────────────────────────────────────────────

    private static Bai2File? Build(List<Logical> records, List<string> errors)
    {
        if (records.Count == 0 || records[0].Code != "01")
        {
            errors.Add("The file must start with an 01 file header record.");
            return null;
        }

        var i = 0;
        var header = Fields(records[i++]);
        var version = header.At(7);
        if (version != "2")
            errors.Add($"Line {records[0].Line}: 01 version number must be 2 (BAI2), found '{Truncate(version)}'.");
        var creationDate = Date(header.At(2), records[0].Line, "01 creation date", errors);

        var groups = new List<Bai2Group>();
        var physicalTotal = records[0].PhysicalCount;
        long sumOfGroups = 0;

        while (i < records.Count && records[i].Code == "02")
        {
            var groupStart = i;
            var g = Fields(records[i++]);
            var currency = Currency(g.At(5));
            var groupPhysical = records[groupStart].PhysicalCount;
            var accounts = new List<Bai2Account>();
            long sumOfAccounts = 0;

            while (i < records.Count && records[i].Code == "03")
            {
                var account = ReadAccount(records, ref i, currency, errors, out var accountPhysical, out var accountTotal);
                if (account is null) return null;
                accounts.Add(account);
                sumOfAccounts += accountTotal;
                groupPhysical += accountPhysical;
            }

            if (i >= records.Count || records[i].Code != "98")
            {
                errors.Add($"Line {LineOf(records, i)}: expected a 98 group trailer, found {Describe(records, i)}.");
                return null;
            }
            var trailerRecord = records[i++];
            groupPhysical += trailerRecord.PhysicalCount;
            var trailer = Fields(trailerRecord);
            var groupTotal = SignedAmount(trailer.At(0), trailerRecord.Line, "98 group control total", errors) ?? 0;
            var accountCount = Int(trailer.At(1), trailerRecord.Line, "98 number of accounts", errors);
            var groupRecords = Int(trailer.At(2), trailerRecord.Line, "98 number of records", errors);

            if (groupTotal != sumOfAccounts)
                errors.Add($"Line {trailerRecord.Line}: 98 group control total {groupTotal} does not equal the sum of its account control totals {sumOfAccounts}.");
            if (accountCount is { } ac && ac != accounts.Count)
                errors.Add($"Line {trailerRecord.Line}: 98 number of accounts {ac} does not equal the {accounts.Count} 03 record(s) in the group.");
            if (groupRecords is { } gr && gr != groupPhysical)
                errors.Add($"Line {trailerRecord.Line}: 98 number of records {gr} does not equal the {groupPhysical} record(s) from 02 to 98.");

            groups.Add(new Bai2Group
            {
                UltimateReceiverId = g.At(0),
                OriginatorId = g.At(1),
                GroupStatus = g.At(2) is { Length: > 0 } s ? s : "1",
                AsOfDate = Date(g.At(3), records[groupStart].Line, "02 as-of date", errors),
                AsOfTime = NullIfEmpty(g.At(4)),
                Currency = currency,
                AsOfDateModifier = NullIfEmpty(g.At(6)),
                Accounts = accounts,
                ControlTotal = groupTotal,
                RecordCount = groupPhysical,
            });
            sumOfGroups += groupTotal;
            physicalTotal += groupPhysical;
        }

        if (i >= records.Count || records[i].Code != "99")
        {
            errors.Add($"Line {LineOf(records, i)}: expected a 99 file trailer, found {Describe(records, i)}.");
            return null;
        }
        var fileTrailerRecord = records[i++];
        physicalTotal += fileTrailerRecord.PhysicalCount;
        if (i < records.Count)
            errors.Add($"Line {records[i].Line}: records follow the 99 file trailer.");

        var ft = Fields(fileTrailerRecord);
        var fileTotal = SignedAmount(ft.At(0), fileTrailerRecord.Line, "99 file control total", errors) ?? 0;
        var groupCount = Int(ft.At(1), fileTrailerRecord.Line, "99 number of groups", errors);
        var fileRecords = Int(ft.At(2), fileTrailerRecord.Line, "99 number of records", errors);
        if (fileTotal != sumOfGroups)
            errors.Add($"Line {fileTrailerRecord.Line}: 99 file control total {fileTotal} does not equal the sum of its group control totals {sumOfGroups}.");
        if (groupCount is { } gc && gc != groups.Count)
            errors.Add($"Line {fileTrailerRecord.Line}: 99 number of groups {gc} does not equal the {groups.Count} 02 record(s).");
        if (fileRecords is { } fr && fr != physicalTotal)
            errors.Add($"Line {fileTrailerRecord.Line}: 99 number of records {fr} does not equal the {physicalTotal} record(s) in the file.");
        if (string.IsNullOrWhiteSpace(header.At(0)) || string.IsNullOrWhiteSpace(header.At(4)))
            errors.Add($"Line {records[0].Line}: 01 sender identification and file identification number are required.");

        return new Bai2File
        {
            SenderId = header.At(0),
            ReceiverId = header.At(1),
            CreationDate = creationDate,
            CreationTime = header.At(3),
            FileId = header.At(4),
            Version = version,
            Groups = groups,
            ControlTotal = fileTotal,
            RecordCount = physicalTotal,
        };
    }

    private static Bai2Account? ReadAccount(List<Logical> records, ref int i, string groupCurrency, List<string> errors,
        out int physicalCount, out long controlTotal)
    {
        var accountRecord = records[i++];
        physicalCount = accountRecord.PhysicalCount;
        controlTotal = 0;
        var f = Fields(accountRecord);
        var accountNumber = f.At(0);
        if (string.IsNullOrWhiteSpace(accountNumber))
            errors.Add($"Line {accountRecord.Line}: 03 customer account number is required.");
        var currency = f.At(1) is { Length: > 0 } c ? Currency(c) : groupCurrency;

        long sum = 0;
        var summaries = new List<Bai2Summary>();
        var reader = new FieldReader(f.Values, 2);
        while (reader.HasMore)
        {
            var typeCode = reader.Next();
            if (typeCode.Length == 0 && !reader.HasMore) break;
            var amount = SignedAmount(reader.Next(), accountRecord.Line, $"03 amount for type {Truncate(typeCode)}", errors);
            var itemCount = Int(reader.Next(), accountRecord.Line, "03 item count", errors);
            var availability = ReadAvailability(reader, accountRecord.Line, errors);
            if (typeCode.Length == 0 && amount is null) continue;
            summaries.Add(new Bai2Summary { TypeCode = typeCode, Amount = amount, ItemCount = itemCount, Availability = availability });
            sum += amount ?? 0;
        }

        var transactions = new List<Bai2Transaction>();
        while (i < records.Count && records[i].Code == "16")
        {
            var t = records[i++];
            physicalCount += t.PhysicalCount;
            var transaction = ReadTransaction(t, transactions.Count, errors);
            if (transaction is not null)
            {
                transactions.Add(transaction);
                sum += transaction.Amount;
            }
        }

        if (i >= records.Count || records[i].Code != "49")
        {
            errors.Add($"Line {LineOf(records, i)}: expected a 49 account trailer, found {Describe(records, i)}.");
            return null;
        }
        var trailerRecord = records[i++];
        physicalCount += trailerRecord.PhysicalCount;
        var trailer = Fields(trailerRecord);
        controlTotal = SignedAmount(trailer.At(0), trailerRecord.Line, "49 account control total", errors) ?? 0;
        var recordCount = Int(trailer.At(1), trailerRecord.Line, "49 number of records", errors);
        if (controlTotal != sum)
            errors.Add($"Line {trailerRecord.Line}: 49 account control total {controlTotal} does not equal the sum of the account's 03 and 16 amounts {sum} (account {AccountMask.Mask(accountNumber)}).");
        if (recordCount is { } rc && rc != physicalCount)
            errors.Add($"Line {trailerRecord.Line}: 49 number of records {rc} does not equal the {physicalCount} record(s) from 03 to 49 (account {AccountMask.Mask(accountNumber)}).");

        return new Bai2Account
        {
            AccountNumber = accountNumber,
            Currency = currency,
            Summaries = summaries,
            Transactions = transactions,
            ControlTotal = controlTotal,
            RecordCount = physicalCount,
        };
    }

    private static Bai2Transaction? ReadTransaction(Logical record, int index, List<string> errors)
    {
        // Parse the first physical record; if it reaches the text field, 88s add to the text.
        var parsed = ReadTransactionFields(record.First.Body, out var reachedText);
        if (!reachedText && record.Continuations.Count > 0)
        {
            // Fields themselves spill into the 88s: rejoin them as one record.
            var joined = string.Join(",", new[] { TrimRecordEnd(record.First.Body) }
                .Concat(record.Continuations.Select(c => c.Body)));
            parsed = ReadTransactionFields(joined, out _);
        }
        else if (record.Continuations.Count > 0)
        {
            var pieces = new[] { parsed.Text ?? string.Empty }
                .Concat(record.Continuations.Select(c => TrimTextEnd(c.Body)))
                .Where(s => s.Length > 0);
            parsed = parsed with { Text = string.Join(" ", pieces) };
        }

        var line = record.Line;
        if (parsed.TypeCode.Length != 3 || !parsed.TypeCode.All(char.IsAsciiDigit))
            errors.Add($"Line {line}: 16 type code '{Truncate(parsed.TypeCode)}' is not a 3-digit code.");
        var amount = UnsignedAmount(parsed.Amount, line, "16 amount", errors);

        var availabilityErrors = new List<string>();
        var availability = ReadAvailability(new FieldReader(parsed.AvailabilityFields, 0), line, availabilityErrors, parsed.FundsType);
        errors.AddRange(availabilityErrors);

        return new Bai2Transaction
        {
            LineNumber = line,
            Index = index,
            TypeCode = parsed.TypeCode,
            Direction = Bai2TypeCodes.DirectionOf(parsed.TypeCode),
            Amount = amount ?? 0,
            Availability = availability,
            BankReference = NullIfEmpty(parsed.BankReference),
            CustomerReference = NullIfEmpty(parsed.CustomerReference),
            Text = NullIfEmpty(parsed.Text?.Trim()),
        };
    }

    private sealed record TransactionFields(
        string TypeCode, string Amount, string FundsType, string[] AvailabilityFields,
        string BankReference, string CustomerReference, string? Text);

    /// <summary>Reads 16 fields up to the text, which is the rest of the record verbatim.</summary>
    private static TransactionFields ReadTransactionFields(string body, out bool reachedText)
    {
        var pos = 0;
        var ended = false;
        var exhausted = false;

        string Next()
        {
            if (ended || pos > body.Length) { exhausted = true; return string.Empty; }
            var start = pos;
            while (pos < body.Length && body[pos] != ',' && body[pos] != '/') pos++;
            var value = body[start..pos];
            if (pos < body.Length && body[pos] == '/') ended = true;
            if (pos >= body.Length) exhausted = true;
            pos++;
            return value.Trim();
        }

        var typeCode = Next();
        var amount = Next();
        var fundsType = Next();
        var availability = new List<string>();
        switch (fundsType.ToUpperInvariant())
        {
            case "S": availability.AddRange([Next(), Next(), Next()]); break;
            case "V": availability.AddRange([Next(), Next()]); break;
            case "D":
                var count = Next();
                availability.Add(count);
                if (int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    for (var k = 0; k < Math.Min(n, 50); k++) availability.AddRange([Next(), Next()]);
                break;
        }
        var bankRef = Next();
        var customerRef = Next();
        // The text starts only after a comma that ends the customer reference
        // inside this record; a record that ran out earlier continues its
        // fields (not its text) in the next 88.
        reachedText = !ended && !exhausted;
        var text = !ended && pos <= body.Length ? TrimTextEnd(body[Math.Min(pos, body.Length)..]) : null;
        return new TransactionFields(typeCode, amount, fundsType, availability.ToArray(), bankRef, customerRef, text);
    }

    private static Bai2FundsAvailability ReadAvailability(FieldReader reader, int line, List<string> errors, string? fundsTypeOverride = null)
    {
        var fundsType = (fundsTypeOverride ?? reader.Next()).Trim().ToUpperInvariant();
        switch (fundsType)
        {
            case "" or "Z": return new Bai2FundsAvailability { FundsType = "Z" };
            case "0" or "1" or "2": return new Bai2FundsAvailability { FundsType = fundsType };
            case "S":
                return new Bai2FundsAvailability
                {
                    FundsType = "S",
                    ImmediateAmount = SignedAmount(reader.Next(), line, "funds type S immediate amount", errors),
                    OneDayAmount = SignedAmount(reader.Next(), line, "funds type S one-day amount", errors),
                    TwoOrMoreDayAmount = SignedAmount(reader.Next(), line, "funds type S two-or-more-day amount", errors),
                };
            case "V":
            {
                var date = reader.Next();
                var time = reader.Next();
                var errorsBefore = errors.Count;
                var valueDate = Date(date, line, "funds type V value date", errors);
                return new Bai2FundsAvailability
                {
                    FundsType = "V",
                    ValueDate = errors.Count == errorsBefore ? valueDate : null,
                    ValueTime = NullIfEmpty(time),
                };
            }
            case "D":
            {
                var count = Int(reader.Next(), line, "funds type D number of distributions", errors) ?? 0;
                var distributions = new List<Bai2Distribution>();
                for (var k = 0; k < count; k++)
                {
                    var days = Int(reader.Next(), line, "funds type D days", errors) ?? 0;
                    var amount = SignedAmount(reader.Next(), line, "funds type D amount", errors) ?? 0;
                    distributions.Add(new Bai2Distribution(days, amount));
                }
                return new Bai2FundsAvailability { FundsType = "D", Distributions = distributions };
            }
            default:
                errors.Add($"Line {line}: funds type '{Truncate(fundsType)}' is not 0, 1, 2, S, V, D or Z.");
                return new Bai2FundsAvailability { FundsType = "Z" };
        }
    }

    // ── Fields ────────────────────────────────────────────────────────

    private sealed class RecordFields(string[] values)
    {
        public string[] Values { get; } = values;
        public string At(int index) => index < Values.Length ? Values[index].Trim() : string.Empty;
    }

    private sealed class FieldReader(string[] values, int start)
    {
        private int _pos = start;
        public bool HasMore => _pos < values.Length;
        public string Next() => _pos < values.Length ? values[_pos++].Trim() : string.Empty;
    }

    /// <summary>The fields of a non-text record: everything before its '/', with 88 continuations appended.</summary>
    private static RecordFields Fields(Logical record)
    {
        var parts = new[] { record.First.Body }.Concat(record.Continuations.Select(c => c.Body)).Select(TrimRecordEnd);
        var joined = string.Join(",", parts.Where(p => p.Length > 0));
        return new RecordFields(joined.Length == 0 ? [] : joined.Split(','));
    }

    private static string TrimRecordEnd(string body)
    {
        var slash = body.IndexOf('/');
        var value = slash >= 0 ? body[..slash] : body;
        return value.TrimEnd();
    }

    private static string TrimTextEnd(string text)
    {
        var t = text.TrimEnd();
        return t.EndsWith('/') ? t[..^1].TrimEnd() : t;
    }

    private static long? SignedAmount(string value, int line, string what, List<string> errors)
    {
        value = value.Trim();
        if (value.Length == 0) return null;
        var negative = value[0] == '-';
        var digits = value[0] is '-' or '+' ? value[1..] : value;
        if (digits.Length == 0 || digits.Length > 18 || !digits.All(char.IsAsciiDigit))
        {
            errors.Add($"Line {line}: {what} '{Truncate(value)}' is not a whole number of minor units.");
            return null;
        }
        var n = long.Parse(digits, CultureInfo.InvariantCulture);
        return negative ? -n : n;
    }

    private static long? UnsignedAmount(string value, int line, string what, List<string> errors)
    {
        value = value.Trim();
        if (value.Length == 0 || value.Length > 18 || !value.All(char.IsAsciiDigit))
        {
            errors.Add($"Line {line}: {what} '{Truncate(value)}' must be an unsigned whole number of minor units.");
            return null;
        }
        return long.Parse(value, CultureInfo.InvariantCulture);
    }

    private static int? Int(string value, int line, string what, List<string> errors)
    {
        value = value.Trim();
        if (value.Length == 0) return null;
        if (value.Length > 9 || !value.All(char.IsAsciiDigit))
        {
            errors.Add($"Line {line}: {what} '{Truncate(value)}' is not a number.");
            return null;
        }
        return int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static DateOnly Date(string value, int line, string what, List<string> errors)
    {
        if (DateOnly.TryParseExact(value.Trim(), "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        errors.Add($"Line {line}: {what} '{Truncate(value)}' is not a valid YYMMDD date.");
        return default;
    }

    private static string Currency(string value) => value.Trim() is { Length: 3 } c ? c.ToUpperInvariant() : "USD";

    /// <summary>Minor units implied by a currency: 0 for JPY and the like, 2 otherwise.</summary>
    public static int MinorUnits(string currency) =>
        currency.ToUpperInvariant() is "JPY" or "KRW" or "CLP" or "ISK" or "VND" ? 0 : 2;

    /// <summary>A minor-unit amount as a decimal in the currency's units.</summary>
    public static decimal ToDecimal(long amount, string currency) =>
        MinorUnits(currency) == 0 ? amount : amount / 100m;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string? value) => value is null ? string.Empty : value.Length <= 12 ? value : value[..12];

    private static int LineOf(List<Logical> records, int i) => i < records.Count ? records[i].Line : records[^1].Line;

    private static string Describe(List<Logical> records, int i) => i < records.Count ? $"a {records[i].Code} record" : "the end of the file";
}
