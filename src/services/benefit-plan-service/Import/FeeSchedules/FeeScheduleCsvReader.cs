using System.Globalization;
using System.IO.Compression;
using CsvHelper;
using CsvHelper.Configuration;

namespace BenefitPlanService.Import.FeeSchedules;

/// <summary>A fee schedule file the importer cannot read (no code or rate column, empty, ...).</summary>
public sealed class FeeScheduleFormatException(string message) : Exception(message);

/// <summary>The columns of a fee schedule file the importer reads, by their position.</summary>
public sealed record FeeScheduleColumns(
    int HeaderRowNumber,
    IReadOnlyList<string> Header,
    int Code,
    int Rate,
    int? FacilityRate,
    int? Modifier,
    int? SecondModifier,
    int? RevenueCode,
    int? Description,
    int? EffectiveDate,
    int? EndDate,
    int? MultipleProcedureIndicator)
{
    /// <summary>Column name per role, for the import report.</summary>
    public IReadOnlyDictionary<string, string> Describe()
    {
        var d = new Dictionary<string, string>
        {
            ["procedureCode"] = Header[Code],
            ["rate"] = Header[Rate],
        };
        void Add(string role, int? i) { if (i is { } v) d[role] = Header[v]; }
        Add("facilityRate", FacilityRate);
        Add("modifier", Modifier);
        Add("secondModifier", SecondModifier);
        Add("revenueCode", RevenueCode);
        Add("description", Description);
        Add("effectiveDate", EffectiveDate);
        Add("endDate", EndDate);
        Add("multipleProcedureIndicator", MultipleProcedureIndicator);
        return d;
    }
}

/// <summary>Caller-named columns that override the header aliases.</summary>
public sealed record FeeScheduleColumnOverrides(
    string? Code = null,
    string? Rate = null,
    string? FacilityRate = null,
    string? Modifier = null,
    string? RevenueCode = null);

/// <summary>
/// Reads the fee schedule files state Medicaid agencies, CMS and plans
/// publish as CSV (or a .zip holding one). They share a shape (one row per
/// procedure code and modifier, a dollar amount, sometimes a facility amount
/// and effective dates) but not their column names or their title rows, so
/// the header is found by alias within the first rows of the file rather than
/// assumed to be row 1. Caller overrides win over the aliases.
/// </summary>
public static class FeeScheduleCsvReader
{
    /// <summary>How many leading rows may be titles, notes or blank before the header.</summary>
    public const int MaxHeaderSearchRows = 30;

    // Header aliases, compared after Normalize (lower case, letters and digits only).
    // Order is priority: the first alias present wins.
    internal static readonly string[] CodeAliases =
    [
        "procedurecode", "proccode", "hcpcscode", "hcpcs", "cpthcpcs", "cpthcpcscode", "hcpcscpt", "hcpcscptcode",
        "cptcode", "cpt", "cdtcode", "servicecode", "billingcode", "procedure", "code",
    ];

    internal static readonly string[] RateAliases =
    [
        "adjustedfee", "nonfacilityadjustedfee", "nonfacilityfee", "nonfacilityrate", "nonfacilityprice", "nonfacilityamount", "nonfacilityallowable", "nonfacility",
        "nonfacilitypricingamount", "medicaidfee", "medicaidrate", "maximumallowable", "maximumallowablefee", "maxallowable",
        "maximumfee", "maxfee", "feescheduleamount", "feeamount", "allowedamount", "allowable", "allowed",
        "officefee", "basicrate", "rate", "fee", "amount", "price", "paymentrate", "reimbursementrate", "reimbursement", "negotiatedrate",
    ];

    internal static readonly string[] FacilityRateAliases =
        ["facilityadjustedfee", "facilityfee", "facilityrate", "facilityprice", "facilityamount", "facilityallowable", "facilitypricingamount", "facility"];

    internal static readonly string[] ModifierAliases =
        ["modifier", "modifier1", "mod", "mod1", "proceduremodifier", "procmod", "procmodifier"];

    internal static readonly string[] SecondModifierAliases = ["modifier2", "mod2"];

    internal static readonly string[] RevenueCodeAliases = ["revenuecode", "revcode", "revenue"];

    internal static readonly string[] DescriptionAliases =
        ["description", "shortdescription", "procedurecodedescription", "proceduredescription", "longdescription", "desc"];

