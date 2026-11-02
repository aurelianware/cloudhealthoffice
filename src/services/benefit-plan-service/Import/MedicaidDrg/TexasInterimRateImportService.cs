using System.Globalization;
using System.Text.Json.Serialization;
using BenefitPlanService.Import.FeeSchedules;
using CloudHealthOffice.FeeScheduleEngine.Medicaid;
using CsvHelper;
using CsvHelper.Configuration;

namespace BenefitPlanService.Import.MedicaidDrg;

/// <summary>A file that cannot be imported as it stands; nothing was written.</summary>
public sealed class InterimRateFormatException(string message) : Exception(message);

public sealed record InterimRateImportOptions
{
    /// <summary>
    /// The date the rates take effect, for a file with no effective date
    /// column. A row's own date wins.
    /// </summary>
    public DateTime? EffectiveDate { get; init; }

    /// <summary>Overwrite a stored rate for the same hospital and date whose values differ.</summary>
    public bool Replace { get; init; }

    public bool DryRun { get; init; } = true;
}

/// <summary>What happened to one hospital's row.</summary>
public sealed class InterimRateImportRow
{
    public int Row { get; init; }
    public string Npi { get; init; } = string.Empty;
    public string? StateProviderId { get; init; }
    public string? HospitalName { get; init; }
    public DateTime EffectiveDate { get; init; }
    public decimal? InpatientRate { get; init; }
    public decimal? OutpatientRate { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FeeScheduleImportOutcome Outcome { get; set; }
}

public sealed class InterimRateImportReport
{
    public bool DryRun { get; init; }
    public string FileName { get; init; } = string.Empty;

    /// <summary>Which column was read for each field, so a wrong match is visible.</summary>
    public Dictionary<string, string> Columns { get; init; } = [];
    public List<InterimRateImportRow> Rows { get; } = [];

    /// <summary>Things that did not stop the import but deserve a look, e.g. a rate above 100%.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>
    /// Hospitals left out because the file contradicts itself about them
    /// (HHSC RCC PDF: one NPI's TPIs carry different rates). Their stays pend.
    /// </summary>
    public List<TexasRccHeldBack> HeldBack { get; } = [];
}

public interface ITexasInterimRateImportService
{
    Task<InterimRateImportReport> ImportAsync(
        Stream content, string fileName, InterimRateImportOptions options, string tenantId, string actor, CancellationToken ct = default);
}

/// <summary>
/// Loads Texas hospital interim rates (each hospital's inpatient and
/// outpatient cost-to-charge ratio, set by TMHP). Takes HHSC's published
/// "Inpatient Ratio of Cost to Charges" PDF (see <see cref="TexasRccFileReader"/>),
/// or a CSV or Excel sheet with one row per hospital:
///
/// <list type="bullet">
/// <item><b>NPI</b> (required).</item>
/// <item><b>Inpatient</b> and/or <b>outpatient</b> interim rate: a fraction
/// (0.452) or a percentage (45.2%). At least one per row.</item>
/// <item><b>Effective date</b>, unless the request gives one for the whole file.</item>
/// <item>Optional TPI and hospital name, kept for the record.</item>
/// </list>
///
/// Every row is checked before anything is written: one bad row refuses
/// the file, because a mistyped ratio pays every outlier at that hospital
/// wrongly. Dry run by default.
/// </summary>
public sealed class TexasInterimRateImportService(IMedicaidInterimRateRepository repository) : ITexasInterimRateImportService
{
    private const string State = "TX";
    private const int MaxRows = 5_000;

