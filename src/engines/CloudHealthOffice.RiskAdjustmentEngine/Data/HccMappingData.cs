using CloudHealthOffice.RiskAdjustmentEngine.Domain;

namespace CloudHealthOffice.RiskAdjustmentEngine.Data;

/// <summary>
/// Representative subset of the CMS-HCC v28 ICD-10-CM to HCC crosswalk.
///
/// Source: CMS Medicare Advantage Risk Adjustment Data (CMS-HCC v28, 2024).
/// Full crosswalk is published annually at: cms.gov/medicare/health-plans/medicareadvtgspecratestats
///
/// In production, load the full file (~9,000 ICD-10 codes) from the database or
/// a seeded table. This subset covers the most common disease groups for testing.
/// </summary>
internal static class HccMappingData
{
    /// <summary>
    /// CMS-HCC v28 ICD-10 to HCC mappings.
    /// Format: (ICD10Code, HccCategoryCode)
    /// </summary>
    internal static readonly IReadOnlyList<(string Icd10, int HccCode)> CmsHccV28Mappings =
    [
        // ── HCC 1 — HIV/AIDS ──────────────────────────────────────────────
        ("B20",   1),
        ("Z21",   1),

        // ── HCC 8 — Metastatic Cancer and Acute Leukemia ──────────────────
        ("C770",   8), ("C771",  8), ("C772",  8), ("C773",  8), ("C774",  8),
        ("C7800",  8), ("C7801", 8), ("C781",  8), ("C782",  8), ("C7830", 8),
        ("C784",   8), ("C785",  8), ("C786",  8), ("C787",  8), ("C7889", 8),
        ("C791",   8), ("C7931", 8),
        ("C9100",  8), ("C9110", 8), // Acute lymphoblastic leukemia

        // ── HCC 9 — Lung, Upper Digestive Tract, and Other Severe Cancers ──
        ("C3410",  9), ("C3411", 9), ("C3412", 9), ("C3490", 9), ("C3491", 9),
        ("C1500",  9), ("C1501", 9), ("C1503", 9), ("C1504", 9), // Esophagus
        ("C160",   9), ("C161",  9), ("C162",  9),               // Stomach

        // ── HCC 10 — Lymphoma and Other Cancers ───────────────────────────
        ("C8310",  10), ("C8311", 10), ("C8319", 10),   // DLBCL
        ("C8200",  10), ("C8201", 10),                  // Follicular lymphoma
        ("C9000",  10), ("C9001", 10), ("C9002", 10),   // Multiple myeloma

        // ── HCC 17 — Diabetes with Acute Complications ────────────────────
        ("E1010",  17), ("E1011", 17),  // T1D with ketoacidosis
        ("E1110",  17), ("E1111", 17),  // T2D with ketoacidosis

        // ── HCC 18 — Diabetes with Chronic Complications ──────────────────
        ("E1022",  18), ("E1040", 18), ("E1041", 18), ("E1049", 18), // T1D CKD
        ("E1051",  18), ("E1052", 18),                               // T1D circulatory
        ("E1122",  18), ("E1140", 18), ("E1141", 18), ("E1149", 18), // T2D CKD
        ("E1151",  18), ("E1152", 18),                               // T2D circulatory
        ("E1165",  18), ("E1169", 18),                               // T2D with complications

        // ── HCC 19 — Diabetes without Complication ────────────────────────
        ("E109",   19),  // T1D without complication
        ("E119",   19),  // T2D without complication
        ("E139",   19),  // Other DM without complication

        // ── HCC 85 — Congestive Heart Failure ────────────────────────────
        ("I501",   85),  // Left ventricular failure
        ("I5020",  85), ("I5021", 85), ("I5022", 85), ("I5023", 85), // Systolic HF
        ("I5030",  85), ("I5031", 85), ("I5032", 85), ("I5033", 85), // Diastolic HF
        ("I5040",  85), ("I5041", 85), ("I5042", 85), ("I5043", 85), // Combined HF

        // ── HCC 86 — Acute Myocardial Infarction ─────────────────────────
        ("I2101",  86), ("I2102", 86), ("I2109", 86), // STEMI
        ("I214",   86), ("I219",  86),                // NSTEMI / unspecified AMI
        ("I509",   86),                               // CHF unspecified

        // ── HCC 110 — Chronic Obstructive Pulmonary Disease ───────────────
        ("J440",  110), ("J441", 110), ("J449", 110), // COPD
        ("J961",  110), ("J9620", 110),               // Chronic respiratory failure

        // ── HCC 111 — Asthma ──────────────────────────────────────────────
        ("J4520", 111), ("J4521", 111), ("J4522", 111), // Mild intermittent asthma
        ("J4530", 111), ("J4531", 111), ("J4532", 111), // Mild persistent asthma
        ("J4540", 111), ("J4541", 111), ("J4542", 111), // Moderate persistent asthma
        ("J4550", 111), ("J4551", 111), ("J4552", 111), // Severe persistent asthma

        // ── HCC 136 — Chronic Kidney Disease Stage 5 ──────────────────────
        ("N185",  136),  // CKD stage 5
        ("N186",  136),  // End stage renal disease (ESRD)

        // ── HCC 137 — Chronic Kidney Disease Stage 4 ──────────────────────
        ("N184",  137),

        // ── HCC 138 — Chronic Kidney Disease Stage 3 ──────────────────────
        ("N1831", 138), ("N1832", 138), // CKD stage 3a / 3b

        // ── HCC 22 — Morbid Obesity ───────────────────────────────────────
        ("E6601",  22), ("E6609", 22), // Morbid obesity

        // ── HCC 55 — Drug/Alcohol Dependence ─────────────────────────────
        ("F1020",  55), ("F1120", 55), ("F1220", 55), // Opioid / alcohol / cocaine dependence

        // ── HCC 58 — Major Depressive, Bipolar, and Paranoid Disorders ────
        ("F3110",  58), ("F3111", 58), ("F3112", 58), // Bipolar I
        ("F3220",  58), ("F3221", 58),                // Major depressive disorder, recurrent
    ];

    /// <summary>
    /// HHS-HCC (ACA Marketplace) ICD-10 mappings — abbreviated subset for testing.
    /// Full crosswalk: regtap.info (REGTAP library).
    /// </summary>
    internal static readonly IReadOnlyList<(string Icd10, int HccCode)> HhsHccMappings =
    [
        // HHS HCC 19 — Diabetes without Complication
        ("E109",  19), ("E119",  19),
        // HHS HCC 18 — Diabetes with Chronic Complications
        ("E1040", 18), ("E1140", 18),
        // HHS HCC 161 — Chronic Obstructive Pulmonary Disease
        ("J440", 161), ("J441", 161), ("J449", 161),
        // HHS HCC 163 — Asthma
        ("J4520", 163), ("J4530", 163), ("J4540", 163),
        // HHS HCC 130 — Congestive Heart Failure
        ("I501", 130), ("I5020", 130), ("I5021", 130),
    ];
}
