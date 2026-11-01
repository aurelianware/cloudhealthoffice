using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using BenefitPlanService.Import.FeeSchedules;
using CloudHealthOffice.FeeScheduleEngine.Medicaid;

namespace BenefitPlanService.Import.MedicaidDrg;

/// <summary>One uploaded file: its name and content.</summary>
public sealed record TexasDrgUpload(string FileName, Stream Content);

public sealed record TexasDrgImportOptions
{
    /// <summary>Overwrite a stored table with the same effective date whose content differs.</summary>
    public bool Replace { get; init; }
    public bool DryRun { get; init; } = true;
}

/// <summary>What happened to one uploaded file.</summary>
public sealed class TexasDrgImportFileResult
{
    public string FileName { get; init; } = string.Empty;

    /// <summary>"weights", or the hospital class ("Urban", "Rural").</summary>
    public string Kind { get; init; } = string.Empty;
    public string TableId { get; init; } = string.Empty;
    public string? GrouperVersion { get; init; }
    public int? StateFiscalYear { get; init; }
    public DateTime EffectiveDate { get; init; }
    public decimal? UniversalMean { get; init; }
    public int Rows { get; init; }

    /// <summary>Weights: DRG-SOIs printed without numbers (ungroupable).</summary>
    public IReadOnlyList<string> Unpriced { get; init; } = [];

    /// <summary>Hospitals: rows held back because their columns contradict each other.</summary>
    public IReadOnlyList<MedicaidHospitalRateDiscrepancy> HeldBack { get; init; } = [];

    /// <summary>Hospitals: NPIs on more than one row.</summary>
    public IReadOnlyList<string> DuplicateNpis { get; init; } = [];

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FeeScheduleImportOutcome Outcome { get; set; }
}

public sealed class TexasDrgImportReport
{
    public bool DryRun { get; init; }
    public List<TexasDrgImportFileResult> Files { get; } = [];

    /// <summary>Cross-file checks that did not stop the import, e.g. universal means that disagree.</summary>
    public List<string> Warnings { get; } = [];
}

public interface ITexasDrgRateImportService
{
    Task<TexasDrgImportReport> ImportAsync(
        IReadOnlyList<TexasDrgUpload> uploads, TexasDrgImportOptions options, string tenantId, string actor, CancellationToken ct = default);
}

/// <summary>
/// Loads HHSC's Texas APR-DRG weight tables (PDF or Excel) and hospital SDA
/// files (PDF) into the Medicaid DRG rate store. Each file is recognised by
/// its content, not its name. Every file is parsed before anything is
/// written, so one bad file writes nothing.
/// </summary>
public sealed class TexasDrgRateImportService(IMedicaidDrgRateRepository store) : ITexasDrgRateImportService
{
    private const string State = "TX";

