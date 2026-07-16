using CHO.TerminologyService.Models;

namespace CHO.TerminologyService.Services.CodeSystemCatalog;

internal static class BuiltInIcd10CmCatalog
{
    public const string System = "http://hl7.org/fhir/sid/icd-10-cm";
    private const string Source = "BuiltInIcd10CmCatalog";
    private const string Version = "mcc-seed-2026";

    public static IReadOnlyList<CodeSystemConcept> Concepts { get; } =
    [
        Concept("J06.9", "Acute upper respiratory infection, unspecified"),
        Concept("J20.9", "Acute bronchitis, unspecified"),
        Concept("J18.9", "Pneumonia, unspecified organism"),
        Concept("M54.5", "Low back pain"),
        Concept("M79.3", "Panniculitis, unspecified"),
        Concept("R10.9", "Unspecified abdominal pain"),
        Concept("R51.9", "Headache, unspecified"),
        Concept("I10", "Essential (primary) hypertension"),
        Concept("E11.9", "Type 2 diabetes mellitus without complications"),
        Concept("E11.65", "Type 2 diabetes mellitus with hyperglycemia"),
        Concept("E78.5", "Dyslipidemia, unspecified"),
        Concept("F41.1", "Generalized anxiety disorder"),
        Concept("F32.1", "Major depressive disorder, single episode, moderate"),
        Concept("K21.0", "Gastro-esophageal reflux disease with esophagitis"),
        Concept("N39.0", "Urinary tract infection, site not specified"),
        Concept("J45.20", "Mild intermittent asthma, uncomplicated"),
        Concept("L30.9", "Dermatitis, unspecified"),
        Concept("H66.90", "Otitis media, unspecified, unspecified ear"),
        Concept("B34.9", "Viral infection, unspecified"),
        Concept("Z00.00", "Encounter for general adult medical examination without abnormal findings"),
        Concept("K80.20", "Calculus of gallbladder without cholecystitis without obstruction"),
        Concept("K40.90", "Unilateral inguinal hernia, without obstruction or gangrene, not specified as recurrent"),
        Concept("M17.11", "Primary osteoarthritis, right knee"),
        Concept("M17.12", "Primary osteoarthritis, left knee"),
        Concept("M16.11", "Primary osteoarthritis, right hip"),
        Concept("G56.00", "Carpal tunnel syndrome, unspecified upper limb"),
        Concept("H25.11", "Age-related nuclear cataract, right eye"),
        Concept("K35.80", "Unspecified acute appendicitis"),
        Concept("M75.110", "Incomplete rotator cuff tear of right shoulder"),
        Concept("N20.0", "Calculus of kidney"),
        Concept("I21.3", "ST elevation (STEMI) myocardial infarction of unspecified site"),
        Concept("I63.9", "Cerebral infarction, unspecified"),
        Concept("S72.001A", "Fracture of unspecified part of neck of right femur, initial encounter"),
        Concept("S52.501A", "Unspecified fracture of the lower end of right radius, initial encounter"),
        Concept("K92.2", "Gastrointestinal hemorrhage, unspecified"),
        Concept("R55", "Syncope and collapse"),
        Concept("R07.9", "Chest pain, unspecified"),
        Concept("S06.0X0A", "Concussion without loss of consciousness, initial encounter"),
        Concept("T78.2XXA", "Anaphylactic shock, unspecified, initial encounter"),
        Concept("J96.00", "Acute respiratory failure, unspecified whether with hypoxia or hypercapnia"),
        Concept("F33.1", "Major depressive disorder, recurrent, moderate"),
        Concept("F41.0", "Panic disorder without agoraphobia"),
        Concept("F43.10", "Post-traumatic stress disorder, unspecified"),
        Concept("F10.20", "Alcohol dependence, uncomplicated"),
        Concept("F11.20", "Opioid dependence, uncomplicated"),
        Concept("F31.9", "Bipolar disorder, unspecified"),
        Concept("F84.0", "Autistic disorder"),
        Concept("F90.9", "Attention-deficit hyperactivity disorder, unspecified type"),
        Concept("K02.9", "Dental caries, unspecified"),
        Concept("K04.0", "Pulpitis"),
        Concept("K05.10", "Chronic gingivitis, plaque induced"),
        Concept("K05.31", "Chronic periodontitis, localized, moderate"),
        Concept("K08.1", "Complete loss of teeth"),
        Concept("K08.401", "Partial loss of teeth, unspecified cause, class I"),
        Concept("K12.1", "Other forms of stomatitis"),
        Concept("M26.69", "Other specified disorders of temporomandibular joint"),
        Concept("K03.0", "Excessive attrition of teeth"),
        Concept("S02.5XXA", "Fracture of tooth (traumatic), initial encounter"),
        Concept("Z38.00", "Single liveborn infant, delivered vaginally"),
        Concept("Z38.01", "Single liveborn infant, delivered by cesarean"),
        Concept("P59.9", "Neonatal jaundice, unspecified"),
        Concept("P22.1", "Transient tachypnea of newborn"),
        Concept("P07.39", "Other preterm newborn"),
        Concept("P92.5", "Neonatal difficulty in feeding at breast")
    ];

    private static CodeSystemConcept Concept(string code, string display)
    {
        return new CodeSystemConcept
        {
            System = System,
            Code = code,
            Display = display,
            Version = Version,
            Source = Source
        };
    }
}
