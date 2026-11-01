using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using BenefitPlanService.Controllers;
using BenefitPlanService.Repositories;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Money;
using CloudHealthOffice.ReferenceData.Domain;

namespace BenefitPlanService.Import.FeeSchedules;

/// <summary>Which Medicare PFS localities to load, and how.</summary>
public sealed class MedicarePfsImportOptions
{
    /// <summary>Localities one request may write (each is one schedule document of ~19,000 lines).</summary>
    public const int MaxLocalities = 120;

    public required int Year { get; init; }

    /// <summary>Defaults to January 1 of <see cref="Year"/>; set it for a quarterly release (RVUyyB = April 1, ...).</summary>
    public DateTime? EffectiveDate { get; init; }
    public DateTime? TermDate { get; init; }

    /// <summary>CMS release label, e.g. "RVU26A". Stored as the source version.</summary>
    public string? Release { get; init; }

    /// <summary>Overrides the PPRRVU CONV FACTOR column (for example the QPP conversion factor).</summary>
    public decimal? ConversionFactor { get; init; }

    /// <summary>"{MAC}-{locality}" keys, e.g. "04412-18".</summary>
    public IReadOnlySet<string> Localities { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Two-letter states: every locality in them.</summary>
    public IReadOnlySet<string> States { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every locality in the GPCI file.</summary>
    public bool AllLocalities { get; init; }

    /// <summary>Make the one selected locality's schedule this plan's default (a Medicare Advantage plan in one locality).</summary>
    public Guid? PlanId { get; init; }

    public bool Replace { get; init; }
    public bool DryRun { get; init; } = true;

    public bool HasSelector => AllLocalities || Localities.Count > 0 || States.Count > 0;

    public DateTime Effective => (EffectiveDate ?? new DateTime(Year, 1, 1)).Date;
}

/// <summary>What happened to one locality's schedule.</summary>
public sealed class MedicarePfsLocalityResult
{
    public string Locality { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ScheduleId { get; init; } = string.Empty;
    public decimal WorkGpci { get; init; }
    public decimal PeGpci { get; init; }
    public decimal MpGpci { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FeeScheduleImportOutcome Outcome { get; set; }

    /// <summary>99213 non-facility in this locality: a quick check against the CMS PFS lookup tool.</summary>
    public decimal? Sample99213NonFacility { get; set; }
}

public sealed class MedicarePfsImportReport
{
    public bool DryRun { get; init; }
    public int Year { get; init; }
    public DateTime EffectiveDate { get; init; }
    public string? Release { get; init; }
    public decimal? ConversionFactor { get; set; }
    public string? PprrvuFile { get; set; }
    public string? GpciFile { get; set; }
    public int RowsRead { get; set; }
    public int LinesPerSchedule { get; set; }
    public int NonFacilityNotApplicable { get; set; }
    public int FacilityNotApplicable { get; set; }
    public Dictionary<string, int> Skipped { get; set; } = new();
    public List<FeeScheduleSkippedRow> SkippedExamples { get; set; } = new();
    public int LocalitiesInFile { get; set; }
    public List<MedicarePfsLocalityResult> Localities { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
}

public interface IMedicarePfsImportService
{
    Task<MedicarePfsImportReport> ImportAsync(
        Stream pprrvu, Stream gpci, MedicarePfsImportOptions options, string tenantId, string? actor,
        CancellationToken ct = default);
}

/// <summary>
/// Loads the CMS Medicare Physician Fee Schedule into the fee schedule store
/// claims are priced from: one <see cref="FeeScheduleType.MedicareMpfs"/>
/// schedule per payment locality, holding the PPRRVU RVUs as
/// <see cref="FeeScheduleRateType.Rvu"/> lines and the locality's GPCIs and
/// the conversion factor on the schedule, so the engine computes
/// (work × work GPCI + PE × PE GPCI + MP × MP GPCI) × CF with the facility or
/// non-facility PE by place of service.
///
/// <para>
/// Schedule id: <c>{tenant}:medicare-pfs-{year}-{mac}-{locality}:{yyyyMMdd}</c>.
/// A percent-of-Medicare contract term names it as its base schedule, a
/// Medicaid schedule names it as <c>BaseMpfsFeeScheduleId</c>, and with
/// <c>planId</c> one locality's schedule is a plan's default.
/// </para>
/// See docs/integration/MEDICARE-PFS-IMPORTER.md.
/// </summary>
public sealed class MedicarePfsImportService : IMedicarePfsImportService
{
    private readonly IFeeScheduleRepository _schedules;
    private readonly IBenefitPlanRepository _plans;
    private readonly ILogger<MedicarePfsImportService> _logger;

    public MedicarePfsImportService(
        IFeeScheduleRepository schedules, IBenefitPlanRepository plans, ILogger<MedicarePfsImportService> logger)
    {
        _schedules = schedules;
        _plans = plans;
        _logger = logger;
    }

    public static string ScheduleId(string tenantId, int year, PfsLocality locality, DateTime effective) =>
        FeeSchedule.MakeId(tenantId, $"medicare-pfs-{year}-{locality.Key}", effective);

    public async Task<MedicarePfsImportReport> ImportAsync(
        Stream pprrvu, Stream gpci, MedicarePfsImportOptions options, string tenantId, string? actor,
        CancellationToken ct = default)
    {
        if (options.PlanId is { } planId
            && await _plans.GetByIdAsync(planId.ToString("D"), tenantId) is null)
        {
            throw new FeeSchedulePlanNotFoundException(planId);
        }

        var report = new MedicarePfsImportReport
        {
            DryRun = options.DryRun,
            Year = options.Year,
            EffectiveDate = options.Effective,
            Release = options.Release,
        };

        var localities = MedicarePfsFiles.ReadGpci(gpci);
        report.LocalitiesInFile = localities.Count;

        var selected = localities
            .Where(l => options.AllLocalities
                        || options.Localities.Contains(l.Key)
                        || options.States.Contains(l.State))
            .ToList();
        var unknown = options.Localities.Where(k => !localities.Any(l => l.Key.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
            report.Errors.Add($"Not in the GPCI file: {string.Join(", ", unknown)}. Keys are MAC-locality, e.g. {localities[0].Key}.");
        if (selected.Count == 0)
            report.Errors.Add("No locality matched; choose localities, states or allLocalities=true.");
        if (selected.Count > MedicarePfsImportOptions.MaxLocalities)
            report.Errors.Add($"{selected.Count} localities selected; one request writes at most {MedicarePfsImportOptions.MaxLocalities}.");
        if (options.PlanId is not null && selected.Count != 1)
            report.Errors.Add("planId makes one locality's schedule the plan's default; select exactly one locality.");

        var rvus = MedicarePfsFiles.ReadPprrvu(pprrvu);
        report.RowsRead = rvus.RowsRead;
        report.LinesPerSchedule = rvus.Lines.Count;
        report.NonFacilityNotApplicable = rvus.NonFacilityNotApplicable;
        report.FacilityNotApplicable = rvus.FacilityNotApplicable;
        report.Skipped = rvus.Skipped;
        report.SkippedExamples = rvus.SkippedExamples;

        var cf = options.ConversionFactor;
        if (cf is null)
        {
            if (rvus.ConversionFactors.Count == 1) cf = rvus.ConversionFactors.Single();
            else if (rvus.ConversionFactors.Count == 0)
                report.Errors.Add("The PPRRVU file has no conversion factor; pass conversionFactor.");
            else
                report.Errors.Add($"The PPRRVU file has several conversion factors ({string.Join(", ", rvus.ConversionFactors)}); pass conversionFactor.");
        }
        else if (cf <= 0m || cf > 1000m)
        {
            report.Errors.Add("conversionFactor must be a positive dollar amount (CY2026: 33.4009, or 33.5675 for qualifying APM participants).");
        }
        report.ConversionFactor = cf;

        if (rvus.Lines.Count == 0) report.Errors.Add("No payable PPRRVU rows were found.");
        if (rvus.Lines.Count > FeeScheduleImportOptions.MaxLines)
            report.Errors.Add($"{rvus.Lines.Count} lines exceed the {FeeScheduleImportOptions.MaxLines}-line schedule limit.");

        if (rvus.RowsRead < 1000)
            report.Warnings.Add($"Only {rvus.RowsRead} PPRRVU rows were read; the CMS file has about 19,000. Check that the whole file was uploaded.");
        report.Warnings.Add(
            "Not applied by the engine: the OPPS cap on non-facility imaging (OPPSCAP file), status T's " +
            "\"paid only when billed alone\" rule, anesthesia (ANES file, base units × anesthesia CF), and " +
            "the Alaska/frontier and HPSA bonuses.");

        if (report.Errors.Count > 0) return report;

        foreach (var locality in selected)
        {
            ct.ThrowIfCancellationRequested();
            var result = new MedicarePfsLocalityResult
            {
                Locality = locality.Key,
                State = locality.State,
                Name = locality.Name,
                ScheduleId = options.PlanId is { } pid
                    ? PlanDefaultFeeScheduleController.ScheduleId(tenantId, pid)
                    : ScheduleId(tenantId, options.Year, locality, options.Effective),
                WorkGpci = locality.WorkGpci,
                PeGpci = locality.PeGpci,
                MpGpci = locality.MpGpci,
            };
            report.Localities.Add(result);

            var sample = rvus.Lines.FirstOrDefault(l => l.ProcedureCode == "99213" && l.Modifier is null);
            if (sample is not null)
            {
                result.Sample99213NonFacility = MoneyRounding.ToCents(
                    ((sample.WorkRvu ?? 0m) * locality.WorkGpci + (sample.PeRvu ?? 0m) * locality.PeGpci
                     + (sample.MpRvu ?? 0m) * locality.MpGpci) * cf!.Value);
            }

            var schedule = new FeeSchedule
            {
                Id = result.ScheduleId,
                TenantId = tenantId,
                Name = $"Medicare PFS {options.Year} {locality.State} {locality.Name} ({locality.Key})".Replace("  ", " ").Trim(),
                Type = FeeScheduleType.MedicareMpfs,
                SourceType = FeeScheduleSourceType.PublicGovernment,
                SourceId = "cms-pfs-rvu",
                SourceVersion = options.Release?.Trim() ?? options.Year.ToString(CultureInfo.InvariantCulture),
                Jurisdiction = locality.State.Length == 2 ? locality.State : null,
                Locality = locality.Key,
                CodeSystem = "HCPCS",
                IsGlobal = false,
                LicenseClassification = LicenseClassification.Public,
                DefaultForPlanId = options.PlanId?.ToString("D"),
                ConversionFactor = cf,
                WorkGpci = locality.WorkGpci,
                PeGpci = locality.PeGpci,
                MpGpci = locality.MpGpci,
                EffectiveDate = DateTime.SpecifyKind(options.Effective, DateTimeKind.Utc),
                TermDate = options.TermDate is { } t ? DateTime.SpecifyKind(t.Date, DateTimeKind.Utc) : null,
                Lines = new List<FeeScheduleLine>(rvus.Lines),
            };
            schedule.Checksum = Checksum(schedule);

            var existing = await _schedules.GetByIdAsync(tenantId, schedule.Id, ct);
            if (existing is not null && existing.Checksum == schedule.Checksum && existing.TermDate == schedule.TermDate)
            {
                result.Outcome = FeeScheduleImportOutcome.Unchanged;
                continue;
            }
            if (existing is not null && !options.Replace)
            {
                result.Outcome = FeeScheduleImportOutcome.Exists;
                continue;
            }
            if (options.DryRun)
            {
                result.Outcome = FeeScheduleImportOutcome.WouldWrite;
                continue;
            }

            schedule.CreatedDate = existing?.CreatedDate ?? DateTime.UtcNow;
            schedule.Revision = existing?.Revision ?? 0;
            await _schedules.UpsertAsync(schedule, ct);
            result.Outcome = existing is null ? FeeScheduleImportOutcome.Created : FeeScheduleImportOutcome.Replaced;
        }

        if (report.Localities.Any(l => l.Outcome == FeeScheduleImportOutcome.Exists))
            report.Warnings.Add("Some localities already have a schedule with other values; pass replace=true to replace them.");

        _logger.LogInformation(
            "AUDIT medicare pfs import tenant={Tenant} year={Year} release={Release} localities={Count} written={Written} dryRun={DryRun} actor={Actor}",
            LogSanitizer.Sanitize(tenantId, 256), options.Year, LogSanitizer.Sanitize(options.Release, 64), report.Localities.Count,
            report.Localities.Count(l => l.Outcome is FeeScheduleImportOutcome.Created or FeeScheduleImportOutcome.Replaced),
            options.DryRun, LogSanitizer.Sanitize(actor, 256));

        return report;
    }

    internal static string Checksum(FeeSchedule s)
    {
        static string D(decimal? d) => d?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var sb = new StringBuilder()
            .Append(D(s.ConversionFactor)).Append('|').Append(D(s.WorkGpci)).Append('|')
            .Append(D(s.PeGpci)).Append('|').Append(D(s.MpGpci)).Append('|')
            .Append(s.EffectiveDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var l in s.Lines)
        {
            sb.Append(l.ProcedureCode).Append('|').Append(l.Modifier).Append('|')
              .Append(D(l.WorkRvu)).Append('|').Append(D(l.PeRvu)).Append('|').Append(D(l.PeRvuFacility)).Append('|')
              .Append(D(l.MpRvu)).Append('|')
              .Append(l.MultipleProcedureIndicator is { } m ? ((byte)m).ToString(CultureInfo.InvariantCulture) : string.Empty)
              .Append('|').Append(l.BilateralAdjustmentApplies ? '1' : '0').Append(l.AssistantAtSurgeryAllowed ? '1' : '0')
              .Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
