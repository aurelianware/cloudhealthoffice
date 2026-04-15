using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Generators;

/// <summary>
/// Generates synthetic fee schedules with Medicaid-level rates for benchmark testing.
/// Produces three fee schedules: Medicaid, Out-of-Network (150% of Medicaid), and Capitation.
/// </summary>
public static class SyntheticFeeScheduleGenerator
{
    /// <summary>
    /// Generate all fee schedules.
    /// </summary>
    /// <param name="seed">Random seed (unused — schedules are deterministic).</param>
    /// <param name="effectiveDate">Fee schedule effective date. Default: January 1, 2024.</param>
    /// <returns>List of three fee schedules.</returns>
    public static List<SyntheticFeeSchedule> Generate(int seed = 42, DateTime? effectiveDate = null)
    {
        var effDate = effectiveDate ?? new DateTime(2024, 1, 1);

        return new List<SyntheticFeeSchedule>
        {
            GenerateMedicaidFeeSchedule(effDate),
            GenerateOonFeeSchedule(effDate),
            GenerateCapitationSchedule(effDate),
        };
    }

    /// <summary>
    /// Medicaid Fee Schedule — based on TMPPM rates (~60-75% of Medicare RBRVS).
    /// </summary>
    private static SyntheticFeeSchedule GenerateMedicaidFeeSchedule(DateTime effectiveDate)
    {
        var fs = new SyntheticFeeSchedule
        {
            FeeScheduleId = "FS-MEDICAID",
            Name = "Texas Medicaid Fee Schedule 2024",
            Type = "Medicaid",
            EffectiveDate = effectiveDate,
            PercentOfMedicare = 0.70m,
            DrgBaseRate = 5500m,
        };

        // E/M Office Visit codes (99211-99215)
        fs.Lines.AddRange(new[]
        {
            CreateLine("99211", 25m, effectiveDate),
            CreateLine("99212", 55m, effectiveDate),
            CreateLine("99213", 85m, effectiveDate),
            CreateLine("99214", 125m, effectiveDate),
            CreateLine("99215", 150m, effectiveDate),
            CreateLine("99201", 60m, effectiveDate),
            CreateLine("99202", 75m, effectiveDate),
            CreateLine("99203", 110m, effectiveDate),
            CreateLine("99204", 165m, effectiveDate),
            CreateLine("99205", 220m, effectiveDate),
        });

        // Surgical procedures
        fs.Lines.AddRange(new[]
        {
            CreateLine("27447", 5100m, effectiveDate),
            CreateLine("27130", 5520m, effectiveDate),
            CreateLine("47562", 2700m, effectiveDate),
            CreateLine("49505", 1920m, effectiveDate),
            CreateLine("29881", 2280m, effectiveDate),
            CreateLine("64721", 1680m, effectiveDate),
            CreateLine("66984", 2460m, effectiveDate),
            CreateLine("23412", 3900m, effectiveDate),
            CreateLine("44970", 2880m, effectiveDate),
            CreateLine("50590", 3120m, effectiveDate),
            CreateLine("28296", 2520m, effectiveDate),
            CreateLine("15002", 1080m, effectiveDate),
            CreateLine("22612", 7500m, effectiveDate),
            CreateLine("33533", 10800m, effectiveDate),
            CreateLine("35301", 5280m, effectiveDate),
        });

        // Telemedicine codes
        fs.Lines.AddRange(new[]
        {
            CreateLine("90834", 95m, effectiveDate),
            CreateLine("90837", 120m, effectiveDate),
            CreateLine("99441", 30m, effectiveDate),
            CreateLine("99442", 55m, effectiveDate),
            CreateLine("99443", 75m, effectiveDate),
        });

        // Lab/Pathology codes
        fs.Lines.AddRange(new[]
        {
            CreateLine("80053", 40m, effectiveDate),
            CreateLine("80061", 35m, effectiveDate),
            CreateLine("85025", 25m, effectiveDate),
            CreateLine("81001", 14m, effectiveDate),
            CreateLine("83036", 30m, effectiveDate),
            CreateLine("84443", 38m, effectiveDate),
            CreateLine("87880", 20m, effectiveDate),
            CreateLine("87804", 22m, effectiveDate),
            CreateLine("86900", 16m, effectiveDate),
            CreateLine("88305", 110m, effectiveDate),
            CreateLine("80048", 30m, effectiveDate),
            CreateLine("82947", 11m, effectiveDate),
        });

        // Behavioral Health codes
        fs.Lines.AddRange(new[]
        {
            CreateLine("90791", 160m, effectiveDate),
            CreateLine("90847", 110m, effectiveDate),
            CreateLine("90853", 48m, effectiveDate),
            CreateLine("96127", 14m, effectiveDate),
            CreateLine("99024", 0m, effectiveDate),
        });

        // Dental CDT codes ($20-$1,500 range)
        fs.Lines.AddRange(new[]
        {
            CreateLine("D0120", 30m, effectiveDate),
            CreateLine("D0140", 42m, effectiveDate),
            CreateLine("D0150", 55m, effectiveDate),
            CreateLine("D0210", 80m, effectiveDate),
            CreateLine("D0220", 20m, effectiveDate),
            CreateLine("D0274", 38m, effectiveDate),
            CreateLine("D0330", 70m, effectiveDate),
            CreateLine("D1110", 55m, effectiveDate),
            CreateLine("D1120", 38m, effectiveDate),
            CreateLine("D1206", 23m, effectiveDate),
            CreateLine("D1351", 28m, effectiveDate),
            CreateLine("D1510", 165m, effectiveDate),
            CreateLine("D2140", 95m, effectiveDate),
            CreateLine("D2150", 115m, effectiveDate),
            CreateLine("D2160", 138m, effectiveDate),
            CreateLine("D2330", 100m, effectiveDate),
            CreateLine("D2331", 125m, effectiveDate),
            CreateLine("D2332", 150m, effectiveDate),
            CreateLine("D2391", 115m, effectiveDate),
            CreateLine("D2392", 145m, effectiveDate),
            CreateLine("D2740", 680m, effectiveDate),
            CreateLine("D2750", 740m, effectiveDate),
            CreateLine("D2950", 175m, effectiveDate),
            CreateLine("D3110", 72m, effectiveDate),
            CreateLine("D3220", 115m, effectiveDate),
            CreateLine("D3310", 440m, effectiveDate),
            CreateLine("D3320", 530m, effectiveDate),
            CreateLine("D3330", 650m, effectiveDate),
            CreateLine("D3346", 530m, effectiveDate),
            CreateLine("D3410", 385m, effectiveDate),
            CreateLine("D4210", 235m, effectiveDate),
            CreateLine("D4240", 355m, effectiveDate),
            CreateLine("D4341", 155m, effectiveDate),
            CreateLine("D4342", 100m, effectiveDate),
            CreateLine("D4355", 115m, effectiveDate),
            CreateLine("D4910", 90m, effectiveDate),
            CreateLine("D4381", 50m, effectiveDate),
            CreateLine("D7111", 72m, effectiveDate),
            CreateLine("D7140", 115m, effectiveDate),
            CreateLine("D7210", 192m, effectiveDate),
            CreateLine("D7220", 235m, effectiveDate),
            CreateLine("D7230", 295m, effectiveDate),
            CreateLine("D7240", 355m, effectiveDate),
            CreateLine("D7310", 175m, effectiveDate),
            CreateLine("D7510", 235m, effectiveDate),
            CreateLine("D8010", 1500m, effectiveDate),
            CreateLine("D8020", 1920m, effectiveDate),
            CreateLine("D8080", 3300m, effectiveDate),
            CreateLine("D8090", 3720m, effectiveDate),
            CreateLine("D8210", 1080m, effectiveDate),
            CreateLine("D8670", 115m, effectiveDate),
            CreateLine("D8680", 268m, effectiveDate),
        });

        // DRG rates (simplified 50 DRGs, $3,000-$50,000)
        fs.DrgRates.AddRange(new[]
        {
            new SyntheticDrgRate { DrgCode = "470", Description = "Major hip and knee joint replacement", Weight = 2.40m, AllowedAmount = 13200m },
            new SyntheticDrgRate { DrgCode = "871", Description = "Septicemia without MV >96hr with MCC", Weight = 2.36m, AllowedAmount = 12980m },
            new SyntheticDrgRate { DrgCode = "392", Description = "Esophagitis, gastroenteritis without MCC", Weight = 0.85m, AllowedAmount = 4675m },
            new SyntheticDrgRate { DrgCode = "690", Description = "Kidney and UTI without MCC", Weight = 0.82m, AllowedAmount = 4510m },
            new SyntheticDrgRate { DrgCode = "291", Description = "Heart failure and shock with MCC", Weight = 1.73m, AllowedAmount = 9515m },
            new SyntheticDrgRate { DrgCode = "194", Description = "Simple pneumonia with CC", Weight = 1.06m, AllowedAmount = 5830m },
            new SyntheticDrgRate { DrgCode = "683", Description = "Renal failure with CC", Weight = 1.12m, AllowedAmount = 6160m },
            new SyntheticDrgRate { DrgCode = "766", Description = "Cesarean section without CC/MCC", Weight = 1.33m, AllowedAmount = 7315m },
            new SyntheticDrgRate { DrgCode = "775", Description = "Vaginal delivery without complications", Weight = 0.72m, AllowedAmount = 3960m },
            new SyntheticDrgRate { DrgCode = "917", Description = "Poisoning and toxic effects with MCC", Weight = 1.55m, AllowedAmount = 8525m },
            new SyntheticDrgRate { DrgCode = "378", Description = "GI hemorrhage with CC", Weight = 1.20m, AllowedAmount = 6600m },
            new SyntheticDrgRate { DrgCode = "189", Description = "Pulmonary edema and respiratory failure", Weight = 1.60m, AllowedAmount = 8800m },
            new SyntheticDrgRate { DrgCode = "065", Description = "Intracranial hemorrhage/cerebral infarction with CC", Weight = 1.85m, AllowedAmount = 10175m },
            new SyntheticDrgRate { DrgCode = "480", Description = "Hip/femur procedures without CC/MCC", Weight = 1.95m, AllowedAmount = 10725m },
            new SyntheticDrgRate { DrgCode = "419", Description = "Lap cholecystectomy without CC/MCC", Weight = 1.02m, AllowedAmount = 5610m },
            new SyntheticDrgRate { DrgCode = "795", Description = "Normal newborn", Weight = 0.18m, AllowedAmount = 3000m },
            new SyntheticDrgRate { DrgCode = "794", Description = "Neonate with other problems", Weight = 0.55m, AllowedAmount = 3025m },
            new SyntheticDrgRate { DrgCode = "603", Description = "Cellulitis without MCC", Weight = 0.80m, AllowedAmount = 4400m },
            new SyntheticDrgRate { DrgCode = "312", Description = "Syncope and collapse", Weight = 0.74m, AllowedAmount = 4070m },
            new SyntheticDrgRate { DrgCode = "313", Description = "Chest pain", Weight = 0.56m, AllowedAmount = 3080m },
            new SyntheticDrgRate { DrgCode = "641", Description = "Nutritional disorders without MCC", Weight = 0.72m, AllowedAmount = 3960m },
            new SyntheticDrgRate { DrgCode = "247", Description = "Percutaneous cardiovascular without MCC", Weight = 2.10m, AllowedAmount = 11550m },
            new SyntheticDrgRate { DrgCode = "190", Description = "COPD with MCC", Weight = 1.45m, AllowedAmount = 7975m },
            new SyntheticDrgRate { DrgCode = "193", Description = "Simple pneumonia with MCC", Weight = 1.50m, AllowedAmount = 8250m },
            new SyntheticDrgRate { DrgCode = "682", Description = "Renal failure with MCC", Weight = 1.78m, AllowedAmount = 9790m },
            new SyntheticDrgRate { DrgCode = "292", Description = "Heart failure and shock with CC", Weight = 1.15m, AllowedAmount = 6325m },
            new SyntheticDrgRate { DrgCode = "293", Description = "Heart failure without CC/MCC", Weight = 0.75m, AllowedAmount = 4125m },
            new SyntheticDrgRate { DrgCode = "069", Description = "TIA without thrombolytic", Weight = 0.84m, AllowedAmount = 4620m },
            new SyntheticDrgRate { DrgCode = "948", Description = "Signs and symptoms without MCC", Weight = 0.65m, AllowedAmount = 3575m },
            new SyntheticDrgRate { DrgCode = "191", Description = "COPD with CC", Weight = 0.98m, AllowedAmount = 5390m },
            new SyntheticDrgRate { DrgCode = "192", Description = "COPD without CC/MCC", Weight = 0.70m, AllowedAmount = 3850m },
            new SyntheticDrgRate { DrgCode = "689", Description = "Kidney and UTI with MCC", Weight = 1.44m, AllowedAmount = 7920m },
            new SyntheticDrgRate { DrgCode = "872", Description = "Septicemia without MV with CC", Weight = 1.55m, AllowedAmount = 8525m },
            new SyntheticDrgRate { DrgCode = "308", Description = "Cardiac arrhythmia with MCC", Weight = 1.15m, AllowedAmount = 6325m },
            new SyntheticDrgRate { DrgCode = "309", Description = "Cardiac arrhythmia with CC", Weight = 0.82m, AllowedAmount = 4510m },
            new SyntheticDrgRate { DrgCode = "310", Description = "Cardiac arrhythmia without CC/MCC", Weight = 0.55m, AllowedAmount = 3025m },
            new SyntheticDrgRate { DrgCode = "767", Description = "Vaginal delivery with sterilization/D&C", Weight = 1.10m, AllowedAmount = 6050m },
            new SyntheticDrgRate { DrgCode = "765", Description = "Cesarean section with CC/MCC", Weight = 1.80m, AllowedAmount = 9900m },
            new SyntheticDrgRate { DrgCode = "743", Description = "Uterine/adnexa procedures without CC/MCC", Weight = 1.15m, AllowedAmount = 6325m },
            new SyntheticDrgRate { DrgCode = "460", Description = "Spinal fusion without MCC", Weight = 3.50m, AllowedAmount = 19250m },
            new SyntheticDrgRate { DrgCode = "461", Description = "Bilateral joint replacement", Weight = 3.80m, AllowedAmount = 20900m },
            new SyntheticDrgRate { DrgCode = "853", Description = "Infectious disease with MCC", Weight = 1.68m, AllowedAmount = 9240m },
            new SyntheticDrgRate { DrgCode = "638", Description = "Diabetes with MCC", Weight = 1.25m, AllowedAmount = 6875m },
            new SyntheticDrgRate { DrgCode = "640", Description = "Misc nutritional disorders with MCC", Weight = 1.10m, AllowedAmount = 6050m },
            new SyntheticDrgRate { DrgCode = "563", Description = "FX/Sprains/Strains without MCC", Weight = 0.88m, AllowedAmount = 4840m },
            new SyntheticDrgRate { DrgCode = "885", Description = "Psychoses", Weight = 0.85m, AllowedAmount = 4675m },
            new SyntheticDrgRate { DrgCode = "897", Description = "Alcohol/drug abuse with rehab", Weight = 0.90m, AllowedAmount = 4950m },
            new SyntheticDrgRate { DrgCode = "791", Description = "Prematurity with major problems", Weight = 4.50m, AllowedAmount = 24750m },
            new SyntheticDrgRate { DrgCode = "789", Description = "Neonates died or transferred", Weight = 8.50m, AllowedAmount = 46750m },
            new SyntheticDrgRate { DrgCode = "003", Description = "ECMO or tracheostomy with MV", Weight = 9.00m, AllowedAmount = 49500m },
        });

        return fs;
    }

