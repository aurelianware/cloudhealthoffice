using CloudHealthOffice.RiskAdjustmentEngine.Domain;

namespace CloudHealthOffice.RiskAdjustmentEngine.Data;

/// <summary>
/// Relative factors and demographic coefficients for the CMS-HCC v28 model
/// (Community, Non-Dual segment) and a representative HHS-HCC subset.
///
/// Source: CMS 2024 Medicare Advantage Rate Announcement — Attachment VII.
/// Full factor tables are published at cms.gov annually.
///
/// Factors shown are the Community Non-Dual Aged (CNDA) values used for the
/// majority of Medicare Advantage members. Separate factors exist for
/// institutional, new-enrollee, and dual segments.
/// </summary>
internal static class HccFactorData
{
    // ── CMS-HCC v28 HCC Relative Factors (Community Non-Dual) ────────────

    internal static readonly IReadOnlyDictionary<int, HccCategoryInfo> CmsHccV28Categories =
        new Dictionary<int, HccCategoryInfo>
        {
            [1]   = new(1,   "HIV/AIDS",                                           0.335m),
            [8]   = new(8,   "Metastatic Cancer and Acute Leukemia",               2.421m),
            [9]   = new(9,   "Lung, Upper Digestive Tract, and Other Severe Cancers", 1.048m),
            [10]  = new(10,  "Lymphoma and Other Cancers",                         0.674m),
            [17]  = new(17,  "Diabetes with Acute Complications",                  0.302m),
            [18]  = new(18,  "Diabetes with Chronic Complications",                0.263m),
            [19]  = new(19,  "Diabetes without Complication",                      0.136m),
            [22]  = new(22,  "Morbid Obesity",                                     0.272m),
            [55]  = new(55,  "Drug/Alcohol Dependence",                            0.383m),
            [58]  = new(58,  "Major Depressive, Bipolar, and Paranoid Disorders",  0.309m),
            [85]  = new(85,  "Congestive Heart Failure",                           0.323m),
            [86]  = new(86,  "Acute Myocardial Infarction",                        0.208m),
            [110] = new(110, "Chronic Obstructive Pulmonary Disease",              0.332m),
            [111] = new(111, "Asthma",                                             0.105m),
            [136] = new(136, "Chronic Kidney Disease Stage 5",                     0.289m),
            [137] = new(137, "Chronic Kidney Disease Stage 4",                     0.154m),
            [138] = new(138, "Chronic Kidney Disease Stage 3",                     0.090m),
        };

    // ── HHS-HCC Relative Factors (Silver plan, age 21-60 reference cell) ─

    internal static readonly IReadOnlyDictionary<int, HccCategoryInfo> HhsHccCategories =
        new Dictionary<int, HccCategoryInfo>
        {
            [18]  = new(18,  "Diabetes with Complications",      0.350m),
            [19]  = new(19,  "Diabetes without Complications",   0.190m),
            [130] = new(130, "Congestive Heart Failure",         0.420m),
            [161] = new(161, "COPD",                             0.310m),
            [163] = new(163, "Asthma",                           0.120m),
        };

    // ── CMS-HCC v28 Hierarchy Rules ───────────────────────────────────────
    // When the dominant HCC is present, subordinate HCCs in the same disease
    // group are removed (hierarchy applied after mapping, before scoring).

    internal static readonly IReadOnlyList<HccHierarchyRuleInfo> CmsHccV28Hierarchies =
    [
        new(17,  [18, 19]),        // DM acute > DM chronic > DM none
        new(18,  [19]),
        new(85,  [86]),            // CHF systolic/diastolic > CHF unspecified
        new(8,   [9, 10]),         // Metastatic > severe cancer > lymphoma
        new(9,   [10]),
        new(110, [111]),           // COPD > Asthma
        new(136, [137, 138]),      // CKD 5 > CKD 4 > CKD 3
        new(137, [138]),
    ];

    // ── CMS-HCC v28 Demographic Factors (Community Non-Dual) ─────────────
    // Age bands × Gender. Values are approximate CNDA coefficients from v28.

    internal static readonly IReadOnlyList<DemographicFactorInfo> CmsHccV28DemographicFactors =
    [
        // Male
        new(0,  34,  MemberGender.Male,   EnrollmentSegment.CommunityNonDual, 0.208m),
        new(35, 44,  MemberGender.Male,   EnrollmentSegment.CommunityNonDual, 0.272m),
        new(45, 54,  MemberGender.Male,   EnrollmentSegment.CommunityNonDual, 0.341m),
        new(55, 59,  MemberGender.Male,   EnrollmentSegment.CommunityNonDual, 0.395m),
        new(60, 64,  MemberGender.Male,   EnrollmentSegment.CommunityNonDual, 0.421m),
        new(65, 69,  MemberGender.Male,   EnrollmentSegment.CommunityNonDual, 0.453m),
        new(70, 74,  MemberGender.Male,   EnrollmentSegment.CommunityNonDual, 0.534m),
        new(75, 79,  MemberGender.Male,   EnrollmentSegment.CommunityNonDual, 0.618m),
        new(80, 84,  MemberGender.Male,   EnrollmentSegment.CommunityNonDual, 0.710m),
        new(85, 89,  MemberGender.Male,   EnrollmentSegment.CommunityNonDual, 0.784m),
        new(90, 94,  MemberGender.Male,   EnrollmentSegment.CommunityNonDual, 0.840m),
        new(95, 999, MemberGender.Male,   EnrollmentSegment.CommunityNonDual, 0.873m),
        // Female
        new(0,  34,  MemberGender.Female, EnrollmentSegment.CommunityNonDual, 0.195m),
        new(35, 44,  MemberGender.Female, EnrollmentSegment.CommunityNonDual, 0.259m),
        new(45, 54,  MemberGender.Female, EnrollmentSegment.CommunityNonDual, 0.315m),
        new(55, 59,  MemberGender.Female, EnrollmentSegment.CommunityNonDual, 0.365m),
        new(60, 64,  MemberGender.Female, EnrollmentSegment.CommunityNonDual, 0.390m),
        new(65, 69,  MemberGender.Female, EnrollmentSegment.CommunityNonDual, 0.417m),
        new(70, 74,  MemberGender.Female, EnrollmentSegment.CommunityNonDual, 0.486m),
        new(75, 79,  MemberGender.Female, EnrollmentSegment.CommunityNonDual, 0.563m),
        new(80, 84,  MemberGender.Female, EnrollmentSegment.CommunityNonDual, 0.648m),
        new(85, 89,  MemberGender.Female, EnrollmentSegment.CommunityNonDual, 0.729m),
        new(90, 94,  MemberGender.Female, EnrollmentSegment.CommunityNonDual, 0.786m),
        new(95, 999, MemberGender.Female, EnrollmentSegment.CommunityNonDual, 0.823m),
    ];
}

// ── Internal DTOs (data-layer only) ────────────────────────────────────────

internal record HccCategoryInfo(int Code, string Description, decimal Factor);
internal record HccHierarchyRuleInfo(int Dominant, int[] Subordinates);
internal record DemographicFactorInfo(int AgeFrom, int AgeTo, MemberGender Gender,
    EnrollmentSegment Segment, decimal Factor);
