using CloudHealthOffice.NcciEngine.Domain;

namespace CloudHealthOffice.NcciEngine.Data;

/// <summary>
/// Seed data for common high-frequency NCCI edit pairs and MUE entries.
///
/// This data is derived from publicly available CMS NCCI Policy Manual
/// and CMS NCCI edit tables (published quarterly at cms.gov).
///
/// Purpose: provides a functional baseline so that new tenant environments
/// can begin scrubbing immediately without waiting for a full CMS import.
/// Production environments should overlay this seed data with the current
/// quarterly CMS files via INcciEditService.ImportQuarterlyUpdateAsync().
///
/// Effective date: 2025-01-01 (Q1 2025 baseline).
/// </summary>
public static class NcciSeedData
{
    private static readonly DateTime Q1_2025 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Build seed NCCI edit pairs for a specific tenant.
    /// </summary>
    public static List<NcciEditPair> BuildNcciPairs(string tenantId)
    {
        var pairs = new List<(string col1, string col2, NcciModifierIndicator mi, NcciPolicyType policy)>
        {
            // ── Evaluation & Management bundled with minor procedures ──────
            // 99213 (office visit, level 3) bundles routine minor procedures
            ("99213", "20600", NcciModifierIndicator.Allowed,     NcciPolicyType.ProcedureToProc), // arthrocentesis small joint
            ("99213", "20605", NcciModifierIndicator.Allowed,     NcciPolicyType.ProcedureToProc), // arthrocentesis intermediate joint
            ("99213", "93000", NcciModifierIndicator.Allowed,     NcciPolicyType.ProcedureToProc), // ECG, routine
            ("99213", "36415", NcciModifierIndicator.NotAllowed,  NcciPolicyType.ProcedureToProc), // venipuncture (routine draw)
            ("99214", "36415", NcciModifierIndicator.NotAllowed,  NcciPolicyType.ProcedureToProc),
            ("99215", "36415", NcciModifierIndicator.NotAllowed,  NcciPolicyType.ProcedureToProc),

            // ── Surgery bundling (component → comprehensive) ───────────────
            // Laparoscopic cholecystectomy bundles diagnostic laparoscopy
            ("47563", "49320", NcciModifierIndicator.NotAllowed,  NcciPolicyType.ProcedureToProc),
            // Appendectomy bundles lysis of adhesions
            ("44950", "44005", NcciModifierIndicator.Allowed,     NcciPolicyType.ProcedureToProc),
            // Cataract with IOL bundles anterior vitrectomy
            ("66984", "67005", NcciModifierIndicator.Allowed,     NcciPolicyType.ProcedureToProc),

            // ── Colonoscopy/Sigmoidoscopy ──────────────────────────────────
            // Colonoscopy with biopsy bundles diagnostic colonoscopy
            ("45380", "45378", NcciModifierIndicator.NotAllowed,  NcciPolicyType.ProcedureToProc),
            // Colonoscopy with polypectomy bundles diagnostic colonoscopy
            ("45385", "45378", NcciModifierIndicator.NotAllowed,  NcciPolicyType.ProcedureToProc),
            // Colonoscopy with ablation bundles polypectomy
            ("45388", "45385", NcciModifierIndicator.NotAllowed,  NcciPolicyType.ProcedureToProc),

            // ── Anesthesia / Modifier 26/TC ────────────────────────────────
            // Professional + technical component of same study = mutually exclusive
            ("70553", "70551", NcciModifierIndicator.Allowed,     NcciPolicyType.MutuallyExclusive), // MRI brain with/without contrast
            ("70553", "70552", NcciModifierIndicator.Allowed,     NcciPolicyType.MutuallyExclusive), // MRI brain with contrast

            // ── Radiology ─────────────────────────────────────────────────
            // CT chest with contrast bundles CT chest without contrast
            ("71250", "71046", NcciModifierIndicator.NotAllowed,  NcciPolicyType.ProcedureToProc),
            ("71260", "71046", NcciModifierIndicator.NotAllowed,  NcciPolicyType.ProcedureToProc),
            // CT abdomen/pelvis combined bundles individual CTs
            ("74178", "74176", NcciModifierIndicator.NotAllowed,  NcciPolicyType.ProcedureToProc),
            ("74178", "72193", NcciModifierIndicator.NotAllowed,  NcciPolicyType.ProcedureToProc),

            // ── Wound care ────────────────────────────────────────────────
            // Complex repair bundles simple repair of same wound
            ("13160", "12035", NcciModifierIndicator.NotAllowed,  NcciPolicyType.ProcedureToProc),
            // Debridement bundles wound check at same encounter
            ("97597", "97602", NcciModifierIndicator.Allowed,     NcciPolicyType.ProcedureToProc),

            // ── Injection / Infusion ──────────────────────────────────────
            // Initial infusion bundles additional infusion of same drug
            ("96413", "96415", NcciModifierIndicator.NotAllowed,  NcciPolicyType.ProcedureToProc),
            // Chemotherapy infusion — initial bundles additional hour
            ("96409", "96411", NcciModifierIndicator.NotAllowed,  NcciPolicyType.ProcedureToProc),

            // ── Physical Medicine ─────────────────────────────────────────
            // Therapeutic exercises bundle hot/cold pack (same encounter)
            ("97110", "97010", NcciModifierIndicator.NotAllowed,  NcciPolicyType.ProcedureToProc),
            ("97140", "97010", NcciModifierIndicator.NotAllowed,  NcciPolicyType.ProcedureToProc),

            // ── Mutually exclusive pairs ──────────────────────────────────
            // Bilateral and unilateral heart catheterization
            ("93460", "93458", NcciModifierIndicator.Allowed,     NcciPolicyType.MutuallyExclusive),
            ("93460", "93459", NcciModifierIndicator.Allowed,     NcciPolicyType.MutuallyExclusive),
            ("93461", "93460", NcciModifierIndicator.Allowed,     NcciPolicyType.MutuallyExclusive),
        };

        return pairs.Select((p, i) => new NcciEditPair
        {
            Id = MakePairId(tenantId, p.col1, p.col2, Q1_2025),
            TenantId = tenantId,
            Column1Code = p.col1,
            Column2Code = p.col2,
            ModifierIndicator = p.mi,
            PolicyType = p.policy,
            EffectiveDate = Q1_2025,
            TerminationDate = null,
        }).ToList();
    }

