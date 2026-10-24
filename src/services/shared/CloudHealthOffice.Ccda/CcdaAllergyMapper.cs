using System.Xml.Linq;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.Ccda;

/// <summary>
/// The Allergies section, as FHIR <see cref="AllergyIntolerance"/> resources.
///
/// Two levels again, as with Problems: an Allergy Concern Act wraps one or more
/// Allergy Intolerance Observations. The concern carries whether the allergy is
/// still being tracked; the observation carries the substance, the kind of
/// reaction, and — nested one level deeper — the reactions themselves and their
/// severity.
///
/// The substance is the field that matters most and is the easiest to get
/// wrong. It is NOT the observation's `value` (which says what KIND of
/// intolerance this is) but the participant's playing entity code, and a mapper
/// that confused them would record every allergy as "propensity to adverse
/// reaction" with no substance at all.
/// </summary>
public static class CcdaAllergyMapper
{
    private static readonly XNamespace Cda = CcdaTemplates.CdaNamespace;

    private const string ConcernActTemplate = "2.16.840.1.113883.10.20.22.4.30";
    private const string ObservationTemplate = "2.16.840.1.113883.10.20.22.4.7";
    private const string ReactionObservationTemplate = "2.16.840.1.113883.10.20.22.4.9";
    private const string SeverityObservationTemplate = "2.16.840.1.113883.10.20.22.4.8";

    public static IReadOnlyList<AllergyIntolerance> Map(CcdaSection section, ResourceReference subject)
    {
        if (section.Kind != CcdaSectionKind.Allergies) return [];

        return section.EntriesXml
            .SelectMany(entry => entry.DescendantsAndSelf(Cda + "act"))
            .Where(act => HasTemplate(act, ConcernActTemplate))
            .SelectMany(concern => AllergiesOf(concern, subject))
            .ToArray();
    }

    private static IEnumerable<AllergyIntolerance> AllergiesOf(XElement concern, ResourceReference subject)
    {
        var clinicalStatus = ClinicalStatusOf(concern);

        var observations = concern
            .Elements(Cda + "entryRelationship")
            .SelectMany(r => r.Elements(Cda + "observation"))
            .Where(o => HasTemplate(o, ObservationTemplate));

        foreach (var observation in observations)
        {
            var allergy = AllergyOf(observation, subject, clinicalStatus);
            if (allergy is not null) yield return allergy;
        }
    }

    private static AllergyIntolerance? AllergyOf(
        XElement observation, ResourceReference subject, CodeableConcept? clinicalStatus)
    {
        var substance = SubstanceOf(observation);
        if (substance is null) return null;

        var allergy = new AllergyIntolerance
        {
            Patient = subject,
            Code = substance,
            ClinicalStatus = clinicalStatus,
            Onset = OnsetOf(observation),
        };

        // `value` on the observation says what kind of intolerance this is —
        // drug, food, environmental — which is the FHIR category, not the
        // substance.
        var category = CategoryOf(observation);
        if (category is not null) allergy.Category = [category];

        foreach (var reaction in ReactionsOf(observation)) allergy.Reaction.Add(reaction);

        return allergy;
    }

    /// <summary>
    /// The substance: the participant's playing-entity code, which is where
    /// C-CDA puts it. Falls back to nothing rather than to the observation's
    /// `value`, because that field holds the KIND of intolerance and using it
    /// would record every allergy as the same thing.
    /// </summary>
    private static CodeableConcept? SubstanceOf(XElement observation)
        => CcdaValues.Code(observation
            .Elements(Cda + "participant")
            .Select(p => p.Element(Cda + "participantRole"))
            .Where(r => r is not null)
            .Select(r => r!.Element(Cda + "playingEntity")?.Element(Cda + "code"))
            .FirstOrDefault(c => c is not null));

    private static AllergyIntolerance.AllergyIntoleranceCategory? CategoryOf(XElement observation)
    {
        var value = observation.Element(Cda + "value")?.Attribute("code")?.Value?.Trim();

        // SNOMED codes from C-CDA's allergy-type value set. Only the ones that
        // map cleanly: an intolerance type CHO does not recognise leaves the
        // category unset rather than picking the nearest-looking one, because a
        // food allergy recorded as a drug allergy is worse than one recorded as
        // neither.
        return value switch
        {
            // Drug allergy, propensity to adverse reactions to drug, drug intolerance.
            "416098002" or "419511003" or "59037007"
                => AllergyIntolerance.AllergyIntoleranceCategory.Medication,
            // Food allergy, propensity to adverse reactions to food, food intolerance.
            "414285001" or "418471000" or "235719002"
                => AllergyIntolerance.AllergyIntoleranceCategory.Food,
            // Environmental allergy, dander (animal) allergy.
            "426232007" or "232347008"
                => AllergyIntolerance.AllergyIntoleranceCategory.Environment,
            _ => null,
        };
    }

    private static DataType? OnsetOf(XElement observation)
    {
        var value = CcdaValues.Low(observation.Element(Cda + "effectiveTime"));
        if (value is null) return null;
        return value.Contains('T', StringComparison.Ordinal)
            ? new FhirDateTime(value)
            : new Date(value);
    }

    /// <summary>
    /// The reactions, each with its severity.
    ///
    /// Severity in C-CDA hangs off the REACTION observation, not off the
    /// allergy — the same allergy can produce a rash once and anaphylaxis the
    /// next time. Hoisting it to the allergy would average away the dangerous
    /// one.
    /// </summary>
    private static IEnumerable<AllergyIntolerance.ReactionComponent> ReactionsOf(XElement observation)
    {
        var reactions = observation
            .Elements(Cda + "entryRelationship")
            .SelectMany(r => r.Elements(Cda + "observation"))
            .Where(o => HasTemplate(o, ReactionObservationTemplate));

        foreach (var reaction in reactions)
        {
            var manifestation = CcdaValues.Code(reaction.Element(Cda + "value"));
            if (manifestation is null) continue;

            var component = new AllergyIntolerance.ReactionComponent();
            component.Manifestation.Add(manifestation);

            var severity = SeverityOf(reaction);
            if (severity is not null) component.Severity = severity;

            yield return component;
        }
    }

    private static AllergyIntolerance.AllergyIntoleranceSeverity? SeverityOf(XElement reaction)
    {
        var severity = reaction
            .Elements(Cda + "entryRelationship")
            .SelectMany(r => r.Elements(Cda + "observation"))
            .FirstOrDefault(o => HasTemplate(o, SeverityObservationTemplate));

        return severity?.Element(Cda + "value")?.Attribute("code")?.Value?.Trim() switch
        {
            "255604002" => AllergyIntolerance.AllergyIntoleranceSeverity.Mild,
            "6736007" => AllergyIntolerance.AllergyIntoleranceSeverity.Moderate,
            "24484000" => AllergyIntolerance.AllergyIntoleranceSeverity.Severe,
            _ => null,
        };
    }

    private static CodeableConcept? ClinicalStatusOf(XElement concern)
    {
        var status = CcdaValues.Trimmed(concern.Element(Cda + "statusCode")?.Attribute("code")?.Value);

        var code = status switch
        {
            "active" => "active",
            "suspended" or "completed" or "aborted" => "inactive",
            _ => null,
        };

        return code is null
            ? null
            : new CodeableConcept("http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical", code);
    }

    private static bool HasTemplate(XElement element, string templateId)
        => element.Elements(Cda + "templateId")
            .Any(t => string.Equals(t.Attribute("root")?.Value?.Trim(), templateId, StringComparison.Ordinal));
}