    internal static readonly string[] NpiAliases = ["npi", "nationalprovideridentifier", "providernpi", "billingnpi"];
    internal static readonly string[] TpiAliases = ["tpi", "texasprovideridentifier", "texasprovideridentifiertpi", "providerid", "medicaidid"];
    internal static readonly string[] NameAliases = ["hospitalname", "hospital", "providername", "name", "facilityname"];
    internal static readonly string[] InpatientAliases =
        ["inpatientinterimrate", "inpatientrate", "inpatient", "ipinterimrate", "ipratio", "ipinterim", "inpatientccr", "inpatientratio"];
    internal static readonly string[] OutpatientAliases =
        ["outpatientinterimrate", "outpatientrate", "outpatient", "opinterimrate", "opratio", "opinterim", "outpatientccr", "outpatientratio"];
    internal static readonly string[] EffectiveAliases = ["effectivedate", "effective", "rateeffectivedate", "begindate", "startdate", "eff"];

    public async Task<InterimRateImportReport> ImportAsync(
        Stream content, string fileName, InterimRateImportOptions options, string tenantId, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var buffered = new MemoryStream();
        await content.CopyToAsync(buffered, ct);
        buffered.Position = 0;

        InterimRateImportReport report;
        List<ParsedRow> parsed;
        if (IsPdf(buffered))
        {
            // HHSC's published "Inpatient Ratio of Cost to Charges" PDF.
            var rcc = TexasRccFileReader.Read(buffered);
            parsed = rcc.Rates
                .Select(r => new ParsedRow(0, r.Npi, r.Tpi, r.ProviderName, r.RateEffectiveDate, r.Rate, null))
                .ToList();
            report = new InterimRateImportReport
            {
                DryRun = options.DryRun,
                FileName = fileName,
                Columns = new Dictionary<string, string>
                {
                    ["format"] = $"HHSC inpatient RCC PDF effective {rcc.FileEffectiveDate:yyyy-MM-dd}",
                    ["rows"] = rcc.Rows.Count.ToString(CultureInfo.InvariantCulture),
                },
            };
            report.HeldBack.AddRange(rcc.HeldBack);
            if (rcc.SerialDatesRepaired > 0)
                report.Warnings.Add($"{rcc.SerialDatesRepaired} row(s) printed the rate effective date as an Excel serial (e.g. 4626600%); read as dates.");
        }
        else
        {
            Stream csv;
            try
            {
                csv = FeeScheduleCsvReader.OpenCsv(buffered, fileName);
            }
            catch (FeeScheduleFormatException e)
            {
                throw new InterimRateFormatException(e.Message);
            }

            (var columns, parsed) = Parse(csv, options.EffectiveDate);
            report = new InterimRateImportReport { DryRun = options.DryRun, FileName = fileName, Columns = columns };
        }

        var now = DateTime.UtcNow;
        var records = parsed.Select(p => new MedicaidInterimRate
        {
            Id = MedicaidInterimRate.MakeId(tenantId, State, p.Npi, p.EffectiveDate),
            TenantId = tenantId,
            State = State,
            Npi = p.Npi,
            StateProviderId = p.Tpi,
            HospitalName = p.Name,
            EffectiveDate = p.EffectiveDate,
            InpatientRate = p.Inpatient,
            OutpatientRate = p.Outpatient,
            SourceFile = fileName,
            ImportedAt = now,
            ImportedBy = actor,
        }).ToList();

        foreach (var p in parsed.Where(p => p.Inpatient > 1m || p.Outpatient > 1m))
            report.Warnings.Add($"Row {p.Row}: NPI {p.Npi} has an interim rate above 100%; check it was not entered as a percentage without the % sign.");

        var stored = (await repository.GetByIdsAsync(tenantId, records.Select(r => r.Id).ToList(), ct))
            .ToDictionary(r => r.Id, StringComparer.Ordinal);

        var toWrite = new List<MedicaidInterimRate>();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var outcome = !stored.TryGetValue(record.Id, out var existing)
                ? (options.DryRun ? FeeScheduleImportOutcome.WouldWrite : FeeScheduleImportOutcome.Created)
                : existing.InpatientRate == record.InpatientRate && existing.OutpatientRate == record.OutpatientRate
                    ? FeeScheduleImportOutcome.Unchanged
                    : !options.Replace
                        ? FeeScheduleImportOutcome.Exists
                        : options.DryRun ? FeeScheduleImportOutcome.WouldWrite : FeeScheduleImportOutcome.Replaced;

            if (outcome is FeeScheduleImportOutcome.Created or FeeScheduleImportOutcome.Replaced)
                toWrite.Add(record);

            report.Rows.Add(new InterimRateImportRow
            {
                Row = parsed[i].Row,
                Npi = record.Npi,
                StateProviderId = record.StateProviderId,
                HospitalName = record.HospitalName,
                EffectiveDate = record.EffectiveDate,
                InpatientRate = record.InpatientRate,
                OutpatientRate = record.OutpatientRate,
                Outcome = outcome,
            });
        }

