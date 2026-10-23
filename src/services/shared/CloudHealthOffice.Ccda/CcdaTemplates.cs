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
        };
}

/// <summary>
/// The sections CHO reads. <see cref="Unsupported"/> is a member of the same
/// enumeration on purpose: an unread section is a known outcome that is counted
/// and shown, not an absence. The same choice
/// <c>PayerToPayerImportPolicy.Classify</c> makes.
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
}
