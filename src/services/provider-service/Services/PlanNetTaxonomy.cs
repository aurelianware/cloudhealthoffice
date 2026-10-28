namespace ProviderService.Services;

/// <summary>
/// Derives the Plan-Net 1.2.0 classification codes the public Provider
/// Directory needs from a provider's NUCC taxonomy code. CHO stores one
/// taxonomy per provider and none of these classifications directly, so the
/// mapping works on NUCC's grouping prefixes: the first two characters name
/// the NUCC grouping (20 = allopathic and osteopathic physicians, 28 =
/// hospitals, 33 = suppliers, ...), and the longer prefixes below pick out the
/// classifications that grouping mixes together.
///
/// Every answer is a code from the Plan-Net code system it is used with. When
/// nothing fits, the role and organization type answers are null (the element
/// is left out) and the service category and affiliation role answer "other",
/// which both code systems define.
/// </summary>
internal static class PlanNetTaxonomy
{
    /// <summary>A ProviderRoleCS code for an individual's taxonomy, or null.</summary>
    public static (string Code, string Display)? PractitionerRole(string? taxonomy)
    {
        if (string.IsNullOrEmpty(taxonomy)) return null;
        return FirstMatch(taxonomy, IndividualRoles);
    }

    /// <summary>The OrgTypeCS code for a provider organization's taxonomy.</summary>
    public static (string Code, string Display) OrganizationType(string? taxonomy)
    {
        if (!string.IsNullOrEmpty(taxonomy))
        {
            if (taxonomy.StartsWith("19", StringComparison.Ordinal)) return ("prvgrp", "Provider Group");
            if (taxonomy.StartsWith("17", StringComparison.Ordinal)
                || taxonomy.StartsWith("34", StringComparison.Ordinal)
                || taxonomy.StartsWith("37", StringComparison.Ordinal))
                return ("atyprv", "Atypical Provider");
        }
        return ("fac", "Facility");
    }

    /// <summary>The HealthcareServiceCategoryCS code for a provider organization's taxonomy.</summary>
    public static (string Code, string Display) ServiceCategory(string? taxonomy)
        => FirstMatch(taxonomy ?? string.Empty, ServiceCategories) ?? ("other", "Other");

    /// <summary>The OrganizationAffiliationRoleCS code for a provider organization's taxonomy.</summary>
    public static (string Code, string Display) AffiliationRole(string? taxonomy)
        => FirstMatch(taxonomy ?? string.Empty, AffiliationRoles) ?? ("other", "Other");

    private static (string Code, string Display)? FirstMatch(
        string taxonomy, IReadOnlyList<(string Prefix, string Code, string Display)> table)
    {
        // Tables are ordered longest prefix first, so the first hit is the most specific.
        foreach (var (prefix, code, display) in table)
        {
            if (taxonomy.StartsWith(prefix, StringComparison.Ordinal)) return (code, display);
        }
        return null;
    }

    private static readonly IReadOnlyList<(string Prefix, string Code, string Display)> IndividualRoles =
    [
        ("2255A2300X", "at", "Athletic Trainer"),
        ("103K", "ba", "Board Certified Behavior Analyst"),
        ("103T", "py", "Psychologist"),
        ("106H", "mt", "Marriage/Family Therapist"),
        ("1223", "de", "Dentist"),
        ("1835", "rx", "Pharmacy Service Providers"),
        ("152W", "oo", "Optometrist"),
        ("156F", "op", "Optician"),
        ("163W", "rn", "Registered Nurse"),
        ("164W", "lpn", "Licensed Practical Nurse"),
        ("164X", "lpn", "Licensed Practical Nurse"),
        ("175F", "na", "Naturopath"),
        ("175L", "ho", "Homeopath"),
        ("175M", "nh", "Naprapath"),
        ("1711", "om", "Doctor of Oriental Medicine"),
        ("2251", "pt", "Physical Therapist"),
        ("225X", "ot", "Occupational Therapist"),
        ("2278", "rt", "Respiratory Therapist"),
        ("231H", "au", "Audiologist"),
        ("235Z", "sp", "Speech Language Pathologist"),
        ("363A", "pa", "Physician Assistant"),
        ("363L", "crnp", "Certified Registered Nurse Practitioner"),
        ("367A", "cnw", "Certified Nurse Midwife"),
        ("101Y", "co", "Counselor"),
        ("104", "sw", "Social Worker"),
        ("111", "ch", "Chiropractor"),
        ("122", "dp", "Dental Provider"),
        ("133", "dn", "Dietary and Nutritional Service Provider"),
        ("146", "em", "Emergency Medical Service Provider"),
        ("213", "po", "Podiatrist"),
        ("364", "apn", "Advanced Practice Nursing Providers"),
        ("367", "apn", "Advanced Practice Nursing Providers"),
        ("10", "bh", "Behavioral Health & Social Service Providers"),
        ("13", "dn", "Dietary and Nutritional Service Provider"),
        ("14", "em", "Emergency Medical Service Provider"),
        ("16", "nu", "Nursing Service Related Providers"),
        ("17", "os", "Other Service Provider"),
        ("18", "rx", "Pharmacy Service Providers"),
        ("20", "ph", "Physician"),
        ("22", "drr", "Developmental, Rehabilitative and Restorative Service Providers"),
        ("23", "sh", "Speech, Language and Hearing Service Providers"),
        ("24", "te", "Technologists, Technicians and Other Technical Service Providers"),
    ];

    private static readonly IReadOnlyList<(string Prefix, string Code, string Display)> ServiceCategories =
    [
        ("261QU0200X", "urg", "Urgent Care"),
        ("261QE0002X", "emerg", "Emergency care"),
        ("261QM0801X", "behav", "Behavioral Health"),
        ("261QR0405X", "behav", "Behavioral Health"),
        ("251E", "home", "Home Health"),
        ("261Q", "outpat", "Clinic or Outpatient Facility"),
        ("3336", "pharm", "Pharmacy"),
        ("332", "dme", "DME/Medical Supplies"),
        ("335", "dme", "DME/Medical Supplies"),
        ("1223", "dent", "Dental"),
        ("152W", "vis", "Vision"),
        ("156", "vis", "Vision"),
        ("193", "group", "Medical Group"),
        ("28", "hosp", "Hospital"),
        ("27", "hosp", "Hospital"),
        ("29", "lab", "Laboratory"),
        ("32", "behav", "Behavioral Health"),
        ("34", "trans", "Transportation"),
    ];

    private static readonly IReadOnlyList<(string Prefix, string Code, string Display)> AffiliationRoles =
    [
        ("261QU0200X", "urgent", "Urgent Care Center"),
        ("261QM0801X", "behavioral", "Outpatient Behavioral Health Provider"),
        ("261QR0405X", "behavioral", "Outpatient Behavioral Health Provider"),
        ("251E", "home", "Home Health Provider"),
        ("251G", "hospice", "Hospice"),
        ("261Q", "outpatient", "Clinic or Outpatient Facility"),
        ("3336", "pharmacy", "Pharmacy"),
        ("332", "dme", "DME/Medical Supplier"),
        ("335", "dme", "DME/Medical Supplier"),
        ("193", "group", "Medical Group"),
        ("28", "hospital", "Hospital"),
        ("27", "hospital", "Hospital"),
        ("29", "laboratory", "Laboratory"),
        ("31", "nurseCustodial", "Nursing and Custodial Care Facility"),
        ("32", "residential", "Residential Treatment Facility"),
        ("34", "transport", "Transporation Provider"),
    ];
}
