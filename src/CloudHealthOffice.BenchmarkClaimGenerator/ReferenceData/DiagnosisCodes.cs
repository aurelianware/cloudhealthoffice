namespace CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

/// <summary>
/// Common ICD-10-CM diagnosis codes used in synthetic claim generation.
/// Codes are organized by clinical category for realistic claim construction.
/// </summary>
public static class DiagnosisCodes
{
    /// <summary>General/primary care diagnosis codes.</summary>
    internal static readonly (string Code, string Description)[] General =
    {
        ("J06.9", "Acute upper respiratory infection, unspecified"),
        ("J20.9", "Acute bronchitis, unspecified"),
        ("J18.9", "Pneumonia, unspecified organism"),
        ("M54.5", "Low back pain"),
        ("M79.3", "Panniculitis, unspecified"),
        ("R10.9", "Unspecified abdominal pain"),
        ("R51.9", "Headache, unspecified"),
        ("I10", "Essential (primary) hypertension"),
        ("E11.9", "Type 2 diabetes mellitus without complications"),
        ("E11.65", "Type 2 diabetes mellitus with hyperglycemia"),
        ("E78.5", "Dyslipidemia, unspecified"),
        ("F41.1", "Generalized anxiety disorder"),
        ("F32.1", "Major depressive disorder, single episode, moderate"),
        ("K21.0", "Gastro-esophageal reflux disease with esophagitis"),
        ("N39.0", "Urinary tract infection, site not specified"),
        ("J45.20", "Mild intermittent asthma, uncomplicated"),
        ("L30.9", "Dermatitis, unspecified"),
        ("H66.90", "Otitis media, unspecified, unspecified ear"),
        ("B34.9", "Viral infection, unspecified"),
        ("Z00.00", "Encounter for general adult medical examination without abnormal findings")
    };

    /// <summary>Surgical/procedural diagnosis codes.</summary>
    internal static readonly (string Code, string Description)[] Surgical =
    {
        ("K80.20", "Calculus of gallbladder without cholecystitis without obstruction"),
        ("K40.90", "Unilateral inguinal hernia, without obstruction or gangrene, not specified as recurrent"),
        ("M17.11", "Primary osteoarthritis, right knee"),
        ("M17.12", "Primary osteoarthritis, left knee"),
        ("M16.11", "Primary osteoarthritis, right hip"),
        ("G56.00", "Carpal tunnel syndrome, unspecified upper limb"),
        ("H25.11", "Age-related nuclear cataract, right eye"),
        ("K35.80", "Unspecified acute appendicitis"),
        ("M75.110", "Incomplete rotator cuff tear of right shoulder"),
        ("N20.0", "Calculus of kidney")
    };

    /// <summary>Emergency/acute diagnosis codes.</summary>
    internal static readonly (string Code, string Description)[] Emergency =
    {
        ("I21.3", "ST elevation (STEMI) myocardial infarction of unspecified site"),
        ("I63.9", "Cerebral infarction, unspecified"),
        ("S72.001A", "Fracture of unspecified part of neck of right femur, initial encounter"),
        ("S52.501A", "Unspecified fracture of the lower end of right radius, initial encounter"),
        ("K92.2", "Gastrointestinal hemorrhage, unspecified"),
        ("R55", "Syncope and collapse"),
        ("R07.9", "Chest pain, unspecified"),
        ("S06.0X0A", "Concussion without loss of consciousness, initial encounter"),
        ("T78.2XXA", "Anaphylactic shock, unspecified, initial encounter"),
        ("J96.00", "Acute respiratory failure, unspecified whether with hypoxia or hypercapnia")
    };

    /// <summary>Behavioral health diagnosis codes.</summary>
    internal static readonly (string Code, string Description)[] BehavioralHealth =
    {
        ("F32.1", "Major depressive disorder, single episode, moderate"),
        ("F33.1", "Major depressive disorder, recurrent, moderate"),
        ("F41.1", "Generalized anxiety disorder"),
        ("F41.0", "Panic disorder without agoraphobia"),
        ("F43.10", "Post-traumatic stress disorder, unspecified"),
        ("F10.20", "Alcohol dependence, uncomplicated"),
        ("F11.20", "Opioid dependence, uncomplicated"),
        ("F31.9", "Bipolar disorder, unspecified"),
        ("F84.0", "Autistic disorder"),
        ("F90.9", "Attention-deficit hyperactivity disorder, unspecified type")
    };

    /// <summary>Dental diagnosis codes (ICD-10 used alongside CDT).</summary>
    internal static readonly (string Code, string Description)[] Dental =
    {
        ("K02.9", "Dental caries, unspecified"),
        ("K04.0", "Pulpitis"),
        ("K05.10", "Chronic gingivitis, plaque induced"),
        ("K05.31", "Chronic periodontitis, localized, moderate"),
        ("K08.1", "Complete loss of teeth"),
        ("K08.401", "Partial loss of teeth, unspecified cause, class I"),
        ("K12.1", "Other forms of stomatitis"),
        ("M26.69", "Other specified disorders of temporomandibular joint"),
        ("K03.0", "Excessive attrition of teeth"),
        ("S02.5XXA", "Fracture of tooth (traumatic), initial encounter")
    };

    /// <summary>Newborn diagnosis codes.</summary>
    internal static readonly (string Code, string Description)[] Newborn =
    {
        ("Z38.00", "Single liveborn infant, delivered vaginally"),
        ("Z38.01", "Single liveborn infant, delivered by cesarean"),
        ("P59.9", "Neonatal jaundice, unspecified"),
        ("P22.1", "Transient tachypnea of newborn"),
        ("P07.39", "Other preterm newborn"),
        ("P92.5", "Neonatal difficulty in feeding at breast")
    };

    /// <summary>Returns the display description for a known synthetic ICD-10-CM diagnosis code.</summary>
    public static string? FindDescription(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return AllCodes()
            .FirstOrDefault(dx => string.Equals(dx.Code, code, StringComparison.OrdinalIgnoreCase))
            .Description;
    }

    private static IEnumerable<(string Code, string Description)> AllCodes()
    {
        foreach (var diagnosisCode in General) yield return diagnosisCode;
        foreach (var diagnosisCode in Surgical) yield return diagnosisCode;
        foreach (var diagnosisCode in Emergency) yield return diagnosisCode;
        foreach (var diagnosisCode in BehavioralHealth) yield return diagnosisCode;
        foreach (var diagnosisCode in Dental) yield return diagnosisCode;
        foreach (var diagnosisCode in Newborn) yield return diagnosisCode;
    }
}