        if (!options.DryRun && toWrite.Count > 0)
            await repository.UpsertAsync(toWrite, ct);

        return report;
    }

    private static bool IsPdf(MemoryStream stream)
    {
        var head = stream.Length >= 4 ? stream.GetBuffer().AsSpan(0, 4) : [];
        return head.SequenceEqual("%PDF"u8);
    }

    internal sealed record ParsedRow(int Row, string Npi, string? Tpi, string? Name, DateTime EffectiveDate, decimal? Inpatient, decimal? Outpatient);

    /// <summary>Reads and validates every row. Throws <see cref="InterimRateFormatException"/> listing the problems.</summary>
    internal static (Dictionary<string, string> Columns, List<ParsedRow> Rows) Parse(Stream csv, DateTime? defaultEffective)
    {
        using var reader = new StreamReader(csv, detectEncodingFromByteOrderMarks: true);
        using var parser = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            BadDataFound = null,
            MissingFieldFound = null,
        });

        int? npiCol = null, tpiCol = null, nameCol = null, ipCol = null, opCol = null, effCol = null;
        string[] header = [];
        var rows = new List<ParsedRow>();
        var problems = new List<string>();
        var rowNumber = 0;

        while (parser.Read())
        {
            rowNumber++;
            var fields = Enumerable.Range(0, parser.Parser.Count).Select(i => parser.GetField(i) ?? string.Empty).ToArray();
            if (fields.All(f => f.Trim().Length == 0)) continue;

            if (npiCol is null)
            {
                var normalized = fields.Select(FeeScheduleCsvReader.Normalize).ToArray();
                npiCol = Find(normalized, NpiAliases);
                if (npiCol is null)
                {
                    if (rowNumber >= FeeScheduleCsvReader.MaxHeaderSearchRows)
                        throw new InterimRateFormatException($"No header row with an NPI column was found in the first {FeeScheduleCsvReader.MaxHeaderSearchRows} rows.");
                    continue;
                }
                header = fields;
                tpiCol = Find(normalized, TpiAliases);
                nameCol = Find(normalized, NameAliases);
                ipCol = Find(normalized, InpatientAliases);
                opCol = Find(normalized, OutpatientAliases);
                effCol = Find(normalized, EffectiveAliases);
                if (ipCol is null && opCol is null)
                    throw new InterimRateFormatException("The header has no inpatient or outpatient interim rate column.");
                if (effCol is null && defaultEffective is null)
                    throw new InterimRateFormatException("The file has no effective date column; give effectiveDate for the whole file.");
                continue;
            }

            string At(int? col) => col is { } c && c < fields.Length ? fields[c].Trim() : string.Empty;

            var npi = At(npiCol);
            if (!IsValidNpi(npi))
            {
                problems.Add($"Row {rowNumber}: NPI '{npi}' is not a valid NPI.");
                continue;
            }

            var ip = ParseRate(At(ipCol), rowNumber, "inpatient", problems);
            var op = ParseRate(At(opCol), rowNumber, "outpatient", problems);
            if (ip.Failed || op.Failed) continue;
            if (ip.Value is null && op.Value is null)
            {
                problems.Add($"Row {rowNumber}: NPI {npi} has no inpatient or outpatient rate.");
                continue;
            }

            DateTime effective;
            var effText = At(effCol);
            if (effText.Length > 0)
            {
                if (!TryParseDate(effText, out effective))
                {
                    problems.Add($"Row {rowNumber}: effective date '{effText}' is not a date.");
                    continue;
                }
            }
            else if (defaultEffective is { } d)
            {
                effective = d.Date;
            }
            else
            {
                problems.Add($"Row {rowNumber}: NPI {npi} has no effective date.");
                continue;
            }

            rows.Add(new ParsedRow(rowNumber, npi, NullIfEmpty(At(tpiCol)), NullIfEmpty(At(nameCol)), effective, ip.Value, op.Value));
        }

        if (npiCol is null)
            throw new InterimRateFormatException("The file is empty or has no header row with an NPI column.");

        foreach (var dup in rows.GroupBy(r => (r.Npi, r.EffectiveDate)).Where(g => g.Count() > 1))
        {
            var distinct = dup.Select(r => (r.Inpatient, r.Outpatient)).Distinct().Count();
            if (distinct > 1)
                problems.Add($"NPI {dup.Key.Npi} has {dup.Count()} rows effective {dup.Key.EffectiveDate:yyyy-MM-dd} with different rates (rows {string.Join(", ", dup.Select(r => r.Row))}).");
        }

        if (rows.Count > MaxRows)
            problems.Add($"The file has {rows.Count} rows; at most {MaxRows} per import.");
        if (rows.Count == 0 && problems.Count == 0)
            problems.Add("The file has a header but no rows.");

        if (problems.Count > 0)
        {
            var shown = problems.Take(20).ToList();
            if (problems.Count > shown.Count) shown.Add($"... and {problems.Count - shown.Count} more.");
            throw new InterimRateFormatException("Nothing was imported. " + string.Join(" ", shown));
        }

        var columns = new Dictionary<string, string> { ["npi"] = header[npiCol.Value] };
        if (tpiCol is { } t) columns["tpi"] = header[t];
        if (nameCol is { } n) columns["hospitalName"] = header[n];
        if (ipCol is { } i) columns["inpatientRate"] = header[i];
        if (opCol is { } o) columns["outpatientRate"] = header[o];
        if (effCol is { } e) columns["effectiveDate"] = header[e];

        // Identical duplicates collapse to one.
        return (columns, rows.DistinctBy(r => (r.Npi, r.EffectiveDate)).ToList());
    }

    private static int? Find(string[] normalized, string[] aliases)
    {
        foreach (var alias in aliases)
        {
            var index = Array.IndexOf(normalized, alias);
            if (index >= 0) return index;
        }
        return null;
    }

    /// <summary>
    /// "0.452" or "45.2%" → 0.452. A bare number above 2 is refused: it is
    /// almost certainly a percentage typed without the sign, and reading it
    /// as a ratio would multiply outliers by 45.
    /// </summary>
    private static (decimal? Value, bool Failed) ParseRate(string text, int row, string kind, List<string> problems)
    {
        if (text.Length == 0 || text is "-" or "N/A" or "n/a") return (null, false);

        var percent = text.EndsWith('%');
        var number = percent ? text[..^1].Trim() : text;
        if (!decimal.TryParse(number, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            problems.Add($"Row {row}: {kind} rate '{text}' is not a number.");
            return (null, true);
        }
        if (percent) value /= 100m;
        if (value <= 0m || value > 2m)
        {
            problems.Add(percent || value <= 0m
                ? $"Row {row}: {kind} rate '{text}' must be above 0% and at most 200%."
                : $"Row {row}: {kind} rate '{text}' reads as {value:0.##} times charges; write a percentage with a % sign (e.g. {value}%) or as a fraction.");
            return (null, true);
        }
        return (value, false);
    }

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "M/d/yy", "yyyyMMdd"];

    private static bool TryParseDate(string text, out DateTime date) =>
        DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    /// <summary>Ten digits with a valid Luhn check digit over the "80840" prefix.</summary>
    internal static bool IsValidNpi(string npi)
    {
        if (npi.Length != 10 || !npi.All(char.IsAsciiDigit)) return false;
        var digits = "80840" + npi;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return sum % 10 == 0;
    }
}
