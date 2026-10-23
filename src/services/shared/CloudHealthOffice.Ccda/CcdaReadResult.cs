using System.Xml.Linq;

namespace CloudHealthOffice.Ccda;

/// <summary>
/// Either a document CHO read, or the reason it would not.
///
/// A refusal carries prose rather than a code because its audience is a UM
/// reviewer looking at a submission that did not open, and "the document is a
/// CDA but not a C-CDA" tells them what to ask the sender for in a way an
/// enumeration member would not.
/// </summary>
public sealed record CcdaReadResult
{
    private CcdaReadResult(CcdaDocument? document, string? refusal)
    {
        Document = document;
        Refusal = refusal;
    }

    public CcdaDocument? Document { get; }

    public string? Refusal { get; }

    public bool Succeeded => Document is not null;

    public static CcdaReadResult Read(CcdaDocument document) => new(document, null);

    public static CcdaReadResult Refused(string reason) => new(null, reason);
}

/// <summary>
/// One C-CDA, as CHO reads it: the document's own identity and what it
/// contains. Deliberately not a FHIR resource — this is the document, and
/// turning it into resources is a later, separate decision.
/// </summary>
/// <param name="DocumentId">
/// <c>ClinicalDocument/id</c>, root and extension joined. Null where the sender
/// omitted it, which is out of conformance but does happen; nothing here
/// invents one, because a generated id would look like the sender's.
/// </param>
/// <param name="EffectiveTime">
/// The document's own <c>effectiveTime/@value</c>, verbatim in HL7's form. Left
/// as the sender wrote it rather than parsed to a timestamp: C-CDA times carry
/// varying precision and offsets, and reading "20240115" as midnight in some
/// zone would state a precision the document does not have.
/// </param>
public sealed record CcdaDocument(
    string? DocumentId,
    string? Title,
    string? EffectiveTime,
    IReadOnlyList<CcdaSection> Sections)
{
    /// <summary>Sections CHO has a mapping for.</summary>
    public IEnumerable<CcdaSection> Supported
        => Sections.Where(s => s.Kind != CcdaSectionKind.Unsupported);

    /// <summary>
    /// Sections CHO does not read. Surfaced, not dropped: a reviewer deciding
    /// an authorization needs to know a document had a section CHO skipped,
    /// and its narrative is still readable.
    /// </summary>
    public IEnumerable<CcdaSection> Unsupported
        => Sections.Where(s => s.Kind == CcdaSectionKind.Unsupported);
}

/// <param name="Title">
/// The sender's own title, kept for display alongside CHO's name for the
/// section — it is often more specific ("Active Problems as of discharge").
/// </param>
/// <param name="EntriesXml">
/// The section's <c>entry</c> elements, kept as XML for the section mappers to
/// read. The reader deliberately stops here: recognising a document and
/// interpreting its clinical content are separate decisions, and a section CHO
/// has no mapper for still arrives with its entries intact rather than being
/// parsed into nothing.
/// </param>
public sealed record CcdaSection(
    CcdaSectionKind Kind,
    IReadOnlyList<string> TemplateIds,
    string? Title,
    string? NarrativeXml,
    IReadOnlyList<XElement> EntriesXml)
{
    /// <summary>
    /// How many coded entries the section holds. A section whose narrative
    /// looks full but whose entries are absent — common in real exports, and
    /// the case where a reader that trusted the prose would invent data — is
    /// visible as such.
    /// </summary>
    public int EntryCount => EntriesXml.Count;
}