    internal static readonly string[] EffectiveDateAliases =
        ["effectivedate", "rateeffectivedate", "effdate", "effective", "begindate", "startdate", "fromdate"];

    internal static readonly string[] EndDateAliases =
        ["enddate", "rateenddate", "termdate", "terminationdate", "expirationdate", "thrudate", "throughdate", "todate"];

    internal static readonly string[] MultipleProcedureAliases =
        ["multproc", "multipleprocedure", "multipleprocedureindicator", "multprocind", "mpi"];

    /// <summary>Lower case, letters and digits only: "Non-Facility Fee" → "nonfacilityfee".</summary>
    public static string Normalize(string header) =>
        new(header.Trim().TrimStart('﻿').ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Opens the upload as CSV; a zip (by name or "PK" signature) yields its first .csv entry.</summary>
    public static Stream OpenCsv(Stream content, string? fileName)
    {
        var seekable = content;
        if (!content.CanSeek)
        {
            var copy = new MemoryStream();
            content.CopyTo(copy);
            copy.Position = 0;
            seekable = copy;
        }

        var isZip = fileName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true;
        if (!isZip && seekable.Length >= 2)
        {
            var b0 = seekable.ReadByte();
            var b1 = seekable.ReadByte();
            seekable.Position = 0;
            isZip = b0 == 'P' && b1 == 'K';
        }
        if (!isZip) return seekable;

        var archive = new ZipArchive(seekable, ZipArchiveMode.Read, leaveOpen: false);
        // A .csv first; otherwise the largest .txt (DHCS's Medi-Cal zip holds rates_data.txt and the
        // much smaller conv_data.txt).
        var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            ?? archive.Entries.Where(e => e.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Length).FirstOrDefault()
            ?? throw new FeeScheduleFormatException(
                $"The zip '{fileName}' has no .csv or .txt file in it. Save an Excel fee schedule as CSV first.");
        // Copied out so the reader can look at the start of the file to tell tab from comma.
        var copyOut = new MemoryStream();
        using (var entryStream = entry.Open()) entryStream.CopyTo(copyOut);
        archive.Dispose();
        copyOut.Position = 0;
        return copyOut;
    }

    /// <summary>
    /// Finds the header among the first <see cref="MaxHeaderSearchRows"/> rows, then yields
    /// every later non-blank row with its 1-based row number. <paramref name="columns"/> is
    /// set before the first data row is yielded.
    /// </summary>
    public static IEnumerable<(int RowNumber, string[] Fields)> Read(
        Stream csv, FeeScheduleColumnOverrides overrides, Action<FeeScheduleColumns> columns)
    {
        var tabs = IsTabDelimited(csv);
        using var reader = new StreamReader(csv, detectEncodingFromByteOrderMarks: true);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = tabs ? "\t" : ",",
            Mode = tabs ? CsvMode.NoEscape : CsvMode.RFC4180,
            HasHeaderRecord = false,
            BadDataFound = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.None,
            DetectColumnCountChanges = false,
        };
        using var parser = new CsvParser(reader, config);

        FeeScheduleColumns? found = null;
        var rowNumber = 0;
        var searched = 0;
        var searchedRows = new List<(int Row, string[] Fields)>();
        while (found is null && searched < MaxHeaderSearchRows && parser.Read())
        {
            searched++;
            rowNumber = parser.Row; // the file's line number (blank lines are not records)
            var record = parser.Record ?? [];
            searchedRows.Add((rowNumber, record));
            found = TryResolve(record, rowNumber, overrides);
        }

        // DHCS's Medi-Cal rates_data.txt has no header row: give it the columns of its Excel twin,
        // and read the rows the header search went past as data.
        var replay = new List<(int Row, string[] Fields)>();
        if (found is null && tabs && searchedRows.Any(r => IsMediCalRatesRow(r.Fields)))
        {
            found = TryResolve(MediCalRatesHeader, 0, overrides);
            replay = searchedRows;
        }

        if (found is null)
        {
            throw new FeeScheduleFormatException(searched == 0
                ? "The fee schedule file is empty."
                : $"No header row with a procedure code column and a rate column was found in the first {MaxHeaderSearchRows} rows. " +
                  "Name them with codeColumn and rateColumn (for example codeColumn=\"Proc Code\"&rateColumn=\"Max Fee\").");
        }

        columns(found);

        foreach (var (row, record) in replay)
        {
            if (record.All(string.IsNullOrWhiteSpace)) continue;
            yield return (row, record);
        }

        while (parser.Read())
        {
            rowNumber = parser.Row;
            var record = parser.Record;
            if (record is null || record.All(string.IsNullOrWhiteSpace)) continue;
            yield return (rowNumber, record);
        }
    }

