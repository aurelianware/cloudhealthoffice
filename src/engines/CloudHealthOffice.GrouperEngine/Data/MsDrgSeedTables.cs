using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Models;

namespace CloudHealthOffice.GrouperEngine.Data;

/// <summary>
/// A hand-built slice of the MS-DRG tables, for development and for the
/// grouper's own tests.
///
/// <para>
/// <b>This is not the CMS table set.</b> It covers four families out of about
/// 330 and a few dozen codes out of about 74,000, and it exists so the
/// grouping logic can be exercised end to end before the definitions-manual
/// importer lands (ADR 024 §5 step 2). The DRG numbers, titles, MDCs and
/// severity splits in it are real; the CC exclusion row is illustrative and
/// is marked as such. Nothing here may be used to pay a claim — a tenant with
/// no imported CMS version must group to
/// <see cref="GroupingStatus.NoTablesForDischargeDate"/> rather than fall back
/// to this.
/// </para>
/// </summary>
public static class MsDrgSeedTables
{
    /// <summary>Version label for the seed slice. Deliberately not a CMS version number.</summary>
    public const string SeedVersion = "SEED-FY2026";

    /// <summary>Builds the seed table set, covering discharges in FY 2026.</summary>
    public static MsDrgTableSet Build() => Build(
        new DateOnly(2025, 10, 1), new DateOnly(2026, 9, 30), SeedVersion);

    /// <summary>Builds the seed table set over a caller-chosen window and version label.</summary>
    public static MsDrgTableSet Build(DateOnly effectiveFrom, DateOnly? effectiveThrough, string version)
    {
        var drgs = new List<MsDrgDefinition>
        {
            // ── Pre-MDC: heart transplant.
            new() { Drg = "001", Title = "Heart Transplant or Implant of Heart Assist System with MCC",
                    Mdc = null, Partition = DrgPartition.PreMdc,
                    FamilyKey = Families.HeartTransplant, Severity = DrgSeverity.WithMcc },
            new() { Drg = "002", Title = "Heart Transplant or Implant of Heart Assist System without MCC",
                    Mdc = null, Partition = DrgPartition.PreMdc,
                    FamilyKey = Families.HeartTransplant, Severity = DrgSeverity.WithoutCcMcc },

            // ── MDC 04, medical: simple pneumonia and pleurisy. A three-way split.
            new() { Drg = "193", Title = "Simple Pneumonia and Pleurisy with MCC",
                    Mdc = 4, Partition = DrgPartition.Medical,
                    FamilyKey = Families.SimplePneumonia, Severity = DrgSeverity.WithMcc },
            new() { Drg = "194", Title = "Simple Pneumonia and Pleurisy with CC",
                    Mdc = 4, Partition = DrgPartition.Medical,
                    FamilyKey = Families.SimplePneumonia, Severity = DrgSeverity.WithCc },
            new() { Drg = "195", Title = "Simple Pneumonia and Pleurisy without CC/MCC",
                    Mdc = 4, Partition = DrgPartition.Medical,
                    FamilyKey = Families.SimplePneumonia, Severity = DrgSeverity.WithoutCcMcc },

            // ── MDC 05, medical: heart failure and shock. A three-way split.
            new() { Drg = "291", Title = "Heart Failure and Shock with MCC",
                    Mdc = 5, Partition = DrgPartition.Medical,
                    FamilyKey = Families.HeartFailure, Severity = DrgSeverity.WithMcc },
            new() { Drg = "292", Title = "Heart Failure and Shock with CC",
                    Mdc = 5, Partition = DrgPartition.Medical,
                    FamilyKey = Families.HeartFailure, Severity = DrgSeverity.WithCc },
            new() { Drg = "293", Title = "Heart Failure and Shock without CC/MCC",
                    Mdc = 5, Partition = DrgPartition.Medical,
                    FamilyKey = Families.HeartFailure, Severity = DrgSeverity.WithoutCcMcc },

            // ── MDC 08, surgical: major joint replacement. A two-way split, so a
            //    stay with a CC and no MCC pays at "without".
            new() { Drg = "469", Title = "Major Hip and Knee Joint Replacement or Reattachment of Lower Extremity with MCC or Total Ankle Replacement",
                    Mdc = 8, Partition = DrgPartition.Surgical,
                    FamilyKey = Families.MajorJointLower, Severity = DrgSeverity.WithMcc },
            new() { Drg = "470", Title = "Major Hip and Knee Joint Replacement or Reattachment of Lower Extremity without MCC",
                    Mdc = 8, Partition = DrgPartition.Surgical,
                    FamilyKey = Families.MajorJointLower, Severity = DrgSeverity.WithoutCcMcc },
        };

        var mdcAssignments = new List<MdcAssignment>
        {
            // MDC 04 — respiratory.
            new("J189", 4),   // Pneumonia, unspecified organism
            new("J181", 4),   // Lobar pneumonia, unspecified organism
            new("J690", 4),   // Pneumonitis due to inhalation of food and vomit

            // MDC 05 — circulatory.
            new("I509", 5),   // Heart failure, unspecified
            new("I5021", 5),  // Acute systolic (congestive) heart failure
            new("I5033", 5),  // Acute on chronic diastolic (congestive) heart failure
            new("I5043", 5),  // Acute on chronic combined systolic and diastolic heart failure

            // MDC 08 — musculoskeletal.
            new("M1711", 8),  // Unilateral primary osteoarthritis, right knee
            new("M1712", 8),  // Unilateral primary osteoarthritis, left knee
            new("M1611", 8),  // Unilateral primary osteoarthritis, right hip

            // An MCE unacceptable principal diagnosis: a code that may be
            // reported but cannot be the reason for an admission.
            new("Z0289", 21, AcceptableAsPrincipal: false),
        };

        var complications = new List<ComplicationAssignment>
        {
            // MCCs.
            new("A419", ComplicationLevel.Mcc),   // Sepsis, unspecified organism
            new("J9600", ComplicationLevel.Mcc),  // Acute respiratory failure, unspecified
            new("J9601", ComplicationLevel.Mcc),  // Acute respiratory failure with hypoxia
            new("N170", ComplicationLevel.Mcc),   // Acute kidney failure with tubular necrosis

            // CCs.
            new("N179", ComplicationLevel.Cc),    // Acute kidney failure, unspecified
            new("E1165", ComplicationLevel.Cc),   // Type 2 diabetes with hyperglycaemia
            new("D62", ComplicationLevel.Cc),     // Acute post-haemorrhagic anaemia
            new("J181", ComplicationLevel.Cc),    // Lobar pneumonia, unspecified organism
        };

        var ccExclusions = new List<CcExclusion>
        {
            // Illustrative, not the CMS exclusion list: a second pneumonia code
            // adds nothing to a pneumonia admission. The real list has roughly
            // 170,000 pairs and arrives with the importer.
            new("J189", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "J181" }),
        };