    public async Task<TexasDrgImportReport> ImportAsync(
        IReadOnlyList<TexasDrgUpload> uploads, TexasDrgImportOptions options, string tenantId, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        var now = DateTime.UtcNow;
        var weightTables = new List<(string File, MedicaidDrgWeightTable Table, TexasDrgWeightFile Parsed)>();
        var hospitalTables = new List<(string File, MedicaidHospitalRateTable Table, TexasSdaFile Parsed)>();

        foreach (var upload in uploads)
        {
            var bytes = await ReadAll(upload.Content, ct);
            var name = upload.FileName;
            var ext = Path.GetExtension(name).ToLowerInvariant();

            if (ext is ".xls" or ".xlsx")
            {
                var parsed = TexasDrgWeightFileReader.ReadExcel(new MemoryStream(bytes), name);
                weightTables.Add((name, ToTable(parsed, tenantId, name, actor, now), parsed));
                continue;
            }

            if (ext != ".pdf" && !IsPdf(bytes))
                throw new TexasSdaFormatException($"'{name}' is not a PDF or Excel file.");

            // A PDF is a weight table or an SDA file; the title says which.
            var text = PdfTitleText(bytes);
            if (text.Contains("Grouper Version", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = TexasDrgWeightFileReader.Read(new MemoryStream(bytes));
                weightTables.Add((name, ToTable(parsed, tenantId, name, actor, now), parsed));
            }
            else
            {
                var parsed = TexasSdaFileReader.Read(new MemoryStream(bytes));
                hospitalTables.Add((name, ToTable(parsed, tenantId, name, actor, now), parsed));
            }
        }

        var report = new TexasDrgImportReport { DryRun = options.DryRun };

        var dupWeights = weightTables.GroupBy(w => w.Table.Id).FirstOrDefault(g => g.Select(x => x.Table.Checksum).Distinct().Count() > 1);
        if (dupWeights is not null)
            throw new TexasSdaFormatException(
                $"Two weight files take effect on {dupWeights.First().Table.EffectiveDate:yyyy-MM-dd} with different rows: {string.Join(", ", dupWeights.Select(d => d.File))}.");
        var dupHospitals = hospitalTables.GroupBy(h => h.Table.Id).FirstOrDefault(g => g.Select(x => x.Table.Checksum).Distinct().Count() > 1);
        if (dupHospitals is not null)
            throw new TexasSdaFormatException(
                $"Two {dupHospitals.First().Table.HospitalClass} SDA files take effect on {dupHospitals.First().Table.EffectiveDate:yyyy-MM-dd} with different rows.");

        CrossCheck(weightTables.Select(w => w.Table).ToList(), hospitalTables.Select(h => h.Table).ToList(), report);

        // Identical copies of one file (same id and checksum) are written once.
        foreach (var (file, table, parsed) in weightTables.GroupBy(w => w.Table.Id).Select(g => g.First()))
        {
            var existing = await store.GetWeightTableByIdAsync(tenantId, table.Id, ct);
            var result = new TexasDrgImportFileResult
            {
                FileName = file,
                Kind = "weights",
                TableId = table.Id,
                GrouperVersion = table.GrouperVersion,
                StateFiscalYear = table.StateFiscalYear,
                EffectiveDate = table.EffectiveDate,
                UniversalMean = table.UniversalMean,
                Rows = table.Rows.Count,
                Unpriced = parsed.Unpriced,
                Outcome = Decide(existing?.Checksum, table.Checksum, options),
            };
            if (result.Outcome is FeeScheduleImportOutcome.Created or FeeScheduleImportOutcome.Replaced)
                await store.UpsertWeightTableAsync(table, ct);
            report.Files.Add(result);
        }

        foreach (var (file, table, parsed) in hospitalTables.GroupBy(h => h.Table.Id).Select(g => g.First()))
        {
            var existing = await store.GetHospitalTableByIdAsync(tenantId, table.Id, ct);
            var result = new TexasDrgImportFileResult
            {
                FileName = file,
                Kind = table.HospitalClass,
                TableId = table.Id,
                StateFiscalYear = table.StateFiscalYear,
                EffectiveDate = table.EffectiveDate,
                UniversalMean = table.UniversalMean,
                Rows = table.Rows.Count,
                HeldBack = table.Discrepancies,
                DuplicateNpis = parsed.DuplicateNpis,
                Outcome = Decide(existing?.Checksum, table.Checksum, options),
            };
            if (result.Outcome is FeeScheduleImportOutcome.Created or FeeScheduleImportOutcome.Replaced)
                await store.UpsertHospitalTableAsync(table, ct);
            report.Files.Add(result);
        }

        return report;
    }

    private static FeeScheduleImportOutcome Decide(string? existingChecksum, string checksum, TexasDrgImportOptions options) =>
        existingChecksum switch
        {
            null => options.DryRun ? FeeScheduleImportOutcome.WouldWrite : FeeScheduleImportOutcome.Created,
            var c when c == checksum => FeeScheduleImportOutcome.Unchanged,
            _ when !options.Replace => FeeScheduleImportOutcome.Exists,
            _ => options.DryRun ? FeeScheduleImportOutcome.WouldWrite : FeeScheduleImportOutcome.Replaced,
        };

    /// <summary>Universal means for the same effective date should agree across files.</summary>
    private static void CrossCheck(List<MedicaidDrgWeightTable> weights, List<MedicaidHospitalRateTable> hospitals, TexasDrgImportReport report)
    {
        var means = weights.Where(w => w.UniversalMean is not null).Select(w => (w.EffectiveDate, Mean: w.UniversalMean!.Value, Source: "weight table"))
            .Concat(hospitals.Where(h => h.UniversalMean is not null).Select(h => (h.EffectiveDate, Mean: h.UniversalMean!.Value, Source: $"{h.HospitalClass} SDA file")));
        foreach (var g in means.GroupBy(m => m.EffectiveDate).Where(g => g.Select(m => m.Mean).Distinct().Count() > 1))
            report.Warnings.Add($"Universal means effective {g.Key:yyyy-MM-dd} disagree: " +
                string.Join(", ", g.Select(m => $"{m.Source} {m.Mean.ToString("0.00", CultureInfo.InvariantCulture)}")) + ".");
    }

    private static MedicaidDrgWeightTable ToTable(TexasDrgWeightFile f, string tenantId, string file, string actor, DateTime now)
    {
        var table = new MedicaidDrgWeightTable
        {
            Id = MedicaidDrgWeightTable.MakeId(tenantId, State, f.EffectiveDate),
            TenantId = tenantId,
            State = State,
            GrouperVersion = f.GrouperVersion,
            EffectiveDate = f.EffectiveDate,
            StateFiscalYear = f.StateFiscalYear,
            UniversalMean = f.UniversalMean,
            SourceFile = file,
            ImportedAt = now,
            ImportedBy = actor,
            Rows = f.Rows.Select(r => new MedicaidDrgWeightRow
            {
                DrgSoi = r.DrgSoi,
                RelativeWeight = r.RelativeWeight,
                MeanLengthOfStay = r.MeanLengthOfStay,
                DayOutlierThreshold = r.DayOutlierThreshold,
                Description = f.Descriptions.GetValueOrDefault(r.DrgSoi[..3]),
            }).ToList(),
        };
        var sb = new StringBuilder().Append(table.GrouperVersion).Append('|').Append(N(table.UniversalMean)).Append('\n');
        foreach (var r in table.Rows.OrderBy(r => r.DrgSoi, StringComparer.Ordinal))
            sb.Append($"{r.DrgSoi}|{N(r.RelativeWeight)}|{N(r.MeanLengthOfStay)}|{N(r.DayOutlierThreshold)}\n");
        table.Checksum = Hash(sb);
        return table;
    }

    private static MedicaidHospitalRateTable ToTable(TexasSdaFile f, string tenantId, string file, string actor, DateTime now)
    {
        var cls = f.HospitalClass.ToString();
        var table = new MedicaidHospitalRateTable
        {
            Id = MedicaidHospitalRateTable.MakeId(tenantId, State, cls, f.EffectiveDate),
            TenantId = tenantId,
            State = State,
            HospitalClass = cls,
            EffectiveDate = f.EffectiveDate,
            StateFiscalYear = f.StateFiscalYear,
            UniversalMean = f.UniversalMean,
            SourceFile = file,
            ImportedAt = now,
            ImportedBy = actor,
            Rows = f.Rows.Select(r => new MedicaidHospitalRateRow
            {
                StateProviderId = r.Tpi,
                Npi = r.Npi,
                MedicareNumber = r.MedicareNumber,
                HospitalName = r.HospitalName,
                City = r.City,
                Zip = r.Zip,
                County = r.County,
                BaseRate = r.BaseSda,
                AddOns = r.AddOns.ToDictionary(),
                FinalBaseRate = r.FinalSda,
                DeliveryBaseRate = r.DeliverySda,
            }).ToList(),
            Discrepancies = f.Discrepancies.Select(d => new MedicaidHospitalRateDiscrepancy
            {
                StateProviderId = d.Tpi,
                Npi = d.Npi,
                HospitalName = d.HospitalName,
                Problem = d.Problem,
            }).ToList(),
        };
        var sb = new StringBuilder().Append(cls).Append('|').Append(N(table.UniversalMean)).Append('\n');
        foreach (var r in table.Rows.OrderBy(r => r.StateProviderId, StringComparer.Ordinal))
            sb.Append($"{r.StateProviderId}|{r.Npi}|{N(r.FinalBaseRate)}|{N(r.DeliveryBaseRate)}\n");
        foreach (var d in table.Discrepancies.OrderBy(d => d.StateProviderId, StringComparer.Ordinal))
            sb.Append(CultureInfo.InvariantCulture, $"held|{d.StateProviderId}\n");
        table.Checksum = Hash(sb);
        return table;
    }

    /// <summary>
    /// A number as text without trailing zeros, so the PDF's 8.4410 and the
    /// workbook's 8.441 hash the same.
    /// </summary>
    private static string N(decimal? d) => d?.ToString("0.##########", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Hash(StringBuilder sb) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();

    private static async Task<byte[]> ReadAll(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static bool IsPdf(byte[] bytes) => bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F';

    /// <summary>The first page's text, to tell a weight table from an SDA file.</summary>
    private static string PdfTitleText(byte[] bytes)
    {
        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(bytes);
            return doc.NumberOfPages == 0 ? string.Empty : string.Join(' ', doc.GetPage(1).GetWords().Take(80).Select(w => w.Text));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TexasSdaFormatException($"Could not read the upload as a PDF: {ex.Message}");
        }
    }
}
