namespace CloudHealthOffice.Ccda;

/// <summary>
/// Translates the OIDs a C-CDA names code systems by into the canonical URIs
/// FHIR names the same systems by.
///
/// An explicit table rather than a pattern, because the mapping is not
/// derivable: the two naming schemes were defined independently, and HL7
/// publishes the correspondence one system at a time.
///
/// An OID not in this table is NOT translated and NOT guessed. The OID travels
/// through as <c>urn:oid:…</c>, which is a real and dereferenceable form, so a
/// code in an unfamiliar system stays readable and stays honest about which
/// system it came from. Inventing a URI would produce a coding that looks
/// resolvable and is not.
/// </summary>
public static class CcdaCodeSystems
{
    private static readonly IReadOnlyDictionary<string, string> Canonical =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["2.16.840.1.113883.6.96"] = "http://snomed.info/sct",
            ["2.16.840.1.113883.6.90"] = "http://hl7.org/fhir/sid/icd-10-cm",
            ["2.16.840.1.113883.6.3"] = "http://hl7.org/fhir/sid/icd-10",
            ["2.16.840.1.113883.6.103"] = "http://hl7.org/fhir/sid/icd-9-cm",
            ["2.16.840.1.113883.6.1"] = "http://loinc.org",
            ["2.16.840.1.113883.6.88"] = "http://www.nlm.nih.gov/research/umls/rxnorm",
            ["2.16.840.1.113883.12.292"] = "http://hl7.org/fhir/sid/cvx",
            ["2.16.840.1.113883.6.12"] = "http://www.ama-assn.org/go/cpt",
            ["2.16.840.1.113883.6.4"] = "http://www.cms.gov/Medicare/Coding/ICD10",
            ["2.16.840.1.113883.6.8"] = "http://unitsofmeasure.org",
            ["2.16.840.1.113883.5.83"] = "http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation",
            ["2.16.840.1.113883.6.345"] = "http://hl7.org/fhir/sid/ndc",
            ["2.16.840.1.113883.6.69"] = "http://hl7.org/fhir/sid/ndc",
        };

    /// <summary>
    /// The FHIR system URI for <paramref name="oid"/>, or its <c>urn:oid:</c>
    /// form where CHO has no translation. Null only where no OID was given.
    /// </summary>
    public static string? SystemFor(string? oid)
    {
        if (string.IsNullOrWhiteSpace(oid)) return null;
        var trimmed = oid.Trim();
        return Canonical.TryGetValue(trimmed, out var system) ? system : $"urn:oid:{trimmed}";
    }

    /// <summary>Whether CHO recognises the system, as opposed to passing its OID through.</summary>
    public static bool IsKnown(string? oid)
        => !string.IsNullOrWhiteSpace(oid) && Canonical.ContainsKey(oid.Trim());
}
