namespace CloudHealthOffice.Ccda;

/// <summary>
/// The OIDs a C-CDA identifies itself and its sections by.
///
/// Stated as constants rather than matched on section titles, because a title
/// is free text an EHR chooses: "Problems", "Problem List", "Active Problems"
/// and "Diagnoses" are the same section, and a reader that keys on the words
/// silently drops the ones it has not seen before. The templateId is the only
/// thing a C-CDA guarantees.
/// </summary>
public static class CcdaTemplates
{
    /// <summary>US Realm Header, the templateId every C-CDA R2.1 document carries.</summary>
    public const string UsRealmHeader = "2.16.840.1.113883.10.20.22.1.1";

    /// <summary>HL7 CDA R2's own root, present on any CDA whether or not it is a C-CDA.</summary>
    public const string ClinicalDocumentRoot = "2.16.840.1.113883.1.3";

    /// <summary>
    /// C-CDA's document-level templates, other than the header itself.
    ///
    /// Every one of them requires the US Realm Header, so a document asserting
    /// any of them has claimed the header's conformance whether or not it also
    /// states the header's templateId — which is why these are accepted as an
    /// alternative to it (see <c>CcdaReader.Read</c>).
    ///
    /// Taken from HL7's published template list; the names are HL7's.
    /// Deliberately NOT including the patient-generated header
    /// (2.16.840.1.113883.10.20.29.1): it is a different realm header rather
    /// than a document type, and no document CHO has seen carries it.
    /// </summary>
    public static readonly IReadOnlySet<string> DocumentTemplates =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "2.16.840.1.113883.10.20.22.1.2",   // Continuity of Care Document (CCD)
            "2.16.840.1.113883.10.20.22.1.3",   // History and Physical
            "2.16.840.1.113883.10.20.22.1.4",   // Consultation Note
            "2.16.840.1.113883.10.20.22.1.6",   // Procedure Note
            "2.16.840.1.113883.10.20.22.1.7",   // Operative Note
            "2.16.840.1.113883.10.20.22.1.8",   // Discharge Summary
            "2.16.840.1.113883.10.20.22.1.9",   // Progress Note
            "2.16.840.1.113883.10.20.22.1.10",  // Unstructured Document
            "2.16.840.1.113883.10.20.22.1.13",  // Transfer Summary
            "2.16.840.1.113883.10.20.22.1.14",  // Referral Note
            "2.16.840.1.113883.10.20.22.1.15",  // Care Plan
        };

    /// <summary>The CDA namespace. A document outside it is not a CDA at all.</summary>
    public const string CdaNamespace = "urn:hl7-org:v3";

    /// <summary>
    /// The sections CHO reads, keyed by templateId.
    ///
    /// Both the R1.1 and R2.1 templateIds appear where they differ, because a
    /// sending system that has not migrated still emits the earlier one and the
    /// section means the same thing. The value is the name CHO shows a reviewer
    /// and records in the inventory — never the sender's title, so two
    /// documents calling the same section different things read alike.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, CcdaSectionKind> Known =
        new Dictionary<string, CcdaSectionKind>(StringComparer.Ordinal)
        {
            ["2.16.840.1.113883.10.20.22.2.5"] = CcdaSectionKind.Problems,
            ["2.16.840.1.113883.10.20.22.2.5.1"] = CcdaSectionKind.Problems,
            ["2.16.840.1.113883.10.20.22.2.6"] = CcdaSectionKind.Allergies,
            ["2.16.840.1.113883.10.20.22.2.6.1"] = CcdaSectionKind.Allergies,
            ["2.16.840.1.113883.10.20.22.2.1"] = CcdaSectionKind.Medications,
            ["2.16.840.1.113883.10.20.22.2.1.1"] = CcdaSectionKind.Medications,
            ["2.16.840.1.113883.10.20.22.2.2"] = CcdaSectionKind.Immunizations,
            ["2.16.840.1.113883.10.20.22.2.2.1"] = CcdaSectionKind.Immunizations,
            ["2.16.840.1.113883.10.20.22.2.3"] = CcdaSectionKind.Results,
            ["2.16.840.1.113883.10.20.22.2.3.1"] = CcdaSectionKind.Results,
            ["2.16.840.1.113883.10.20.22.2.4"] = CcdaSectionKind.VitalSigns,
            ["2.16.840.1.113883.10.20.22.2.4.1"] = CcdaSectionKind.VitalSigns,
            ["2.16.840.1.113883.10.20.22.2.7"] = CcdaSectionKind.Procedures,
            ["2.16.840.1.113883.10.20.22.2.7.1"] = CcdaSectionKind.Procedures,
            ["2.16.840.1.113883.10.20.22.2.10"] = CcdaSectionKind.PlanOfTreatment,
            ["2.16.840.1.113883.10.20.22.2.500"] = CcdaSectionKind.CareTeam,

            // Recognised but not yet mapped. A section CHO can name, count and
            // show the narrative of is worth more to a reviewer than one that
            // arrives as "Unsupported", and naming it is what lets the mapping
            // work be ordered by what senders actually send.
            //
            // The list is every section templateId carried by the 382
            // documents in ONC's 2015-Edition certification corpus, which is a
            // rule rather than a preference: these are sections certified EHRs
            // emit. Two are deliberately absent — a vendor-private OID
            // (2.16.840.1.113883.3.3251.1.5) and a malformed one
            // (1.3.6.1.4.19376.1.5.3.1.1.5.3.6, missing an arc) — because
            // neither names a C-CDA section.
            ["2.16.840.1.113883.10.20.22.2.17"] = CcdaSectionKind.SocialHistory,
            ["2.16.840.1.113883.10.20.22.2.58"] = CcdaSectionKind.HealthConcerns,
            ["2.16.840.1.113883.10.20.22.2.60"] = CcdaSectionKind.Goals,
            ["2.16.840.1.113883.10.20.22.2.22"] = CcdaSectionKind.Encounters,
            ["2.16.840.1.113883.10.20.22.2.22.1"] = CcdaSectionKind.Encounters,
            ["2.16.840.1.113883.10.20.22.2.23"] = CcdaSectionKind.MedicalEquipment,
            ["2.16.840.1.113883.10.20.22.2.14"] = CcdaSectionKind.FunctionalStatus,
            ["2.16.840.1.113883.10.20.22.2.56"] = CcdaSectionKind.MentalStatus,
            ["2.16.840.1.113883.10.20.22.2.15"] = CcdaSectionKind.FamilyHistory,
            ["2.16.840.1.113883.10.20.22.2.8"] = CcdaSectionKind.Assessments,
            ["2.16.840.1.113883.10.20.22.2.9"] = CcdaSectionKind.AssessmentAndPlan,
            ["2.16.840.1.113883.10.20.22.2.18"] = CcdaSectionKind.Payers,
            ["2.16.840.1.113883.10.20.22.2.21"] = CcdaSectionKind.AdvanceDirectives,
            ["2.16.840.1.113883.10.20.22.2.21.1"] = CcdaSectionKind.AdvanceDirectives,
            ["2.16.840.1.113883.10.20.22.2.11"] = CcdaSectionKind.DischargeMedications,
            ["2.16.840.1.113883.10.20.22.2.11.1"] = CcdaSectionKind.DischargeMedications,
            ["2.16.840.1.113883.10.20.22.2.44"] = CcdaSectionKind.AdmissionMedications,
            ["2.16.840.1.113883.10.20.22.2.38"] = CcdaSectionKind.MedicationsAdministered,
            ["2.16.840.1.113883.10.20.22.2.24"] = CcdaSectionKind.DischargeDiagnosis,
            ["2.16.840.1.113883.10.20.22.2.43"] = CcdaSectionKind.AdmissionDiagnosis,
            ["2.16.840.1.113883.10.20.22.2.41"] = CcdaSectionKind.DischargeInstructions,
            ["2.16.840.1.113883.10.20.22.2.45"] = CcdaSectionKind.Instructions,
            ["2.16.840.1.113883.10.20.22.2.12"] = CcdaSectionKind.ReasonForVisit,
            ["2.16.840.1.113883.10.20.22.2.13"] = CcdaSectionKind.ChiefComplaintAndReasonForVisit,
            ["2.16.840.1.113883.10.20.22.2.20"] = CcdaSectionKind.PastMedicalHistory,
            ["2.16.840.1.113883.10.20.22.2.57"] = CcdaSectionKind.Nutrition,
            ["2.16.840.1.113883.10.20.22.2.61"] = CcdaSectionKind.Outcomes,
            ["2.16.840.1.113883.10.20.21.2.3"] = CcdaSectionKind.Interventions,

            // The sections C-CDA inherited from IHE, whose OIDs sit under IHE's
            // arc rather than HL7's. Certified senders emit these unchanged.
            ["1.3.6.1.4.1.19376.1.5.3.1.3.1"] = CcdaSectionKind.ReasonForReferral,
            ["1.3.6.1.4.1.19376.1.5.3.1.3.4"] = CcdaSectionKind.HistoryOfPresentIllness,
            ["1.3.6.1.4.1.19376.1.5.3.1.3.5"] = CcdaSectionKind.HospitalCourse,
            ["1.3.6.1.4.1.19376.1.5.3.1.3.18"] = CcdaSectionKind.ReviewOfSystems,
            ["1.3.6.1.4.1.19376.1.5.3.1.1.13.2.1"] = CcdaSectionKind.ChiefComplaint,

            // CDA R2's own section OIDs, which predate C-CDA's arc and still
            // arrive from senders who never migrated.
            ["2.16.840.1.113883.10.20.2.10"] = CcdaSectionKind.PhysicalExam,
            ["2.16.840.1.113883.10.20.2.5"] = CcdaSectionKind.GeneralStatus,
        };

    /// <summary>
    /// The section kinds CHO has a mapper for — the kinds whose coded entries
    /// become FHIR resources.
    ///
    /// Stated once, here, because recognising a section and mapping it are
    /// different claims and every caller that conflates them tells a reviewer
    /// an empty resource list means an empty document. Everything else in
    /// <see cref="Known"/> is named, counted and carries its narrative.
    /// </summary>
    public static readonly IReadOnlySet<CcdaSectionKind> Mapped =
        new HashSet<CcdaSectionKind>
        {
            CcdaSectionKind.Problems,
            CcdaSectionKind.Allergies,
            CcdaSectionKind.Medications,
        };
}

