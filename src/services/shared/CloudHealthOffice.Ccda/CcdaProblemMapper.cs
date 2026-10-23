using System.Xml.Linq;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.Ccda;

/// <summary>
/// The Problems section, as FHIR <see cref="Condition"/> resources.
///
/// The shape C-CDA uses here is two levels and both matter: a Problem Concern
/// Act wraps one or more Problem Observations. The CONCERN carries whether the
/// clinician is still tracking the problem; the OBSERVATION carries what the
/// problem is and when it started. Readers that flatten to the observation
/// alone lose clinical status entirely and report resolved problems as active,
/// which on a prior-authorization review is the difference between a condition
/// that justifies the request and one that does not.
/// </summary>
public static class CcdaProblemMapper
{
    private static readonly XNamespace Cda = CcdaTemplates.CdaNamespace;

    private const string ConcernActTemplate = "2.16.840.1.113883.10.20.22.4.3";
    private const string ProblemObservationTemplate = "2.16.840.1.113883.10.20.22.4.4";

    /// <summary>
    /// Reads every problem the section states.
    ///
    /// <paramref name="subject"/> is the reference CHO has already established
    /// for the member — this mapper never resolves a patient from the document,
    /// because a C-CDA's recordTarget is the SENDER's identification of a
    /// person and matching on it is a decision about identity, not a parse.
    /// </summary>
    public static IReadOnlyList<Condition> Map(CcdaSection section, ResourceReference subject)
    {
        if (section.Kind != CcdaSectionKind.Problems) return [];

        return section.EntriesXml
            .SelectMany(entry => entry.DescendantsAndSelf(Cda + "act"))
            .Where(act => HasTemplate(act, ConcernActTemplate))
            .SelectMany(concern => ConditionsOf(concern, subject))
            .ToArray();
    }

    private static IEnumerable<Condition> ConditionsOf(XElement concern, ResourceReference subject)
    {
        // Read once from the concern, applied to each observation inside it:
        // the concern is the clinician's tracking of the problem, and every
        // observation under it shares that status by construction.
        var clinicalStatus = ClinicalStatusOf(concern);

        var observations = concern
            .Elements(Cda + "entryRelationship")
            .SelectMany(r => r.Elements(Cda + "observation"))
            .Where(o => HasTemplate(o, ProblemObservationTemplate));

        foreach (var observation in observations)
        {
            var condition = ConditionOf(observation, subject, clinicalStatus);
            if (condition is not null) yield return condition;
        }
    }

    private static Condition? ConditionOf(
        XElement observation, ResourceReference subject, CodeableConcept? clinicalStatus)
    {
        // `value` is the problem. `code` is what KIND of statement it is
        // ("Finding", "Diagnosis"), which is not the diagnosis and must never
        // be used as one — a reader that fell back to `code` would record every
        // problem as the same condition.
        var value = CcdaValues.Code(observation.Element(Cda + "value"));
        if (value is null) return null;

        var effective = observation.Element(Cda + "effectiveTime");

        var condition = new Condition
        {
            Subject = subject,
            Code = value,
            ClinicalStatus = clinicalStatus,
            // Everything CHO reads here was asserted by the sending
            // organisation, never confirmed by CHO, so the verification status
            // is left unstated rather than set to `confirmed`.
            Onset = Date(CcdaValues.Low(effective)),
            Abatement = Date(CcdaValues.High(effective)),
        };

        var category = CcdaValues.Code(observation.Element(Cda + "code"));
        if (category is not null) condition.Category.Add(category);

        return condition;
    }

    /// <summary>
    /// A FHIR date or dateTime, whichever the precision supports. A
    /// year-only onset is a <c>date</c>; forcing it into a <c>dateTime</c>
    /// would require inventing a month, a day and a zone.
    /// </summary>
    private static DataType? Date(string? value)
    {
        if (value is null) return null;
        return value.Contains('T', StringComparison.Ordinal)
            ? new FhirDateTime(value)
            : new Date(value);
    }

    /// <summary>
    /// The concern act's status, as the FHIR clinical status.
    ///
    /// C-CDA says a concern act is `active`, `completed`, `aborted` or
    /// `suspended`, and those are statuses of the CONCERN rather than of the
    /// problem: a completed concern means the clinician stopped tracking it,
    /// which is `inactive`, not `resolved`. Claiming resolution would be
    /// stating a clinical outcome from an administrative fact.
    /// </summary>
    private static CodeableConcept? ClinicalStatusOf(XElement concern)
    {
        var status = CcdaValues.Trimmed(concern.Element(Cda + "statusCode")?.Attribute("code")?.Value);

        var code = status switch
        {
            "active" => "active",
            "suspended" => "inactive",
            "completed" => "inactive",
            "aborted" => "inactive",
            _ => null,
        };

        return code is null
            ? null
            : new CodeableConcept("http://terminology.hl7.org/CodeSystem/condition-clinical", code);
    }

    private static bool HasTemplate(XElement element, string templateId)
        => element.Elements(Cda + "templateId")
            .Any(t => string.Equals(t.Attribute("root")?.Value?.Trim(), templateId, StringComparison.Ordinal));
}
