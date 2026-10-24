using System.Xml.Linq;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.Ccda;

/// <summary>
/// The Medications section, as FHIR <see cref="MedicationRequest"/> resources.
///
/// Flatter than Problems and Allergies — a Medication Activity stands on its
/// own rather than inside a concern — but with its own trap: the element that
/// says WHAT the medication is and the element that says WHEN it is taken are
/// both called <c>effectiveTime</c>, distinguished only by <c>xsi:type</c> and
/// an <c>operator</c> attribute. A mapper that takes the first one it finds
/// will report a dosing frequency as a prescription date, or the reverse.
/// </summary>
public static class CcdaMedicationMapper
{
    private static readonly XNamespace Cda = CcdaTemplates.CdaNamespace;

    private const string MedicationActivityTemplate = "2.16.840.1.113883.10.20.22.4.16";

    public static IReadOnlyList<MedicationRequest> Map(CcdaSection section, ResourceReference subject)
    {
        if (section.Kind != CcdaSectionKind.Medications) return [];

        return section.EntriesXml
            .SelectMany(entry => entry.DescendantsAndSelf(Cda + "substanceAdministration"))
            .Where(s => HasTemplate(s, MedicationActivityTemplate))
            .Select(activity => RequestOf(activity, subject))
            .Where(r => r is not null)
            .Select(r => r!)
            .ToArray();
    }

    private static MedicationRequest? RequestOf(XElement activity, ResourceReference subject)
    {
        var medication = MedicationOf(activity);
        if (medication is null) return null;

        var request = new MedicationRequest
        {
            Subject = subject,
            // R4's `medication[x]` is a choice of CodeableConcept or
            // Reference, not R5's CodeableReference. A coded drug needs no
            // contained Medication resource, so the concept goes in directly.
            Medication = medication,
            Status = StatusOf(activity),
            // C-CDA's Medications section records what the member is taking,
            // which FHIR calls an order rather than a plan or a proposal.
            Intent = MedicationRequest.MedicationRequestIntent.Order,
        };

        var dosage = DosageOf(activity);
        if (dosage is not null) request.DosageInstruction.Add(dosage);

        return request;
    }

    /// <summary>
    /// The drug, from the consumable's manufactured material. CHO does not fall
    /// back to the activity's own <c>text</c>: a `MedicationRequest` naming a
    /// medication only in prose cannot be checked against anything, and a
    /// reviewer reading one would assume it had been coded.
    /// </summary>
    private static CodeableConcept? MedicationOf(XElement activity)
        => CcdaValues.Code(activity
            .Element(Cda + "consumable")?
            .Element(Cda + "manufacturedProduct")?
            .Element(Cda + "manufacturedMaterial")?
            .Element(Cda + "code"));

    private static MedicationRequest.MedicationrequestStatus StatusOf(XElement activity)
        => activity.Element(Cda + "statusCode")?.Attribute("code")?.Value?.Trim() switch
        {
            "active" => MedicationRequest.MedicationrequestStatus.Active,
            "completed" => MedicationRequest.MedicationrequestStatus.Completed,
            "aborted" => MedicationRequest.MedicationrequestStatus.Stopped,
            "suspended" => MedicationRequest.MedicationrequestStatus.OnHold,
            // "we were not told" rather than a guess at active, which would put
            // a discontinued drug on a reviewer's current-medications list.
            _ => MedicationRequest.MedicationrequestStatus.Unknown,
        };

    private static Dosage? DosageOf(XElement activity)
    {
        var dosage = new Dosage();
        var any = false;

        var dose = CcdaValues.Quantity(activity.Element(Cda + "doseQuantity"));
        if (dose is not null)
        {
            dosage.DoseAndRate.Add(new Dosage.DoseAndRateComponent { Dose = dose });
            any = true;
        }

        var route = CcdaValues.Code(activity.Element(Cda + "routeCode"));
        if (route is not null)
        {
            dosage.Route = route;
            any = true;
        }

        var timing = TimingOf(activity);
        if (timing is not null)
        {
            dosage.Timing = timing;
            any = true;
        }

        return any ? dosage : null;
    }

    /// <summary>
    /// The dosing schedule, which is the <c>effectiveTime</c> carrying
    /// <c>operator="A"</c> — the one that means "and also, on this cycle".
    ///
    /// Selected by that attribute rather than by position. Senders order the
    /// two effectiveTimes differently, and reading the wrong one turns "every
    /// 8 hours" into a prescription start date.
    /// </summary>
    private static Timing? TimingOf(XElement activity)
    {
        var periodic = activity
            .Elements(Cda + "effectiveTime")
            .FirstOrDefault(e => e.Attribute("operator")?.Value == "A"
                                 || CcdaValues.XsiType(e)?.Contains("PIVL", StringComparison.Ordinal) == true);

        var period = periodic?.Element(Cda + "period");
        if (period is null) return null;

        var value = CcdaValues.Trimmed(period.Attribute("value")?.Value);
        var unit = CcdaValues.Trimmed(period.Attribute("unit")?.Value);
        if (value is null || unit is null
            || !decimal.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var period_))
        {
            return null;
        }

        var units = UnitOf(unit);
        if (units is null) return null;

        return new Timing { Repeat = new Timing.RepeatComponent { Period = period_, PeriodUnit = units } };
    }

    /// <summary>
    /// A UCUM time unit as a FHIR one. An unrecognised unit yields no timing at
    /// all rather than a default: a schedule in the wrong unit is a dosing
    /// error on a reviewer's screen.
    /// </summary>
    private static Timing.UnitsOfTime? UnitOf(string unit) => unit switch
    {
        "s" => Timing.UnitsOfTime.S,
        "min" => Timing.UnitsOfTime.Min,
        "h" => Timing.UnitsOfTime.H,
        "d" => Timing.UnitsOfTime.D,
        "wk" => Timing.UnitsOfTime.Wk,
        "mo" => Timing.UnitsOfTime.Mo,
        "a" => Timing.UnitsOfTime.A,
        _ => null,
    };

    private static bool HasTemplate(XElement element, string templateId)
        => element.Elements(Cda + "templateId")
            .Any(t => string.Equals(t.Attribute("root")?.Value?.Trim(), templateId, StringComparison.Ordinal));
}
