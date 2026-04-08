using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;

namespace CloudHealthOffice.PriorAuthRuleEngine.SeedRules;

/// <summary>
/// Platform-level seed rules for Texas Medicaid managed care programs.
/// These ship with CHO and apply to all tenants running TX Medicaid LOBs.
/// Tenants may layer overrides on top; they cannot remove platform rules
/// (they can disable them via the portal admin UI, which sets IsEnabled = false).
///
/// Sources:
///   STAR:     HHSC Uniform Managed Care Manual (UMCM) Chapter 4 — PA Requirements
///   STARPlus: UMCM Chapter 5 — LTSS PA Requirements
///   STARKids: UMCM Chapter 6 — Medically Dependent Children PA Requirements
///   Gold Card: Texas Insurance Code §4201.653 (HB 3229, eff. 09/01/2022)
///
/// Seed loaded by PriorAuthRuleEngineSeeder on first deployment per environment.
/// Subsequent updates are admin operations — rules are not re-seeded on restart.
/// </summary>
public static class TxMedicaidSeedRules
{
    public static IReadOnlyList<PaRuleDocument> GetAll() =>
        [.. Star(), .. StarPlus(), .. StarKids()];

    // ── STAR (TX Medicaid for children and families) ──────────────

    private static IEnumerable<PaRuleDocument> Star() =>
    [
        // Gold card exemption — applies across all TX STAR procedures
        new PaRuleDocument
        {
            RuleId       = "TX-STAR-REG-001",
            RuleName     = "TX Gold Card Exemption (HB 3229) — STAR",
            Description  = "Providers with ≥90% PA approval rate over 180 days are exempt " +
                           "from PA requirements per Texas Insurance Code §4201.653.",
            StateCode    = "TX",
            Lob          = PaLineOfBusiness.Medicaid,
            Program      = "STAR",
            TenantId     = null,   // platform rule
            Category     = RuleCategory.RegulatoryExemption,
            Scope        = RuleScope.Platform,
            Priority     = 1,
            RuleType     = "TxGoldCardExemption",
            GoldCardApprovalRateThreshold = 0.90m,
            GoldCardMinimumDecisions      = 20
        },

        // Chiropractic — visit limit
        new PaRuleDocument
        {
            RuleId       = "TX-STAR-QTY-001",
            RuleName     = "Chiropractic Visit Limit — STAR",
            Description  = "Chiropractic services (98940–98943) up to 20 visits/year " +
                           "auto-approved. Over 20 visits requires clinical review.",
            StateCode    = "TX",
            Lob          = PaLineOfBusiness.Medicaid,
            Program      = "STAR",
            TenantId     = null,
            Category     = RuleCategory.QuantityLimit,
            Scope        = RuleScope.Platform,
            Priority     = 20,
            RuleType     = "QuantityLimit",
            ProcedureCodes = ["98940", "98941", "98942", "98943"],
            VisitLimit   = 20
        },

        // PT / OT — visit limit
        new PaRuleDocument
        {
            RuleId       = "TX-STAR-QTY-002",
            RuleName     = "PT/OT Visit Limit — STAR",
            Description  = "Physical and occupational therapy (97001–97546 range) up to " +
                           "30 visits/year auto-approved. Over 30 requires clinical review.",
            StateCode    = "TX",
            Lob          = PaLineOfBusiness.Medicaid,
            Program      = "STAR",
            TenantId     = null,
            Category     = RuleCategory.QuantityLimit,
            Scope        = RuleScope.Platform,
            Priority     = 21,
            RuleType     = "QuantityLimit",
            ProcedureCodePrefixes = ["970", "971", "972"],
            VisitLimit   = 30
        },

        // Inpatient hospital admission — always requires PA
        new PaRuleDocument
        {
            RuleId       = "TX-STAR-PA-001",
            RuleName     = "Inpatient Admission PA Required — STAR",
            Description  = "All non-emergency inpatient admissions (POS 21) require " +
                           "prior authorization.",
            StateCode    = "TX",
            Lob          = PaLineOfBusiness.Medicaid,
            Program      = "STAR",
            TenantId     = null,
            Category     = RuleCategory.PlaceOfService,
            Scope        = RuleScope.Platform,
            Priority     = 40,
            RuleType     = "ProcedureRequiresAuth",
            PlaceOfServiceCodes = ["21"],   // inpatient hospital
            // No DenialCode — Pend for clinical review rather than hard deny
        },

        // Primary care office visits — exempt by provider type
        new PaRuleDocument
        {
            RuleId       = "TX-STAR-PCP-001",
            RuleName     = "PCP Office Visit Exemption — STAR",
            Description  = "Office/outpatient E&M visits (99201–99215) rendered by a " +
                           "primary care physician (taxonomy 207Q*, 207R*) are PA-exempt.",
            StateCode    = "TX",
            Lob          = PaLineOfBusiness.Medicaid,
            Program      = "STAR",
            TenantId     = null,
            Category     = RuleCategory.ProviderType,
            Scope        = RuleScope.Platform,
            Priority     = 60,
            RuleType     = "ProviderTypeExemption",
            ProcedureCodes = ["99201","99202","99203","99204","99205",
                              "99211","99212","99213","99214","99215"],
            ExemptTaxonomyPrefixes = ["207Q", "207R"]
        }
    ];

