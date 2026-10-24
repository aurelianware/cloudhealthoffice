using System.Text;
using CloudHealthOffice.Ccda;
using DocumentService.Models;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace DocumentService.Services;

/// <summary>
/// Turns a stored C-CDA into the summary a UM reviewer reads
/// (docs/architecture/ccda.md).
///
/// Parse-on-read, deliberately. Nothing is persisted, so none of the three
/// problems that storing parsed clinical data would raise — deriving a stable
/// identity per resource, giving the content an origin visible on a regulated
/// API, and the clinical store's single-writer rule — has to be answered to
/// put the content in front of a reviewer. They return if this is ever
/// promoted to member-visible.
/// </summary>
public sealed class ClinicalSummaryReader
{
    private static readonly FhirJsonSerializer Serializer = new();

    /// <summary>Content types this reader can make a summary from.</summary>
    public static bool CanRead(string? contentType)
        => contentType is not null
           && Normalize(contentType) is "application/hl7-cda+xml" or "text/xml" or "application/xml";

    /// <summary>
    /// Reads <paramref name="content"/> as a C-CDA for one member.
    ///
    /// <paramref name="subject"/> is the reference CHO has already established,
    /// never one resolved from the document: a C-CDA's recordTarget is the
    /// SENDER's identification of a person, and matching on it is a decision
    /// about identity rather than a parse.
    ///
    /// Null where the document is filed against no member. The sections, their
    /// entry counts and their narratives are still returned — those are what a
    /// reviewer reads — but no resource is produced, because a Condition or an
    /// AllergyIntolerance attributed to nobody is a clinical fact about an
    /// unnamed person.
    /// </summary>
    public ClinicalSummary Read(ReadOnlySpan<byte> content, ResourceReference? subject)
    {
        var result = CcdaReader.Read(Decode(content));

        if (!result.Succeeded)
            return new ClinicalSummary { Refusal = result.Refusal };

        var document = result.Document!;

        return new ClinicalSummary
        {
            DocumentId = document.DocumentId,
            Title = document.Title,
            EffectiveTime = document.EffectiveTime,
            MemberLinked = subject is not null,
            Sections = document.Sections.Select(s => SectionOf(s, subject)).ToArray(),
        };
    }

    private static ClinicalSummarySection SectionOf(CcdaSection section, ResourceReference? subject)
    {
        var resources = ResourcesOf(section, subject);

        return new ClinicalSummarySection
        {
            Kind = section.Kind.ToString(),
            Title = section.Title,
            Mapped = Mapped(section.Kind),
            EntryCount = section.EntryCount,
            Items = resources.Select(ItemOf).ToArray(),
            Resources = resources.Select(r => Serializer.SerializeToString(r)).ToArray(),
            NarrativeXml = section.NarrativeXml,
        };
    }

    /// <summary>
    /// Whether CHO turns this section's entries into FHIR resources.
    ///
    /// Recognising a section is not the same as mapping it: CHO names and
    /// counts every section it knows, but only three have mappers today. A
    /// reviewer told "mapped" about a section that produced nothing would read
    /// the empty list as "the document held nothing", which is the one wrong
    /// conclusion this screen must not invite.
    /// </summary>
    // Asked of the library rather than restated here: a second copy of this
    // list would drift the day a mapper is added, and the screen would then
    // tell a reviewer a mapped section was unmapped.
    private static bool Mapped(CcdaSectionKind kind) => CcdaTemplates.Mapped.Contains(kind);

    private static IReadOnlyList<Resource> ResourcesOf(CcdaSection section, ResourceReference? subject)
        => subject is null ? [] : section.Kind switch
        {
            CcdaSectionKind.Problems => CcdaProblemMapper.Map(section, subject),
            CcdaSectionKind.Allergies => CcdaAllergyMapper.Map(section, subject),
            CcdaSectionKind.Medications => CcdaMedicationMapper.Map(section, subject),
            // A section CHO recognises but has no mapper for yet is NOT an
            // error: it is named and counted above, and its narrative travels,
            // so a reviewer sees that it exists and can read it.
            _ => [],
        };

    /// <summary>
    /// One resource as the line a reviewer reads. Each kind's <c>Detail</c> is
    /// the field that changes a utilization decision: when a problem began,
    /// what an allergy actually did, how much of a drug is being taken.
    /// </summary>
    private static ClinicalSummaryItem ItemOf(Resource resource) => resource switch
    {
        Condition condition => new ClinicalSummaryItem
        {
            Display = Name(condition.Code),
            Status = Name(condition.ClinicalStatus),
            Detail = When(condition.Onset) is { } onset ? "Onset " + onset : null,
        },
        AllergyIntolerance allergy => new ClinicalSummaryItem
        {
            Display = Name(allergy.Code),
            Status = Name(allergy.ClinicalStatus),
            Detail = Reactions(allergy),
        },
        MedicationRequest medication => new ClinicalSummaryItem
        {
            Display = Name(medication.Medication as CodeableConcept),
            Status = medication.Status?.ToString().ToLowerInvariant(),
            Detail = medication.DosageInstruction.FirstOrDefault()?.Text,
        },
        _ => new ClinicalSummaryItem { Display = resource.TypeName },
    };

    /// <summary>
    /// What the allergy did, which is what a reviewer weighs — a rash and
    /// anaphylaxis are the same resource and not the same decision.
    /// </summary>
    private static string? Reactions(AllergyIntolerance allergy)
    {
        var reactions = allergy.Reaction
            .SelectMany(r => r.Manifestation.Select(Name))
            .Where(m => m.Length > 0)
            .ToArray();

        return reactions.Length == 0 ? null : string.Join(", ", reactions);
    }

    /// <summary>
    /// The sender's own words first: a display name they wrote is more use to a
    /// reviewer than a code, and a code is better than nothing at all.
    /// </summary>
    private static string Name(CodeableConcept? concept)
        => concept?.Text
           ?? concept?.Coding.Select(c => c.Display).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))
           ?? concept?.Coding.Select(c => c.Code).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))
           ?? string.Empty;

    /// <summary>A date as the sender stated it, at the precision they stated it.</summary>
    private static string? When(DataType? onset) => onset switch
    {
        FhirDateTime date => date.Value,
        Period period => period.Start,
        Date date => date.Value,
        _ => null,
    };

    private static string Decode(ReadOnlySpan<byte> content)
        => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(
            content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF
                ? content[3..]
                : content);

    private static string Normalize(string contentType)
    {
        var separator = contentType.IndexOf(';');
        return (separator >= 0 ? contentType[..separator] : contentType).Trim().ToLowerInvariant();
    }
}
