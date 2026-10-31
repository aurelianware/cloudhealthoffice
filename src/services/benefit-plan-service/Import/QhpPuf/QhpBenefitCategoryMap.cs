namespace BenefitPlanService.Import.QhpPuf;

/// <summary>
/// Maps QHP PUF <c>BenefitName</c> values (the HIOS Plans &amp; Benefits
/// template's benefit list) onto the CHO service categories that claim
/// adjudication joins on: the <c>serviceTypeCode</c> values in
/// <c>schemas/service-category-mappings/system-defaults.json</c>. A plan's
/// <c>Benefit.ServiceCategory</c> must equal one of those labels for a claim
/// line to find its cost share, so the importer writes those labels and not
/// the QHP names.
///
/// <para>
/// Each CHO category lists its QHP sources in order of preference. The first
/// one the plan has is used; the others are reported as "folded" so a reviewer
/// can see, for example, that a plan's MRI copay differs from its X-ray copay
/// but CHO took the X-ray row for the Imaging category. One QHP row can feed
/// more than one category (the outpatient facility fee feeds both Outpatient
/// Surgery and Outpatient Hospital).
/// </para>
/// </summary>
public static class QhpBenefitCategoryMap
{
    /// <summary>A CHO benefit built from a QHP benefit row.</summary>
    public sealed record Target(string ChoCategory, string[] QhpSources, QhpBenefitKind Kind, string? FormularyTier = null);

    public enum QhpBenefitKind { Medical, Preventive, Pharmacy, PharmacyTier }

    /// <summary>Adjudication categories, in the order they appear on the plan.</summary>
    public static readonly IReadOnlyList<Target> Targets =
    [
        new("Office Visit", ["Primary Care Visit to Treat an Injury or Illness"], QhpBenefitKind.Medical),
        new("Specialist Visit", ["Specialist Visit", "Specialist Office Visit"], QhpBenefitKind.Medical),
        new("Preventive Care", ["Preventive Care/Screening/Immunization"], QhpBenefitKind.Preventive),
        new("Urgent Care", ["Urgent Care Centers or Facilities"], QhpBenefitKind.Medical),
        new("Emergency Room", ["Emergency Room Services"], QhpBenefitKind.Medical),
        new("Ambulance", ["Emergency Transportation/Ambulance"], QhpBenefitKind.Medical),
        new("Inpatient Hospital", ["Inpatient Hospital Services (e.g., Hospital Stay)"], QhpBenefitKind.Medical),
        new("Outpatient Surgery", ["Outpatient Facility Fee (e.g., Ambulatory Surgery Center)", "Outpatient Surgery Physician/Surgical Services"], QhpBenefitKind.Medical),
        new("Outpatient Hospital", ["Outpatient Facility Fee (e.g., Ambulatory Surgery Center)"], QhpBenefitKind.Medical),
        new("Laboratory", ["Laboratory Outpatient and Professional Services"], QhpBenefitKind.Medical),
        new("Imaging", ["X-rays and Diagnostic Imaging", "Imaging (CT/PET Scans, MRIs)"], QhpBenefitKind.Medical),
        new("Behavioral Health", ["Mental/Behavioral Health Outpatient Services", "Substance Abuse Disorder Outpatient Services"], QhpBenefitKind.Medical),
        new("Maternity", ["Delivery and All Inpatient Services for Maternity Care", "Prenatal and Postnatal Care"], QhpBenefitKind.Medical),
        new("Skilled Nursing", ["Skilled Nursing Facility"], QhpBenefitKind.Medical),
        new("Home Health", ["Home Health Care Services", "Home Health"], QhpBenefitKind.Medical),
        new("Hospice", ["Hospice Services"], QhpBenefitKind.Medical),
        new("Physical Therapy", ["Outpatient Rehabilitation Services", "Rehabilitative Occupational and Rehabilitative Physical Therapy"], QhpBenefitKind.Medical),
        new("DME", ["Durable Medical Equipment"], QhpBenefitKind.Medical),
        new("Vision", ["Routine Eye Exam for Children", "Routine Eye Exam (Adult)"], QhpBenefitKind.Medical),
        // Medical claims for drugs (J-codes, NDC on a professional claim)
        // resolve to "Pharmacy"; the generic tier is the closest single rate.
        new("Pharmacy", ["Generic Drugs"], QhpBenefitKind.Pharmacy),
        // Display-only tiers for the member benefits view. Their labels match
        // the pharmacy-tier labels BenefitCategoryMap already understands.
        new("Generic", ["Generic Drugs"], QhpBenefitKind.PharmacyTier, "Generic"),
        new("Preferred Brand", ["Preferred Brand Drugs"], QhpBenefitKind.PharmacyTier, "Preferred Brand"),
        new("Non-Preferred Brand", ["Non-Preferred Brand Drugs"], QhpBenefitKind.PharmacyTier, "Non-Preferred Brand"),
        new("Specialty Drug", ["Specialty Drugs"], QhpBenefitKind.PharmacyTier, "Specialty Drug"),
    ];

    /// <summary>Every QHP benefit name some target uses, normalized.</summary>
    public static readonly IReadOnlySet<string> KnownSources =
        Targets.SelectMany(t => t.QhpSources).Select(Normalize).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Case-, whitespace- and dash-insensitive key, so "Specialist  Visit"
    /// and an en dash in "Basic Dental Care – Child" compare equal across
    /// plan years and Excel round-trips.
    /// </summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var s = name.Trim().ToLowerInvariant().Replace('–', '-').Replace('—', '-');
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
