using System.Xml;
using System.Xml.Linq;

namespace CloudHealthOffice.Ccda;

/// <summary>
/// Reads a C-CDA R2.1 document into the shape CHO works from: the document's
/// own identity, and an inventory of its sections.
///
/// This type decides ONE question — "is this a C-CDA, and what is in it?" —
/// and answers it without interpreting clinical content. Entry-level mapping
/// builds on the inventory rather than re-walking the XML, so a section CHO
/// cannot read still appears in the inventory with its narrative intact.
///
/// Hostile input is the normal case here: the bytes arrive from an outside
/// organisation over CDex or a 275, so every refusal below is a document CHO
/// would otherwise have had to guess about.
/// </summary>
public static class CcdaReader
{
    /// <summary>
    /// Parses <paramref name="xml"/>, or explains why it is not a C-CDA.
    ///
    /// Never throws for bad input. A caller handling a provider's submission
    /// needs a reason it can show and record, and an exception for malformed
    /// XML — which is an ordinary occurrence on this path, not a defect —
    /// would make every call site write the same try/catch.
    /// </summary>
    public static CcdaReadResult Read(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return CcdaReadResult.Refused("The document is empty.");

        XDocument document;
        try
        {
            // DTD processing off and no resolver: a C-CDA is a document from an
            // outside organisation, and an external entity reference in one is
            // either an attack on this process's filesystem or a request CHO
            // would make to a host of the sender's choosing. Neither is a thing
            // a document reader should do.
            using var reader = XmlReader.Create(
                new StringReader(xml),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreWhitespace = false,
                });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            return CcdaReadResult.Refused($"The document is not well-formed XML: {ex.Message}");
        }

        var root = document.Root;
        if (root is null)
            return CcdaReadResult.Refused("The document has no root element.");

        if (root.Name.NamespaceName != CcdaTemplates.CdaNamespace
            || root.Name.LocalName != "ClinicalDocument")
        {
            return CcdaReadResult.Refused(
                "The document is not an HL7 CDA ClinicalDocument "
                + $"(found '{root.Name.LocalName}' in namespace '{root.Name.NamespaceName}').");
        }

        var templateIds = TemplateIdsOf(root);
        if (!templateIds.Contains(CcdaTemplates.UsRealmHeader))
        {
            // A CDA that is not a C-CDA is refused rather than read as far as it
            // goes. Section templateIds only mean what C-CDA says they mean, so
            // reading one out of a document that never claimed the header would
            // be inventing a guarantee the sender did not give.
            return CcdaReadResult.Refused(
                "The document is a CDA but not a C-CDA: it does not carry the US Realm Header "
                + $"template ({CcdaTemplates.UsRealmHeader}).");
        }

        return CcdaReadResult.Read(new CcdaDocument(
            DocumentId: IdOf(root.CdaElement("id")),
            Title: root.CdaElement("title")?.Value.Trim(),
            EffectiveTime: root.CdaElement("effectiveTime")?.Attribute("value")?.Value,
            Sections: SectionsOf(root)));
    }

    private static IReadOnlyList<CcdaSection> SectionsOf(XElement root)
    {
        var sections = root
            .CdaElement("component")?
            .CdaElement("structuredBody")?
            .CdaElements("component")
            .Select(c => c.CdaElement("section"))
            .Where(s => s is not null)
            .Select(s => SectionOf(s!))
            .ToArray();

        return sections ?? [];
    }

    private static CcdaSection SectionOf(XElement section)
    {
        var templateIds = TemplateIdsOf(section);

        // First match wins over "any match" so a section carrying both the R1.1
        // and R2.1 ids resolves once; the table maps both to the same kind, so
        // order cannot change the answer.
        var kind = templateIds
            .Select(id => CcdaTemplates.Known.TryGetValue(id, out var k) ? k : CcdaSectionKind.Unsupported)
            .FirstOrDefault(k => k != CcdaSectionKind.Unsupported);

        return new CcdaSection(
            Kind: kind,
            TemplateIds: templateIds,
            Title: section.CdaElement("title")?.Value.Trim(),
            // The narrative is what a reviewer reads when CHO maps nothing, so
            // it is carried whether or not the section is understood. It is
            // kept as its XML rather than flattened to text: the tables in a
            // C-CDA narrative are most of its legibility.
            NarrativeXml: section.CdaElement("text")?.ToString(),
            EntriesXml: section.CdaElements("entry").ToArray());
    }

    private static IReadOnlyList<string> TemplateIdsOf(XElement element) =>
        element.CdaElements("templateId")
            .Select(t => t.Attribute("root")?.Value)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// A CDA <c>II</c> is a root plus an optional extension, and the pair is the
    /// identifier — a root alone from a system that always sends an extension
    /// would collide across documents.
    /// </summary>
    private static string? IdOf(XElement? id)
    {
        if (id is null) return null;
        var root = id.Attribute("root")?.Value?.Trim();
        if (string.IsNullOrEmpty(root)) return null;
        var extension = id.Attribute("extension")?.Value?.Trim();
        return string.IsNullOrEmpty(extension) ? root : $"{root}^{extension}";
    }

    private static XElement? CdaElement(this XElement element, string name)
        => element.Element(XName.Get(name, CcdaTemplates.CdaNamespace));

    private static IEnumerable<XElement> CdaElements(this XElement element, string name)
        => element.Elements(XName.Get(name, CcdaTemplates.CdaNamespace));
}