    /// <summary>
    /// Build seed MUE entries for a specific tenant.
    /// Values are representative of the CMS 2025 Q1 MUE file.
    /// </summary>
    public static List<MueEntry> BuildMueEntries(string tenantId)
    {
        // (code, maxUnits, mai, professional, outpatientFacility)
        var entries = new List<(string code, int max, MueAdjudicationIndicator mai, bool prof, bool fac)>
        {
            // ── E&M ───────────────────────────────────────────────────────
            ("99213", 1, MueAdjudicationIndicator.DateOfService, true,  false),
            ("99214", 1, MueAdjudicationIndicator.DateOfService, true,  false),
            ("99215", 1, MueAdjudicationIndicator.DateOfService, true,  false),

            // ── Injections / Infusions ────────────────────────────────────
            ("96413", 1, MueAdjudicationIndicator.DateOfService, true,  true),
            ("96415", 8, MueAdjudicationIndicator.DateOfService, true,  true),
            ("96409", 1, MueAdjudicationIndicator.DateOfService, true,  true),

            // ── Radiology ─────────────────────────────────────────────────
            ("70553", 1, MueAdjudicationIndicator.DateOfServiceAbsolute, true, true),
            ("71260", 1, MueAdjudicationIndicator.DateOfServiceAbsolute, true, true),
            ("74178", 1, MueAdjudicationIndicator.DateOfServiceAbsolute, true, true),

            // ── Surgery ───────────────────────────────────────────────────
            ("47563", 1, MueAdjudicationIndicator.DateOfServiceAbsolute, true, true),
            ("44950", 1, MueAdjudicationIndicator.DateOfServiceAbsolute, true, true),
            ("66984", 1, MueAdjudicationIndicator.DateOfServiceAbsolute, true, true),
            ("45385", 1, MueAdjudicationIndicator.DateOfServiceAbsolute, true, true),

            // ── Physical Medicine ─────────────────────────────────────────
            ("97110", 8, MueAdjudicationIndicator.DateOfService, true,  false),
            ("97140", 8, MueAdjudicationIndicator.DateOfService, true,  false),
            ("97010", 1, MueAdjudicationIndicator.DateOfService, true,  false),

            // ── Lab / Path ────────────────────────────────────────────────
            ("80053", 1, MueAdjudicationIndicator.DateOfServiceAbsolute, true, true), // comprehensive metabolic panel
            ("85025", 1, MueAdjudicationIndicator.DateOfServiceAbsolute, true, true), // CBC with differential
            ("87491", 2, MueAdjudicationIndicator.DateOfService,         true, true), // chlamydia trachomatis

            // ── Wound Care ────────────────────────────────────────────────
            ("97597", 1, MueAdjudicationIndicator.DateOfService, true, true),
            ("13160", 1, MueAdjudicationIndicator.DateOfServiceAbsolute, true, true),

            // ── Venipuncture ──────────────────────────────────────────────
            ("36415", 1, MueAdjudicationIndicator.DateOfService, true, true),

            // ── ECG ───────────────────────────────────────────────────────
            ("93000", 1, MueAdjudicationIndicator.DateOfService, true, false),
        };

        return entries.Select(e => new MueEntry
        {
            Id = MakeMueId(tenantId, e.code, Q1_2025),
            TenantId = tenantId,
            ProcedureCode = e.code,
            MaxUnits = e.max,
            AdjudicationIndicator = e.mai,
            AppliesToProfessional = e.prof,
            AppliesToOutpatientFacility = e.fac,
            EffectiveDate = Q1_2025,
            TerminationDate = null,
        }).ToList();
    }

    // ── Stable document ID helpers ─────────────────────────────────

    public static string MakePairId(string tenantId, string col1, string col2, DateTime effective)
        => $"{tenantId}_{col1}_{col2}_{effective:yyyyMMdd}";

    public static string MakeMueId(string tenantId, string code, DateTime effective)
        => $"{tenantId}_{code}_{effective:yyyyMMdd}";
}
