using System.Globalization;
using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;

namespace CloudHealthOffice.PricingApi.Services;

/// <summary>
/// Seeds Medicare fee schedule data from CMS public data files.
///
/// Data sources (CMS.gov, updated quarterly/annually):
///   - Physician Fee Schedule (RBRVS): https://www.cms.gov/medicare/payment/fee-schedules/physician
///   - OPPS (Outpatient): https://www.cms.gov/medicare/payment-systems/outpatient-pps
///   - MS-DRG (Inpatient): https://www.cms.gov/medicare/payment/prospective-payment-systems/acute-inpatient-pps
///
/// Download the national PFSRVF (Physician Fee Schedule Relative Value File)
/// and place CSVs in the configured MedicareFeeSchedulePath directory.
/// </summary>
public interface IFeeScheduleLoaderService
{
    Task<int> SeedMedicareRbrvs(string csvFilePath, int year);
    Task<int> SeedMedicareDrg(string csvFilePath, int year, decimal baseRate = 6377.73m);
    Task<int> SeedMedicareOpps(string csvFilePath, int year);
    Task SeedDemoDataAsync();
    Task<bool> AnySchedulesExistAsync();
}

public class FeeScheduleLoaderService : IFeeScheduleLoaderService
{
    private readonly IFeeScheduleRepository _repo;
    private readonly ILogger<FeeScheduleLoaderService> _logger;
    private const int BatchSize = 1000;

    public FeeScheduleLoaderService(IFeeScheduleRepository repo, ILogger<FeeScheduleLoaderService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    /// <summary>
    /// Returns true if any fee schedules already exist in the database.
    /// </summary>
    public async Task<bool> AnySchedulesExistAsync()
    {
        var schedules = await _repo.GetAllSchedulesAsync();
        return schedules.Count > 0;
    }

    /// <summary>
    /// Import CMS Physician Fee Schedule Relative Value File (RVU).
    /// Expected CSV columns: HCPCS, MOD, DESCRIPTION, WORK_RVU, NON_FAC_PE_RVU, FAC_PE_RVU,
    /// MP_RVU, NON_FACILITY_NA_INDICATOR, FACILITY_NA_INDICATOR, CONV_FACTOR, LOCALITY, etc.
    /// </summary>
    public async Task<int> SeedMedicareRbrvs(string csvFilePath, int year)
    {
        _logger.LogInformation("Loading Medicare RBRVS {Year} from {Path}", year, csvFilePath);

        var scheduleId = $"MEDICARE_RBRVS_{year}";
        await _repo.UpsertScheduleInfoAsync(new FeeScheduleInfo
        {
            Id = scheduleId,
            Name = $"Medicare Physician Fee Schedule (RBRVS) {year}",
            Type = FeeScheduleType.MedicareRbrvs,
            Version = $"{year}.Q1",
            EffectiveDate = new DateOnly(year, 1, 1),
            TermDate = new DateOnly(year, 12, 31),
            CodeCount = 0,
            Description = $"CMS Medicare Physician Fee Schedule Relative Value Units for calendar year {year}. Source: CMS.gov PFSRVF.",
            LastUpdated = DateTimeOffset.UtcNow
        });

        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            TrimOptions = TrimOptions.Trim,
            IgnoreBlankLines = true,
        };

        var batch = new List<FeeScheduleEntry>(BatchSize);
        int totalCount = 0;

        using var reader = new StreamReader(csvFilePath);
        using var csv = new CsvReader(reader, csvConfig);
        csv.Context.RegisterClassMap<RbrvsCsvRowMap>();

        await foreach (var row in csv.GetRecordsAsync<RbrvsCsvRow>())
        {
            // Only import base codes (MOD blank or "00"), skip modifier-specific rows
            var mod = row.Mod?.Trim() ?? "";
            if (mod != "" && mod != "00")
                continue;

            // Skip rows where both NA indicators are set
            var nonFacNa = (row.NonFacilityNaIndicator?.Trim() ?? "").Equals("NA", StringComparison.OrdinalIgnoreCase);
            var facNa = (row.FacilityNaIndicator?.Trim() ?? "").Equals("NA", StringComparison.OrdinalIgnoreCase);
            if (nonFacNa && facNa)
                continue;

            var hcpcs = row.Hcpcs?.Trim() ?? "";
            if (string.IsNullOrEmpty(hcpcs))
                continue;

            var workRvu = ParseDecimal(row.WorkRvu);
            var nonFacPeRvu = ParseDecimal(row.NonFacPeRvu);
            var facPeRvu = ParseDecimal(row.FacPeRvu);
            var mpRvu = ParseDecimal(row.MpRvu);
            var convFactor = ParseDecimal(row.ConvFactor);

            var entry = Rbrvs(scheduleId, hcpcs, row.Description?.Trim() ?? "",
                workRvu, nonFacPeRvu, facPeRvu, mpRvu, convFactor);

            // Preserve locality if present
            if (!string.IsNullOrWhiteSpace(row.Locality))
            {
                entry = entry with { Locality = row.Locality.Trim() };
            }

            batch.Add(entry);

            if (batch.Count >= BatchSize)
            {
                await _repo.BulkUpsertEntriesAsync(batch);
                totalCount += batch.Count;
                _logger.LogInformation("RBRVS {Year}: imported {Count} entries so far...", year, totalCount);
                batch.Clear();
            }
        }

        // Flush remaining
        if (batch.Count > 0)
        {
            await _repo.BulkUpsertEntriesAsync(batch);
            totalCount += batch.Count;
        }

        // Update schedule info with actual count
        await _repo.UpsertScheduleInfoAsync(new FeeScheduleInfo
        {
            Id = scheduleId,
            Name = $"Medicare Physician Fee Schedule (RBRVS) {year}",
            Type = FeeScheduleType.MedicareRbrvs,
            Version = $"{year}.Q1",
            EffectiveDate = new DateOnly(year, 1, 1),
            TermDate = new DateOnly(year, 12, 31),
            CodeCount = totalCount,
            Description = $"CMS Medicare Physician Fee Schedule Relative Value Units for calendar year {year}. Source: CMS.gov PFSRVF.",
            LastUpdated = DateTimeOffset.UtcNow
        });

        _logger.LogInformation("Medicare RBRVS {Year} import complete: {Count} entries", year, totalCount);
        return totalCount;
    }