/// <summary>
/// The sections CHO recognises. <see cref="Unsupported"/> is a member of the
/// same enumeration on purpose: a section CHO cannot name is a known outcome
/// that is counted and shown, not an absence. The same choice
/// <c>PayerToPayerImportPolicy.Classify</c> makes.
///
/// Recognising a section is NOT the same as mapping it — most of these have no
/// mapper, and <see cref="CcdaTemplates.Mapped"/> is the list that does. They
/// are named anyway because a reviewer reading a discharge summary is better
/// served by "Hospital Course" than by "Unsupported", and because the mapping
/// work cannot be ordered by what senders send until the sections have names.
/// </summary>
public enum CcdaSectionKind
{
    Unsupported = 0,
    Problems,
    Allergies,
    Medications,
    Immunizations,
    Results,
    VitalSigns,
    Procedures,
    PlanOfTreatment,
    CareTeam,

    // Recognised, not yet mapped.
    SocialHistory,
    HealthConcerns,
    Goals,
    Encounters,
    MedicalEquipment,
    FunctionalStatus,
    MentalStatus,
    FamilyHistory,
    Assessments,
    AssessmentAndPlan,
    Payers,
    AdvanceDirectives,
    DischargeMedications,
    AdmissionMedications,
    MedicationsAdministered,
    DischargeDiagnosis,
    AdmissionDiagnosis,
    DischargeInstructions,
    Instructions,
    ReasonForVisit,
    ChiefComplaintAndReasonForVisit,
    ChiefComplaint,
    PastMedicalHistory,
    Nutrition,
    Outcomes,
    Interventions,
    ReasonForReferral,
    HistoryOfPresentIllness,
    HospitalCourse,
    ReviewOfSystems,
    PhysicalExam,
    GeneralStatus,
}