    /// <summary>
    /// Out-of-Network Fee Schedule — 150% of Medicaid rates.
    /// </summary>
    private static SyntheticFeeSchedule GenerateOonFeeSchedule(DateTime effectiveDate)
    {
        var medicaid = GenerateMedicaidFeeSchedule(effectiveDate);

        var oon = new SyntheticFeeSchedule
        {
            FeeScheduleId = "FS-OON",
            Name = "Out-of-Network Fee Schedule 2024",
            Type = "Custom",
            EffectiveDate = effectiveDate,
            PercentOfMedicare = 1.05m,
            DrgBaseRate = medicaid.DrgBaseRate * 1.5m,
        };

        // 150% of Medicaid rates
        foreach (var line in medicaid.Lines)
        {
            oon.Lines.Add(new SyntheticFeeScheduleLine
            {
                ProcedureCode = line.ProcedureCode,
                Modifier = line.Modifier,
                PlaceOfService = line.PlaceOfService,
                AllowedAmount = Math.Round(line.AllowedAmount * 1.50m, 2),
                RateType = line.RateType,
                EffectiveDate = effectiveDate,
                MaxUnitsPerDay = line.MaxUnitsPerDay,
                BilateralAdjustmentApplies = line.BilateralAdjustmentApplies,
                MultipleProcedureReductionApplies = line.MultipleProcedureReductionApplies,
            });
        }

        // 150% of DRG rates
        foreach (var drg in medicaid.DrgRates)
        {
            oon.DrgRates.Add(new SyntheticDrgRate
            {
                DrgCode = drg.DrgCode,
                Description = drg.Description,
                Weight = drg.Weight,
                AllowedAmount = Math.Round(drg.AllowedAmount * 1.50m, 2),
            });
        }

        return oon;
    }