    public async Task<int> SeedMedicareDrg(string csvFilePath, int year, decimal baseRate = 6377.73m)
    {
        _logger.LogInformation("Loading Medicare MS-DRG {Year} from {Path} (baseRate={BaseRate})", year, csvFilePath, baseRate);

        var scheduleId = $"MEDICARE_DRG_{year}";
        await _repo.UpsertScheduleInfoAsync(new FeeScheduleInfo
        {
            Id = scheduleId,
            Name = $"Medicare MS-DRG Weights {year}",
            Type = FeeScheduleType.MedicareDrg,
            Version = $"FY{year}",
            EffectiveDate = new DateOnly(year - 1, 10, 1),
            TermDate = new DateOnly(year, 9, 30),
            CodeCount = 0,
            Description = $"CMS Medicare Severity Diagnosis-Related Group relative weights for FY{year}.",
            LastUpdated = DateTimeOffset.UtcNow
        });

        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            TrimOptions = TrimOptions.Trim,
            IgnoreBlankLines = true,
        };

        var batch = new List<FeeScheduleEntry>(BatchSize);
        int totalCount = 0;

        using var reader = new StreamReader(csvFilePath);
        using var csv = new CsvReader(reader, csvConfig);
        csv.Context.RegisterClassMap<DrgCsvRowMap>();

