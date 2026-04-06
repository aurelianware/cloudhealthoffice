namespace CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

/// <summary>
/// UB-04 revenue codes organized by institutional claim sub-type.
/// </summary>
internal static class RevenueCodeSets
{
    /// <summary>Inpatient revenue codes.</summary>
    internal static readonly (string Code, string Description)[] Inpatient =
    {
        ("0110", "Room and Board — Private"),
        ("0120", "Room and Board — Semi-Private"),
        ("0150", "Room and Board — ICU"),
        ("0200", "ICU — General"),
        ("0250", "Pharmacy — General"),
        ("0260", "IV Therapy"),
        ("0270", "Medical/Surgical Supplies"),
        ("0300", "Laboratory — Clinical"),
        ("0320", "Radiology — Diagnostic"),
        ("0350", "CT Scan"),
        ("0370", "Anesthesia"),
        ("0390", "Blood and Blood Products"),
        ("0710", "Operating Room"),
        ("0720", "Recovery Room")
    };

    /// <summary>Outpatient revenue codes.</summary>
    internal static readonly (string Code, string Description)[] Outpatient =
    {
        ("0250", "Pharmacy — General"),
        ("0260", "IV Therapy"),
        ("0270", "Medical/Surgical Supplies"),
        ("0300", "Laboratory — Clinical"),
        ("0320", "Radiology — Diagnostic"),
        ("0350", "CT Scan"),
        ("0360", "OR Services"),
        ("0450", "Emergency Room"),
        ("0510", "Clinic — General"),
        ("0636", "Drugs Requiring Detailed Coding")
    };

    /// <summary>Emergency department revenue codes.</summary>
    internal static readonly (string Code, string Description)[] Emergency =
    {
        ("0450", "Emergency Room — General"),
        ("0451", "Emergency Room — EMTALA"),
        ("0452", "Emergency Room — Beyond EMTALA"),
        ("0250", "Pharmacy — General"),
        ("0270", "Medical/Surgical Supplies"),
        ("0300", "Laboratory — Clinical"),
        ("0320", "Radiology — Diagnostic"),
        ("0350", "CT Scan")
    };

    /// <summary>Observation revenue codes.</summary>
    internal static readonly (string Code, string Description)[] Observation =
    {
        ("0762", "Observation Room"),
        ("0250", "Pharmacy — General"),
        ("0270", "Medical/Surgical Supplies"),
        ("0300", "Laboratory — Clinical"),
        ("0320", "Radiology — Diagnostic")
    };

    /// <summary>Skilled nursing facility revenue codes.</summary>
    internal static readonly (string Code, string Description)[] SkilledNursing =
    {
        ("0191", "SNF — Subacute Care"),
        ("0192", "SNF — Level II"),
        ("0193", "SNF — Level III"),
        ("0250", "Pharmacy — General"),
        ("0270", "Medical/Surgical Supplies"),
        ("0420", "Physical Therapy"),
        ("0430", "Occupational Therapy"),
        ("0440", "Speech-Language Pathology")
    };
}
