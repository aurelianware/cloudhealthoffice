using System.Globalization;
using System.Text.Json.Serialization;
using BenefitPlanService.Import.FeeSchedules;
using CloudHealthOffice.FeeScheduleEngine.Medicaid;

namespace BenefitPlanService.Import.MedicaidDrg;

public sealed record PsychPerDiemImportOptions
{
    /// <summary>Overwrite a stored per diem for the same hospital and date whose value differs.</summary>
    public bool Replace { get; init; }

    public bool DryRun { get; init; } = true;
}

/// <summary>What happened to one hospital's rate.</summary>
public sealed class PsychPerDiemImportRow
{
    public string Npi { get; init; } = string.Empty;
    public string? StateProviderId { get; init; }
    public string? HospitalName { get; init; }
    public string? FacilityClass { get; init; }
    public DateTime EffectiveDate { get; init; }
    public decimal DailyRate { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FeeScheduleImportOutcome Outcome { get; set; }
}

public sealed class PsychPerDiemImportReport
{
    public bool DryRun { get; init; }
    public string FileName { get; init; } = string.Empty;
    public int FiscalYear { get; init; }

    /// <summary>Rows in the file, one per TPI.</summary>
    public int FileRows { get; init; }

    public List<PsychPerDiemImportRow> Rows { get; } = [];

    /// <summary>Hospitals left out because their TPIs carry different per diems. Their stays pend.</summary>
    public List<TexasRccHeldBack> HeldBack { get; } = [];
}

public interface ITexasPsychPerDiemImportService
{
    Task<PsychPerDiemImportReport> ImportAsync(
        Stream content, string fileName, PsychPerDiemImportOptions options, string tenantId, string actor, CancellationToken ct = default);
}

/// <summary>
/// Loads HHSC's "SFY yyyy Psychiatric Hospital Rates" PDF into the
/// psychiatric per diem store, one rate per hospital (NPI) and section
/// effective date. Dry run by default.
/// </summary>
public sealed class TexasPsychPerDiemImportService(IMedicaidPsychiatricPerDiemRepository repository) : ITexasPsychPerDiemImportService
{
    private const string State = "TX";

    public async Task<PsychPerDiemImportReport> ImportAsync(
        Stream content, string fileName, PsychPerDiemImportOptions options, string tenantId, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var buffered = new MemoryStream();
        await content.CopyToAsync(buffered, ct);
        buffered.Position = 0;
        if (buffered.Length < 4 || !buffered.GetBuffer().AsSpan(0, 4).SequenceEqual("%PDF"u8))
            throw new InterimRateFormatException("Upload HHSC's psychiatric hospital per diem PDF (for example 2027-ffy-psychiatric-hosp-per-diem-rates.pdf).");

        var file = TexasPsychPerDiemFileReader.Read(buffered);
        var report = new PsychPerDiemImportReport
        {
            DryRun = options.DryRun,
            FileName = fileName,
            FiscalYear = file.FiscalYear,
            FileRows = file.Rows.Count,
        };
        report.HeldBack.AddRange(file.HeldBack);

        var now = DateTime.UtcNow;
        var records = file.Rates.Select(r => new MedicaidPsychiatricPerDiem
        {
            Id = MedicaidPsychiatricPerDiem.MakeId(tenantId, State, r.Npi, r.EffectiveDate),
            TenantId = tenantId,
            State = State,
            Npi = r.Npi,
            StateProviderId = r.Tpi,
            HospitalName = r.ProviderName,
            FacilityClass = r.Section,
            EffectiveDate = r.EffectiveDate,
            DailyRate = r.DailyRate,
            SourceFile = string.Create(CultureInfo.InvariantCulture, $"{fileName} (SFY {file.FiscalYear})"),
            ImportedAt = now,
            ImportedBy = actor,
        }).ToList();

        var stored = (await repository.GetByIdsAsync(tenantId, records.Select(r => r.Id).ToList(), ct))
            .ToDictionary(r => r.Id, StringComparer.Ordinal);

        var toWrite = new List<MedicaidPsychiatricPerDiem>();
        foreach (var record in records)
        {
            var outcome = !stored.TryGetValue(record.Id, out var existing)
                ? (options.DryRun ? FeeScheduleImportOutcome.WouldWrite : FeeScheduleImportOutcome.Created)
                : existing.DailyRate == record.DailyRate
                    ? FeeScheduleImportOutcome.Unchanged
                    : !options.Replace
                        ? FeeScheduleImportOutcome.Exists
                        : options.DryRun ? FeeScheduleImportOutcome.WouldWrite : FeeScheduleImportOutcome.Replaced;

            if (outcome is FeeScheduleImportOutcome.Created or FeeScheduleImportOutcome.Replaced)
                toWrite.Add(record);

            report.Rows.Add(new PsychPerDiemImportRow
            {
                Npi = record.Npi,
                StateProviderId = record.StateProviderId,
                HospitalName = record.HospitalName,
                FacilityClass = record.FacilityClass,
                EffectiveDate = record.EffectiveDate,
                DailyRate = record.DailyRate,
                Outcome = outcome,
            });
        }

        if (!options.DryRun && toWrite.Count > 0)
            await repository.UpsertAsync(toWrite, ct);

        return report;
    }
}