        await foreach (var row in csv.GetRecordsAsync<DrgCsvRow>())
        {
            var drgCode = row.DrgCode?.Trim() ?? "";
            if (string.IsNullOrEmpty(drgCode))
                continue;

            var weight = ParseDecimal(row.Weight);
            if (weight <= 0)
                continue;

            var entry = Drg(scheduleId, drgCode, row.Description?.Trim() ?? "", weight, baseRate);
            batch.Add(entry);

            if (batch.Count >= BatchSize)
            {
                await _repo.BulkUpsertEntriesAsync(batch);
                totalCount += batch.Count;
                _logger.LogInformation("DRG {Year}: imported {Count} entries so far...", year, totalCount);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await _repo.BulkUpsertEntriesAsync(batch);
            totalCount += batch.Count;
        }

        await _repo.UpsertScheduleInfoAsync(new FeeScheduleInfo
        {
            Id = scheduleId,
            Name = $"Medicare MS-DRG Weights {year}",
            Type = FeeScheduleType.MedicareDrg,
            Version = $"FY{year}",
            EffectiveDate = new DateOnly(year - 1, 10, 1),
            TermDate = new DateOnly(year, 9, 30),
            CodeCount = totalCount,
            Description = $"CMS Medicare Severity Diagnosis-Related Group relative weights for FY{year}.",
            LastUpdated = DateTimeOffset.UtcNow
        });

        _logger.LogInformation("Medicare MS-DRG {Year} import complete: {Count} entries", year, totalCount);
        return totalCount;
    }

    public async Task<int> SeedMedicareOpps(string csvFilePath, int year)
    {
        _logger.LogInformation("Loading Medicare OPPS {Year} from {Path}", year, csvFilePath);

        var scheduleId = $"MEDICARE_OPPS_{year}";
        await _repo.UpsertScheduleInfoAsync(new FeeScheduleInfo
        {
            Id = scheduleId,
            Name = $"Medicare Outpatient Prospective Payment (OPPS) {year}",
            Type = FeeScheduleType.MedicareOpps,
            Version = $"{year}.Q1",
            EffectiveDate = new DateOnly(year, 1, 1),
            TermDate = new DateOnly(year, 12, 31),
            CodeCount = 0,
            Description = $"CMS Outpatient Prospective Payment System APC rates for calendar year {year}.",
            LastUpdated = DateTimeOffset.UtcNow
        });

        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            TrimOptions = TrimOptions.Trim,
            IgnoreBlankLines = true,
        };

        var batch = new List<FeeScheduleEntry>(BatchSize);
        int totalCount = 0;

        using var reader = new StreamReader(csvFilePath);
        using var csv = new CsvReader(reader, csvConfig);
        csv.Context.RegisterClassMap<OppsCsvRowMap>();

        await foreach (var row in csv.GetRecordsAsync<OppsCsvRow>())
        {
            var hcpcs = row.HcpcsCode?.Trim() ?? "";
            if (string.IsNullOrEmpty(hcpcs))
                continue;

            var paymentRate = ParseDecimal(row.PaymentRate);
            if (paymentRate <= 0)
                continue;

            var entry = Opps(scheduleId, hcpcs, row.Description?.Trim() ?? "",
                row.Apc?.Trim() ?? "", row.StatusIndicator?.Trim() ?? "", paymentRate);

            batch.Add(entry);

            if (batch.Count >= BatchSize)
            {
                await _repo.BulkUpsertEntriesAsync(batch);
                totalCount += batch.Count;
                _logger.LogInformation("OPPS {Year}: imported {Count} entries so far...", year, totalCount);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await _repo.BulkUpsertEntriesAsync(batch);
            totalCount += batch.Count;
        }

        await _repo.UpsertScheduleInfoAsync(new FeeScheduleInfo
        {
            Id = scheduleId,
            Name = $"Medicare Outpatient Prospective Payment (OPPS) {year}",
            Type = FeeScheduleType.MedicareOpps,
            Version = $"{year}.Q1",
            EffectiveDate = new DateOnly(year, 1, 1),
            TermDate = new DateOnly(year, 12, 31),
            CodeCount = totalCount,
            Description = $"CMS Outpatient Prospective Payment System APC rates for calendar year {year}.",
            LastUpdated = DateTimeOffset.UtcNow
        });

        _logger.LogInformation("Medicare OPPS {Year} import complete: {Count} entries", year, totalCount);
        return totalCount;
    }

