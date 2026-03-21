using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;

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
    Task SeedMedicareRbrvs(string csvFilePath, int year);
    Task SeedMedicareDrg(string csvFilePath, int year);
    Task SeedMedicareOpps(string csvFilePath, int year);
    Task SeedDemoDataAsync();
}

public class FeeScheduleLoaderService : IFeeScheduleLoaderService
{
    private readonly IFeeScheduleRepository _repo;
    private readonly ILogger<FeeScheduleLoaderService> _logger;

    public FeeScheduleLoaderService(IFeeScheduleRepository repo, ILogger<FeeScheduleLoaderService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    /// <summary>
    /// Import CMS Physician Fee Schedule Relative Value File (RVU).
    /// Expected CSV columns: HCPCS, MOD, DESCRIPTION, WORK_RVU, NON_FAC_PE_RVU, FAC_PE_RVU, 
    /// MP_RVU, NON_FACILITY_NA_INDICATOR, FACILITY_NA_INDICATOR, CONV_FACTOR, LOCALITY, etc.
    /// </summary>
    public async Task SeedMedicareRbrvs(string csvFilePath, int year)
    {
        _logger.LogInformation("Loading Medicare RBRVS {Year} from {Path}", year, csvFilePath);

        // TODO: Wire in CsvHelper to parse actual CMS PFSRVF file
        // The actual CMS file has ~13,000 HCPCS codes × ~120 localities = ~1.5M rows
        // For now, this is the integration point — the pattern is:
        //
        // using var reader = new StreamReader(csvFilePath);
        // using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        // var records = csv.GetRecords<MedicareRbrvsCsvRow>();
        // var entries = records.Select(r => MapRbrvsCsvToEntry(r, year));
        // await _repo.BulkUpsertEntriesAsync(entries);

        var scheduleId = $"MEDICARE_RBRVS_{year}";
        await _repo.UpsertScheduleInfoAsync(new FeeScheduleInfo
        {
            Id = scheduleId,
            Name = $"Medicare Physician Fee Schedule (RBRVS) {year}",
            Type = FeeScheduleType.MedicareRbrvs,
            Version = $"{year}.Q1",
            EffectiveDate = new DateOnly(year, 1, 1),
            TermDate = new DateOnly(year, 12, 31),
            CodeCount = 0, // Updated after import
            Description = $"CMS Medicare Physician Fee Schedule Relative Value Units for calendar year {year}. Source: CMS.gov PFSRVF.",
            LastUpdated = DateTimeOffset.UtcNow
        });

        _logger.LogInformation("Medicare RBRVS {Year} schedule registered. Load CSV data to populate entries.", year);
    }

    public async Task SeedMedicareDrg(string csvFilePath, int year)
    {
        _logger.LogInformation("Loading Medicare MS-DRG {Year} from {Path}", year, csvFilePath);

        var scheduleId = $"MEDICARE_DRG_{year}";
        await _repo.UpsertScheduleInfoAsync(new FeeScheduleInfo
        {
            Id = scheduleId,
            Name = $"Medicare MS-DRG Weights {year}",
            Type = FeeScheduleType.MedicareDrg,
            Version = $"FY{year}",
            EffectiveDate = new DateOnly(year - 1, 10, 1), // Federal fiscal year
            TermDate = new DateOnly(year, 9, 30),
            CodeCount = 0,
            Description = $"CMS Medicare Severity Diagnosis-Related Group relative weights for FY{year}.",
            LastUpdated = DateTimeOffset.UtcNow
        });
    }

    public async Task SeedMedicareOpps(string csvFilePath, int year)
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
}