        var surgicalClasses = new List<SurgicalClassAssignment>
        {
            // Rank 1: major joint replacement of the lower extremity.
            new("0SR9019", 8, SurgicalClassKeys.MajorJointLower, 1),  // Replacement right hip, metal, cemented, open
            new("0SRB019", 8, SurgicalClassKeys.MajorJointLower, 1),  // Replacement left hip, metal, cemented, open
            new("0SRC0J9", 8, SurgicalClassKeys.MajorJointLower, 1),  // Replacement right knee, synthetic, cemented, open
            new("0SRD0J9", 8, SurgicalClassKeys.MajorJointLower, 1),  // Replacement left knee, synthetic, cemented, open
        };

        var familyRules = new List<DrgFamilyRule>
        {
            // Pre-MDC: a heart transplant, whatever the principal diagnosis.
            new()
            {
                Rank = 1,
                Partition = DrgPartition.PreMdc,
                FamilyKey = Families.HeartTransplant,
                RequiredProcedures = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "02YA0Z0" },
            },

            new() { Rank = 1, Mdc = 4, Partition = DrgPartition.Medical, FamilyKey = Families.SimplePneumonia },
            new() { Rank = 1, Mdc = 5, Partition = DrgPartition.Medical, FamilyKey = Families.HeartFailure },
            new()
            {
                Rank = 1, Mdc = 8, Partition = DrgPartition.Surgical,
                FamilyKey = Families.MajorJointLower,
                SurgicalClass = SurgicalClassKeys.MajorJointLower,
            },
        };

        return new MsDrgTableSet
        {
            Version = version,
            EffectiveFrom = effectiveFrom,
            EffectiveThrough = effectiveThrough,
            Drgs = drgs.ToDictionary(d => d.Drg, StringComparer.OrdinalIgnoreCase),
            MdcAssignments = mdcAssignments.ToDictionary(m => m.Code, StringComparer.OrdinalIgnoreCase),
            Complications = complications.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase),
            CcExclusions = ccExclusions.ToDictionary(c => c.PrincipalCode, StringComparer.OrdinalIgnoreCase),
            SurgicalClasses = surgicalClasses
                .GroupBy(s => s.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<SurgicalClassAssignment>)g.ToList(),
                    StringComparer.OrdinalIgnoreCase),
            FamilyRules = familyRules,
            KnownDiagnoses = mdcAssignments.Select(m => m.Code)
                .Concat(complications.Select(c => c.Code))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>Family keys in the seed slice.</summary>
    public static class Families
    {
        public const string HeartTransplant = "HEART_TRANSPLANT";
        public const string SimplePneumonia = "SIMPLE_PNEUMONIA";
        public const string HeartFailure = "HEART_FAILURE";
        public const string MajorJointLower = "MAJOR_JOINT_LOWER";
    }

    /// <summary>Surgical class keys in the seed slice.</summary>
    public static class SurgicalClassKeys
    {
        public const string MajorJointLower = "MAJOR_JOINT_LOWER";
    }
}