    /// <summary>
    /// Seeds realistic demo data so the API is immediately usable without importing full CMS files.
    /// Covers common E/M, surgical, radiology, and lab codes against national average rates.
    /// </summary>
    public async Task SeedDemoDataAsync()
    {
        _logger.LogInformation("Seeding demo fee schedule data...");

        const string rbrvs = "MEDICARE_RBRVS_2025";
        const string opps = "MEDICARE_OPPS_2025";
        const string drg = "MEDICARE_DRG_2025";
        const decimal cf = 32.7442m; // 2025 CMS conversion factor

        // Register fee schedules
        await _repo.UpsertScheduleInfoAsync(new FeeScheduleInfo
        {
            Id = rbrvs, Name = "Medicare Physician Fee Schedule (RBRVS) 2025 – Demo",
            Type = FeeScheduleType.MedicareRbrvs, Version = "2025.DEMO",
            EffectiveDate = new DateOnly(2025, 1, 1), CodeCount = 25,
            Description = "Demo subset of 2025 Medicare RBRVS with national average rates. Use MEDICARE_RBRVS_2025 with full CMS data for production.",
            LastUpdated = DateTimeOffset.UtcNow
        });

        await _repo.UpsertScheduleInfoAsync(new FeeScheduleInfo
        {
            Id = opps, Name = "Medicare OPPS 2025 – Demo",
            Type = FeeScheduleType.MedicareOpps, Version = "2025.DEMO",
            EffectiveDate = new DateOnly(2025, 1, 1), CodeCount = 10,
            Description = "Demo subset of 2025 Medicare OPPS APC rates.",
            LastUpdated = DateTimeOffset.UtcNow
        });

        await _repo.UpsertScheduleInfoAsync(new FeeScheduleInfo
        {
            Id = drg, Name = "Medicare MS-DRG FY2025 – Demo",
            Type = FeeScheduleType.MedicareDrg, Version = "FY2025.DEMO",
            EffectiveDate = new DateOnly(2024, 10, 1), CodeCount = 10,
            Description = "Demo subset of FY2025 MS-DRG relative weights.",
            LastUpdated = DateTimeOffset.UtcNow
        });

        // ── RBRVS entries (common E/M + surgical codes, national average) ──
        var rbrvsCodes = new List<FeeScheduleEntry>
        {
            Rbrvs(rbrvs, "99203", "Office visit, new patient, low complexity",       0.88m, 1.27m, 0.87m, 0.10m, cf),
            Rbrvs(rbrvs, "99204", "Office visit, new patient, moderate complexity",   1.60m, 1.91m, 1.26m, 0.16m, cf),
            Rbrvs(rbrvs, "99205", "Office visit, new patient, high complexity",       2.31m, 2.61m, 1.64m, 0.23m, cf),
            Rbrvs(rbrvs, "99211", "Office visit, established patient, minimal",       0.18m, 0.47m, 0.30m, 0.02m, cf),
            Rbrvs(rbrvs, "99212", "Office visit, established patient, straightforward",0.70m, 0.81m, 0.55m, 0.06m, cf),
            Rbrvs(rbrvs, "99213", "Office visit, established patient, low complexity", 0.97m, 1.10m, 0.73m, 0.08m, cf),
            Rbrvs(rbrvs, "99214", "Office visit, established patient, moderate",       1.50m, 1.61m, 1.05m, 0.12m, cf),
            Rbrvs(rbrvs, "99215", "Office visit, established patient, high complexity",2.11m, 2.17m, 1.37m, 0.18m, cf),
            Rbrvs(rbrvs, "99281", "ED visit, self-limited problem",                   0.28m, 0.65m, 0.65m, 0.05m, cf),
            Rbrvs(rbrvs, "99283", "ED visit, moderate severity",                      1.24m, 1.43m, 1.43m, 0.14m, cf),
            Rbrvs(rbrvs, "99285", "ED visit, life-threatening condition",              3.80m, 3.08m, 3.08m, 0.36m, cf),
            Rbrvs(rbrvs, "10060", "Incision and drainage of abscess, simple",          1.22m, 1.79m, 0.78m, 0.19m, cf),
            Rbrvs(rbrvs, "27447", "Total knee arthroplasty",                          20.71m, 12.73m, 12.73m, 4.49m, cf),
            Rbrvs(rbrvs, "27130", "Total hip arthroplasty",                           20.06m, 12.37m, 12.37m, 4.93m, cf),
            Rbrvs(rbrvs, "43239", "Upper GI endoscopy with biopsy",                   2.39m, 3.42m, 1.01m, 0.30m, cf),
            Rbrvs(rbrvs, "45380", "Colonoscopy with biopsy",                          3.22m, 4.44m, 1.56m, 0.33m, cf),
            Rbrvs(rbrvs, "71046", "Chest X-ray, 2 views",                             0.18m, 0.67m, 0.12m, 0.04m, cf),
            Rbrvs(rbrvs, "73721", "MRI knee without contrast",                        1.09m, 5.26m, 0.80m, 0.14m, cf),
            Rbrvs(rbrvs, "80053", "Comprehensive metabolic panel",                     0.00m, 0.15m, 0.15m, 0.01m, cf),
            Rbrvs(rbrvs, "85025", "CBC with differential",                             0.00m, 0.10m, 0.10m, 0.01m, cf),
            Rbrvs(rbrvs, "36415", "Venipuncture for blood draw",                       0.00m, 0.15m, 0.10m, 0.01m, cf),
            Rbrvs(rbrvs, "90837", "Psychotherapy, 60 minutes",                         1.65m, 1.67m, 0.97m, 0.09m, cf),
            Rbrvs(rbrvs, "97110", "Therapeutic exercises",                             0.44m, 0.58m, 0.25m, 0.03m, cf),
            Rbrvs(rbrvs, "97140", "Manual therapy techniques",                         0.43m, 0.53m, 0.23m, 0.03m, cf),
            Rbrvs(rbrvs, "99232", "Subsequent hospital care, moderate complexity",      1.39m, 0.58m, 0.58m, 0.06m, cf),
        };

        // ── OPPS entries (common outpatient APCs) ──
        var oppsCodes = new List<FeeScheduleEntry>
        {
            Opps(opps, "99283", "ED visit, moderate severity",          "5022", "V",  284.42m),
            Opps(opps, "99285", "ED visit, life-threatening",           "5025", "V",  734.59m),
            Opps(opps, "43239", "Upper GI endoscopy w/ biopsy",         "5301", "T",  1235.80m),
            Opps(opps, "45380", "Colonoscopy w/ biopsy",                "5302", "T",  1441.28m),
            Opps(opps, "71046", "Chest X-ray 2 views",                  "5521", "S",  32.59m),
            Opps(opps, "73721", "MRI knee w/o contrast",                "5571", "S",  267.83m),
            Opps(opps, "27447", "Total knee arthroplasty",              "5115", "J1", 15237.50m),
            Opps(opps, "27130", "Total hip arthroplasty",               "5115", "J1", 15237.50m),
            Opps(opps, "10060", "I&D abscess, simple",                  "5071", "T",  302.78m),
            Opps(opps, "36415", "Venipuncture",                         "5691", "S",  3.00m),
        };

        // ── DRG entries (common MS-DRGs with relative weights) ──
        const decimal nationalBaseRate = 6377.73m; // Approximate FY2025 national average
        var drgCodes = new List<FeeScheduleEntry>
        {
            Drg(drg, "470", "Major hip/knee joint replacement w/o MCC", 1.7390m, nationalBaseRate),
            Drg(drg, "469", "Major hip/knee joint replacement w/ MCC",  2.2652m, nationalBaseRate),
            Drg(drg, "871", "Septicemia or severe sepsis w/o MV >96hrs w/ MCC", 1.8627m, nationalBaseRate),
            Drg(drg, "291", "Heart failure and shock w/ MCC",           1.2788m, nationalBaseRate),
            Drg(drg, "292", "Heart failure and shock w/ CC",            0.8497m, nationalBaseRate),
            Drg(drg, "194", "Simple pneumonia w/ CC",                   0.7943m, nationalBaseRate),
            Drg(drg, "690", "Kidney/urinary tract infections w/o MCC",  0.6913m, nationalBaseRate),
            Drg(drg, "392", "Esophagitis/gastro/misc digest w/o MCC",   0.7069m, nationalBaseRate),
            Drg(drg, "378", "GI hemorrhage w/ CC",                      0.9614m, nationalBaseRate),
            Drg(drg, "065", "Intracranial hemorrhage or cerebral infarction w/ CC", 1.0651m, nationalBaseRate),
        };

        await _repo.BulkUpsertEntriesAsync(rbrvsCodes);
        await _repo.BulkUpsertEntriesAsync(oppsCodes);
        await _repo.BulkUpsertEntriesAsync(drgCodes);

        _logger.LogInformation("Demo data seeded: {Rbrvs} RBRVS, {Opps} OPPS, {Drg} DRG entries",
            rbrvsCodes.Count, oppsCodes.Count, drgCodes.Count);
    }

