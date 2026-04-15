namespace CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

/// <summary>
/// NUCC Healthcare Provider Taxonomy codes used for provider generation.
/// Distribution percentages reflect realistic specialty mix.
/// </summary>
public static class TaxonomyCodes
{
    /// <summary>
    /// Taxonomy code entry with code, description, percentage weight, and provider category.
    /// </summary>
    public record TaxonomyEntry(
        string Code,
        string Description,
        double Weight,
        string Category,
        string Credentials);

    /// <summary>Primary care taxonomy codes (40% of individual providers).</summary>
    public static readonly TaxonomyEntry[] PrimaryCare =
    {
        new("207Q00000X", "Family Medicine", 0.15, "PCP", "MD"),
        new("208D00000X", "General Practice", 0.10, "PCP", "MD"),
        new("207R00000X", "Internal Medicine", 0.10, "PCP", "MD"),
        new("2083P0901X", "Pediatrics", 0.05, "PCP", "MD"),
    };

    /// <summary>Specialist taxonomy codes (60% of individual providers).</summary>
    public static readonly TaxonomyEntry[] Specialists =
    {
        new("2084P0800X", "Psychiatry", 0.05, "Specialist", "MD"),
        new("207V00000X", "Obstetrics & Gynecology", 0.05, "Specialist", "MD"),
        new("2085R0202X", "Orthopedic Surgery", 0.05, "Specialist", "MD"),
        new("207X00000X", "Orthopaedic Surgery", 0.03, "Specialist", "MD"),
        new("2086S0120X", "Cardiovascular Disease", 0.05, "Specialist", "MD"),
        new("207Y00000X", "Dermatology", 0.03, "Specialist", "MD"),
        new("2084N0400X", "Neurology", 0.03, "Specialist", "MD"),
        new("207RG0300X", "Gastroenterology", 0.03, "Specialist", "MD"),
        new("207RE0101X", "Endocrinology", 0.02, "Specialist", "MD"),
        new("207RH0003X", "Hematology & Oncology", 0.02, "Specialist", "MD"),
        new("2086S0102X", "General Surgery", 0.03, "Specialist", "MD"),
        new("207RP1001X", "Pulmonary Disease", 0.02, "Specialist", "MD"),
        new("207RN0300X", "Nephrology", 0.02, "Specialist", "MD"),
        new("207RC0200X", "Critical Care Medicine", 0.01, "Specialist", "MD"),
        new("2084P0804X", "Child & Adolescent Psychiatry", 0.02, "Specialist", "MD"),
        new("207T00000X", "Neurological Surgery", 0.01, "Specialist", "MD"),
        new("208C00000X", "Colon & Rectal Surgery", 0.01, "Specialist", "MD"),
        new("207W00000X", "Ophthalmology", 0.03, "Specialist", "MD"),
        new("204E00000X", "Oral & Maxillofacial Surgery", 0.01, "Specialist", "DDS"),
        new("1223G0001X", "General Dentistry", 0.04, "Specialist", "DDS"),
        new("152W00000X", "Optometry", 0.02, "Specialist", "OD"),
        new("363L00000X", "Nurse Practitioner", 0.02, "Specialist", "NP"),
    };

    /// <summary>All individual provider taxonomy codes.</summary>
    public static readonly TaxonomyEntry[] AllIndividual = PrimaryCare.Concat(Specialists).ToArray();

    /// <summary>Facility taxonomy codes for organizational providers.</summary>
    public static readonly TaxonomyEntry[] Facilities =
    {
        new("282N00000X", "General Acute Care Hospital", 0.40, "Hospital", ""),
        new("282NC0060X", "Critical Access Hospital", 0.05, "Hospital", ""),
        new("261QU0200X", "Urgent Care Clinic", 0.15, "Clinic", ""),
        new("261QM1300X", "Multi-Specialty Clinic", 0.10, "Clinic", ""),
        new("261QF0400X", "Federally Qualified Health Center", 0.05, "Clinic", ""),
        new("314000000X", "Skilled Nursing Facility", 0.15, "SNF", ""),
        new("261QM0801X", "Mental Health Clinic", 0.05, "BehavioralHealth", ""),
        new("283Q00000X", "Psychiatric Hospital", 0.03, "BehavioralHealth", ""),
        new("324500000X", "Substance Abuse Rehab Facility", 0.02, "BehavioralHealth", ""),
    };

    /// <summary>Select a taxonomy code based on weighted distribution.</summary>
    public static TaxonomyEntry SelectWeighted(TaxonomyEntry[] entries, Random random)
    {
        var totalWeight = entries.Sum(e => e.Weight);
        var roll = random.NextDouble() * totalWeight;
        var cumulative = 0.0;
        foreach (var entry in entries)
        {
            cumulative += entry.Weight;
            if (roll < cumulative)
                return entry;
        }
        return entries[^1];
    }
}