    // ── STARPlus (TX Medicaid LTSS for adults with disabilities) ──

    private static IEnumerable<PaRuleDocument> StarPlus() =>
    [
        // Gold card — STARPlus
        new PaRuleDocument
        {
            RuleId       = "TX-STARPLUS-REG-001",
            RuleName     = "TX Gold Card Exemption (HB 3229) — STARPlus",
            StateCode    = "TX",
            Lob          = PaLineOfBusiness.Medicaid,
            Program      = "STARPlus",
            TenantId     = null,
            Category     = RuleCategory.RegulatoryExemption,
            Scope        = RuleScope.Platform,
            Priority     = 1,
            RuleType     = "TxGoldCardExemption",
            GoldCardApprovalRateThreshold = 0.90m,
            GoldCardMinimumDecisions      = 20
        },

        // DME — cost threshold PA requirement
        new PaRuleDocument
        {
            RuleId       = "TX-STARPLUS-PA-001",
            RuleName     = "DME PA Required Above Threshold — STARPlus",
            Description  = "Durable medical equipment (K*, A*, E* HCPCS) with estimated " +
                           "cost > $500 requires prior authorization.",
            StateCode    = "TX",
            Lob          = PaLineOfBusiness.Medicaid,
            Program      = "STARPlus",
            TenantId     = null,
            Category     = RuleCategory.ClinicalCriteria,
            Scope        = RuleScope.Platform,
            Priority     = 10,
            RuleType     = "ProcedureRequiresAuth",
            ProcedureCodePrefixes = ["K", "A", "E"],
            // DenialCode intentionally null — Pend for clinical review
        },

        // Power wheelchair — diagnosis required
        new PaRuleDocument
        {
            RuleId       = "TX-STARPLUS-DX-001",
            RuleName     = "Power Wheelchair Diagnosis Requirement — STARPlus",
            Description  = "Power wheelchairs (K0800–K0899) require a qualifying " +
                           "neurological or musculoskeletal diagnosis.",
            StateCode    = "TX",
            Lob          = PaLineOfBusiness.Medicaid,
            Program      = "STARPlus",
            TenantId     = null,
            Category     = RuleCategory.DiagnosisRequired,
            Scope        = RuleScope.Platform,
            Priority     = 30,
            RuleType     = "DiagnosisRequired",
            ProcedureCodes = ["K0800","K0801","K0802","K0806","K0807","K0808",
                              "K0812","K0813","K0814","K0815","K0816","K0820",
                              "K0821","K0822","K0823","K0824","K0825","K0826",
                              "K0827","K0828","K0829","K0835","K0836","K0837",
                              "K0838","K0839","K0840","K0841","K0842","K0843",
                              "K0848","K0849","K0850","K0851","K0852","K0853",
                              "K0854","K0855","K0856","K0857","K0858","K0859",
                              "K0860","K0861","K0862","K0863","K0864","K0868",
                              "K0869","K0870","K0871","K0877","K0878","K0879",
                              "K0880","K0884","K0885","K0886","K0890","K0891",
                              "K0898","K0899"],
            RequiredDiagnosisCodes = [
                // Neurological
                "G12","G20","G35","G37","G80","G81","G82","G83",
                // Musculoskeletal
                "M05","M06","M08","M30","M32","M33","M34","M35",
                // Trauma
                "S14","S24","S34","T09"
            ]
        }
    ];

    // ── STARKids (TX Medicaid for medically dependent children) ───

    private static IEnumerable<PaRuleDocument> StarKids() =>
    [
        // Gold card — STARKids
        new PaRuleDocument
        {
            RuleId       = "TX-STARKIDS-REG-001",
            RuleName     = "TX Gold Card Exemption (HB 3229) — STARKids",
            StateCode    = "TX",
            Lob          = PaLineOfBusiness.Medicaid,
            Program      = "STARKids",
            TenantId     = null,
            Category     = RuleCategory.RegulatoryExemption,
            Scope        = RuleScope.Platform,
            Priority     = 1,
            RuleType     = "TxGoldCardExemption",
            GoldCardApprovalRateThreshold = 0.90m,
            GoldCardMinimumDecisions      = 20
        },

        // EPSDT — children under 21 have enhanced coverage; most services PA-exempt
        new PaRuleDocument
        {
            RuleId       = "TX-STARKIDS-AGE-001",
            RuleName     = "EPSDT Under-21 PA Exemption — STARKids",
            Description  = "Per EPSDT requirements, preventive, diagnostic, and treatment " +
                           "services for members under 21 (STARKids) do not require PA " +
                           "when medically necessary.",
            StateCode    = "TX",
            Lob          = PaLineOfBusiness.Medicaid,
            Program      = "STARKids",
            TenantId     = null,
            Category     = RuleCategory.MemberAge,
            Scope        = RuleScope.Platform,
            Priority     = 5,
            RuleType     = "MemberAgeLimit",
            MaxMemberAgeYears = 21,
            // Empty ProcedureCodes = all procedures
        }
    ];
}
