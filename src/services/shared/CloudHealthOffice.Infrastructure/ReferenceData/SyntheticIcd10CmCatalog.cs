namespace CloudHealthOffice.Infrastructure.ReferenceData;

/// <summary>
/// ICD-10-CM display metadata used by the synthetic MCC/demo claim corpus.
/// </summary>
public static class SyntheticIcd10CmCatalog
{
    public static IReadOnlyList<SyntheticIcd10CmDiagnosis> Diagnoses { get; } =
    [
        Diagnosis("J06.9", "Acute upper respiratory infection, unspecified"),
        Diagnosis("J20.9", "Acute bronchitis, unspecified"),
        Diagnosis("J18.9", "Pneumonia, unspecified organism"),
        Diagnosis("M54.5", "Low back pain"),
        Diagnosis("M79.3", "Panniculitis, unspecified"),
        Diagnosis("R10.9", "Unspecified abdominal pain"),
        Diagnosis("R51.9", "Headache, unspecified"),
        Diagnosis("I10", "Essential (primary) hypertension"),
        Diagnosis("E11.9", "Type 2 diabetes mellitus without complications"),
        Diagnosis("E11.65", "Type 2 diabetes mellitus with hyperglycemia"),
        Diagnosis("E78.5", "Dyslipidemia, unspecified"),
        Diagnosis("F41.1", "Generalized anxiety disorder"),
        Diagnosis("F32.1", "Major depressive disorder, single episode, moderate"),
        Diagnosis("K21.0", "Gastro-esophageal reflux disease with esophagitis"),
        Diagnosis("N39.0", "Urinary tract infection, site not specified"),
        Diagnosis("J45.20", "Mild intermittent asthma, uncomplicated"),
        Diagnosis("L30.9", "Dermatitis, unspecified"),
        Diagnosis("H66.90", "Otitis media, unspecified, unspecified ear"),
        Diagnosis("B34.9", "Viral infection, unspecified"),
        Diagnosis("Z00.00", "Encounter for general adult medical examination without abnormal findings"),
        Diagnosis("K80.20", "Calculus of gallbladder without cholecystitis without obstruction"),
        Diagnosis("K40.90", "Unilateral inguinal hernia, without obstruction or gangrene, not specified as recurrent"),
        Diagnosis("M17.11", "Primary osteoarthritis, right knee"),
        Diagnosis("M17.12", "Primary osteoarthritis, left knee"),
        Diagnosis("M16.11", "Primary osteoarthritis, right hip"),
        Diagnosis("G56.00", "Carpal tunnel syndrome, unspecified upper limb"),
        Diagnosis("H25.11", "Age-related nuclear cataract, right eye"),
        Diagnosis("K35.80", "Unspecified acute appendicitis"),
        Diagnosis("M75.110", "Incomplete rotator cuff tear of right shoulder"),
        Diagnosis("N20.0", "Calculus of kidney"),
        Diagnosis("I21.3", "ST elevation (STEMI) myocardial infarction of unspecified site"),
        Diagnosis("I63.9", "Cerebral infarction, unspecified"),
        Diagnosis("S72.001A", "Fracture of unspecified part of neck of right femur, initial encounter"),
        Diagnosis("S52.501A", "Unspecified fracture of the lower end of right radius, initial encounter"),
        Diagnosis("K92.2", "Gastrointestinal hemorrhage, unspecified"),
        Diagnosis("R55", "Syncope and collapse"),
        Diagnosis("R07.9", "Chest pain, unspecified"),
        Diagnosis("S06.0X0A", "Concussion without loss of consciousness, initial encounter"),
        Diagnosis("T78.2XXA", "Anaphylactic shock, unspecified, initial encounter"),
        Diagnosis("J96.00", "Acute respiratory failure, unspecified whether with hypoxia or hypercapnia"),
        Diagnosis("F33.1", "Major depressive disorder, recurrent, moderate"),
        Diagnosis("F41.0", "Panic disorder without agoraphobia"),
        Diagnosis("F43.10", "Post-traumatic stress disorder, unspecified"),
        Diagnosis("F10.20", "Alcohol dependence, uncomplicated"),
        Diagnosis("F11.20", "Opioid dependence, uncomplicated"),
        Diagnosis("F31.9", "Bipolar disorder, unspecified"),
        Diagnosis("F84.0", "Autistic disorder"),
        Diagnosis("F90.9", "Attention-deficit hyperactivity disorder, unspecified type"),
        Diagnosis("K02.9", "Dental caries, unspecified"),
        Diagnosis("K04.0", "Pulpitis"),
        Diagnosis("K05.10", "Chronic gingivitis, plaque induced"),
        Diagnosis("K05.31", "Chronic periodontitis, localized, moderate"),
        Diagnosis("K08.1", "Complete loss of teeth"),
        Diagnosis("K08.401", "Partial loss of teeth, unspecified cause, class I"),
        Diagnosis("K12.1", "Other forms of stomatitis"),
        Diagnosis("M26.69", "Other specified disorders of temporomandibular joint"),
        Diagnosis("K03.0", "Excessive attrition of teeth"),
        Diagnosis("S02.5XXA", "Fracture of tooth (traumatic), initial encounter"),
        Diagnosis("Z38.00", "Single liveborn infant, delivered vaginally"),
        Diagnosis("Z38.01", "Single liveborn infant, delivered by cesarean"),
        Diagnosis("P59.9", "Neonatal jaundice, unspecified"),
        Diagnosis("P22.1", "Transient tachypnea of newborn"),
        Diagnosis("P07.39", "Other preterm newborn"),
        Diagnosis("P92.5", "Neonatal difficulty in feeding at breast")
    ];

    private static SyntheticIcd10CmDiagnosis Diagnosis(string code, string display) => new(code, display);
}

public sealed record SyntheticIcd10CmDiagnosis(string Code, string Display);