    /// <summary>The columns of DHCS's Medi-Cal Rates worksheet, which rates_data.txt has without a header.</summary>
    internal static readonly string[] MediCalRatesHeader =
    [
        "Proc Type", "Proc Code", "Procedure Description", "Unit Value", "Basic Rate", "Child Rate", "ER Rate",
        "Conv Ind", "ER Ind", "Cutback Ind", "Prof %", "Rental Rate", "Non-Physn Med Prac Ind",
    ];

    /// <summary>A rates_data.txt row: proc type, a 5-character code, a description, a unit value, then a $ basic rate.</summary>
    internal static bool IsMediCalRatesRow(string[] f) =>
        f.Length is >= 12 and <= 14
        && f[0].Trim().Length is >= 1 and <= 2
        && f[1].Trim().Length == 5
        && f[4].Trim().StartsWith('$');

    /// <summary>
    /// True when the start of the file has more tabs than commas outside quotes (a tab-delimited
    /// export such as DHCS's rates_data.txt). Only a seekable stream can be looked at; others are
    /// read as comma-separated.
    /// </summary>
    internal static bool IsTabDelimited(Stream csv)
    {
        if (!csv.CanSeek) return false;
        var start = csv.Position;
        var buffer = new byte[16 * 1024];
        var read = csv.Read(buffer, 0, buffer.Length);
        csv.Position = start;
        int tabs = 0, commas = 0;
        var quoted = false;
        for (var i = 0; i < read; i++)
        {
            switch ((char)buffer[i])
            {
                case '"': quoted = !quoted; break;
                case '\t' when !quoted: tabs++; break;
                case ',' when !quoted: commas++; break;
            }
        }
        return tabs > commas;
    }

    /// <summary>The columns of <paramref name="record"/> as a header, or null when it is not the header.</summary>
    internal static FeeScheduleColumns? TryResolve(string[] record, int rowNumber, FeeScheduleColumnOverrides overrides)
    {
        var header = record.Select(h => h.Trim().TrimStart('﻿')).ToArray();
        var normalized = header.Select(Normalize).ToArray();

        int? Find(string? overrideName, string[] aliases, params int?[] taken)
        {
            if (!string.IsNullOrWhiteSpace(overrideName))
            {
                var wanted = Normalize(overrideName);
                var i = Array.IndexOf(normalized, wanted);
                return i >= 0 ? i : null;
            }
            foreach (var alias in aliases)
            {
                for (var i = 0; i < normalized.Length; i++)
                {
                    if (normalized[i] == alias && !taken.Contains(i)) return i;
                }
            }
            return null;
        }

        var code = Find(overrides.Code, CodeAliases);
        if (code is null) return null;
        var facility = Find(overrides.FacilityRate, FacilityRateAliases, code);
        var rate = Find(overrides.Rate, RateAliases, code, facility);
        if (rate is null) return null;

        // A named column that is not in this row means this row is not the header.
        if (!string.IsNullOrWhiteSpace(overrides.FacilityRate) && facility is null) return null;

        var modifier = Find(overrides.Modifier, ModifierAliases, code, rate, facility);
        if (!string.IsNullOrWhiteSpace(overrides.Modifier) && modifier is null) return null;
        var revenue = Find(overrides.RevenueCode, RevenueCodeAliases, code, rate, facility, modifier);
        if (!string.IsNullOrWhiteSpace(overrides.RevenueCode) && revenue is null) return null;

        return new FeeScheduleColumns(
            HeaderRowNumber: rowNumber,
            Header: header,
            Code: code.Value,
            Rate: rate.Value,
            FacilityRate: facility,
            Modifier: modifier,
            SecondModifier: Find(null, SecondModifierAliases, code, rate, facility, modifier),
            RevenueCode: revenue,
            Description: Find(null, DescriptionAliases, code, rate, facility),
            EffectiveDate: Find(null, EffectiveDateAliases, code, rate, facility),
            EndDate: Find(null, EndDateAliases, code, rate, facility),
            MultipleProcedureIndicator: Find(null, MultipleProcedureAliases, code, rate, facility));
    }
}