    // ── Helper factories ──

    private static FeeScheduleEntry Rbrvs(string schedId, string code, string desc,
        decimal workRvu, decimal peNonFac, decimal peFac, decimal mpRvu, decimal convFactor)
    {
        var totalNonFac = workRvu + peNonFac + mpRvu;
        var totalFac = workRvu + peFac + mpRvu;
        return new FeeScheduleEntry
        {
            FeeScheduleId = schedId,
            ProcedureCode = code,
            Description = desc,
            WorkRvu = workRvu,
            PracticeExpenseRvu = peNonFac,
            PracticeExpenseRvuFacility = peFac,
            MalpracticeRvu = mpRvu,
            TotalRvuNonFacility = totalNonFac,
            TotalRvuFacility = totalFac,
            ConversionFactor = convFactor,
            NonFacilityRate = Math.Round(totalNonFac * convFactor, 2),
            FacilityRate = Math.Round(totalFac * convFactor, 2),
            MultiProcRank = code.StartsWith("99") ? null : 0  // E/M codes exempt from MPPR
        };
    }

    private static FeeScheduleEntry Opps(string schedId, string code, string desc,
        string apc, string statusIndicator, decimal paymentRate)
    {
        return new FeeScheduleEntry
        {
            FeeScheduleId = schedId,
            ProcedureCode = code,
            Description = desc,
            ApcCode = apc,
            StatusIndicator = statusIndicator,
            ApcPaymentRate = paymentRate,
            NonFacilityRate = paymentRate,
            FacilityRate = paymentRate
        };
    }