    /// <summary>
    /// Capitation Schedule — PMPM rates by LOB/program.
    /// </summary>
    private static SyntheticFeeSchedule GenerateCapitationSchedule(DateTime effectiveDate)
    {
        return new SyntheticFeeSchedule
        {
            FeeScheduleId = "FS-CAPITATION",
            Name = "Capitation PMPM Schedule 2024",
            Type = "Capitation",
            EffectiveDate = effectiveDate,
            CapitationRates = new List<SyntheticCapitationRate>
            {
                new() { Program = "STAR", AgeRange = "Adult", PmpmRate = 250m },
                new() { Program = "STAR", AgeRange = "Child", PmpmRate = 150m },
                new() { Program = "CHIP", PmpmRate = 180m },
                new() { Program = "STAR+PLUS", PmpmRate = 1200m },
                new() { Program = "STAR Kids", PmpmRate = 800m },
                new() { Program = "STAR Health", PmpmRate = 350m },
            },
        };
    }

    private static SyntheticFeeScheduleLine CreateLine(string code, decimal amount, DateTime effectiveDate)
    {
        return new SyntheticFeeScheduleLine
        {
            ProcedureCode = code,
            AllowedAmount = amount,
            RateType = "FlatRate",
            EffectiveDate = effectiveDate,
        };
    }
}