    private static FeeScheduleEntry Drg(string schedId, string drgCode, string desc,
        decimal weight, decimal baseRate)
    {
        return new FeeScheduleEntry
        {
            FeeScheduleId = schedId,
            ProcedureCode = drgCode,
            Description = desc,
            DrgWeight = weight,
            DrgBaseRate = baseRate,
            NonFacilityRate = Math.Round(weight * baseRate, 2),
            FacilityRate = Math.Round(weight * baseRate, 2)
        };
    }

    /// <summary>
    /// Safely parse a decimal from a CMS CSV field that may be blank, whitespace, or contain spaces.
    /// </summary>
    private static decimal ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0m;

        var cleaned = value.Trim().Replace(" ", "");
        if (string.IsNullOrEmpty(cleaned))
            return 0m;

        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0m;
    }

    // ── CsvHelper row models and mappings ──

    /// <summary>CMS PFSRVF (Physician Fee Schedule Relative Value File) row.</summary>
    private sealed class RbrvsCsvRow
    {
        public string? Hcpcs { get; set; }
        public string? Mod { get; set; }
        public string? Description { get; set; }
        public string? WorkRvu { get; set; }
        public string? NonFacPeRvu { get; set; }
        public string? FacPeRvu { get; set; }
        public string? MpRvu { get; set; }
        public string? ConvFactor { get; set; }
        public string? Locality { get; set; }
        public string? NonFacilityNaIndicator { get; set; }
        public string? FacilityNaIndicator { get; set; }
    }

    private sealed class RbrvsCsvRowMap : ClassMap<RbrvsCsvRow>
    {
        public RbrvsCsvRowMap()
        {
            Map(m => m.Hcpcs).Name("HCPCS", "HCPCS_CD", "Hcpcs", "CPT/HCPCS", "hcpcs");
            Map(m => m.Mod).Name("MOD", "MODIFIER", "Mod", "mod");
            Map(m => m.Description).Name("DESCRIPTION", "SHORT_DESCRIPTION", "Description", "Short_Description", "description");
            Map(m => m.WorkRvu).Name("WORK_RVU", "WORK RVU", "Work_RVU", "WorkRVU", "work_rvu");
            Map(m => m.NonFacPeRvu).Name("NON_FAC_PE_RVU", "NON-FAC PE RVU", "Non_Fac_PE_RVU", "NonFacPeRvu", "non_fac_pe_rvu", "NONFAC_PE_RVU");
            Map(m => m.FacPeRvu).Name("FAC_PE_RVU", "FAC PE RVU", "Fac_PE_RVU", "FacPeRvu", "fac_pe_rvu");
            Map(m => m.MpRvu).Name("MP_RVU", "MAL_PRAC_RVU", "MALPRACTICE_RVU", "MP RVU", "MpRvu", "mp_rvu");
            Map(m => m.ConvFactor).Name("CONV_FACTOR", "CF", "CONVERSION_FACTOR", "Conversion_Factor", "conv_factor");
            Map(m => m.Locality).Name("LOCALITY", "MAC_LOCALITY", "Locality", "locality");
            Map(m => m.NonFacilityNaIndicator).Name("NON_FACILITY_NA_INDICATOR", "Non_Facility_NA_Indicator", "NONFAC_NA_IND");
            Map(m => m.FacilityNaIndicator).Name("FACILITY_NA_INDICATOR", "Facility_NA_Indicator", "FAC_NA_IND");
        }
    }

    /// <summary>CMS OPPS Addendum B row.</summary>
    private sealed class OppsCsvRow
    {
        public string? HcpcsCode { get; set; }
        public string? Description { get; set; }
        public string? Apc { get; set; }
        public string? StatusIndicator { get; set; }
        public string? RelativeWeight { get; set; }
        public string? PaymentRate { get; set; }
    }

    private sealed class OppsCsvRowMap : ClassMap<OppsCsvRow>
    {
        public OppsCsvRowMap()
        {
            Map(m => m.HcpcsCode).Name("HCPCS_Code", "HCPCS Code", "CPT/HCPCS", "HCPCS", "hcpcs_code");
            Map(m => m.Description).Name("Short_Descriptor", "Short Descriptor", "SHORT_DESCRIPTOR", "Description", "description");
            Map(m => m.Apc).Name("APC", "APC_Code", "Apc", "apc");
            Map(m => m.StatusIndicator).Name("SI", "Status_Indicator", "Status Indicator", "StatusIndicator", "si");
            Map(m => m.RelativeWeight).Name("Relative_Weight", "APC Relative Weight", "Relative Weight", "relative_weight");
            Map(m => m.PaymentRate).Name("Payment_Rate", "APC Payment Rate", "Payment Rate", "payment_rate");
        }
    }

    /// <summary>CMS MS-DRG Table 5 row.</summary>
    private sealed class DrgCsvRow
    {
        public string? DrgCode { get; set; }
        public string? Description { get; set; }
        public string? Weight { get; set; }
    }

    private sealed class DrgCsvRowMap : ClassMap<DrgCsvRow>
    {
        public DrgCsvRowMap()
        {
            Map(m => m.DrgCode).Name("MS-DRG", "DRG", "MS_DRG", "DRG_Code", "drg", "ms_drg");
            Map(m => m.Description).Name("DRG_Description", "MS-DRG Title", "DRG Description", "Description", "description");
            Map(m => m.Weight).Name("Weights", "Relative Weight", "Relative_Weight", "Weight", "weight", "weights");
        }
    }
}
