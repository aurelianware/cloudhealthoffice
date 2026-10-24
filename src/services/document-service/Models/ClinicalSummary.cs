namespace DocumentService.Models;

/// <summary>
/// A stored clinical document, read into the shape a UM reviewer needs.
///
/// Derived on every request from the stored bytes rather than persisted. That
/// is a deliberate limit of this first version and not an oversight: CHO's
/// clinical resource store has exactly one writer, and nothing parsed here is
/// written to it, so no identity has to be derived and nothing reaches a
/// member-facing API. See docs/architecture/ccda.md.
/// </summary>
public sealed class ClinicalSummary
{
    /// <summary>The document's own id, as the sender stated it. Null where they omitted it.</summary>
    public string? DocumentId { get; init; }

    public string? Title { get; init; }

    /// <summary>
    /// The document's effective time, verbatim in HL7's form. Not parsed to a
    /// timestamp, because C-CDA times carry varying precision and rendering a
    /// year as a midnight would state a precision the document does not have.
    /// </summary>
    public string? EffectiveTime { get; init; }

    public IReadOnlyList<ClinicalSummarySection> Sections { get; init; } = [];

    /// <summary>
    /// Whether the document is filed against a member, which is what CHO
    /// attributes the clinical resources to.
    ///
    /// False means every section's <c>Resources</c> is empty for that reason
    /// and not because the document was empty — a distinction a reviewer
    /// looking at a blank panel cannot otherwise make. The narratives are
    /// still there to read.
    /// </summary>
    public bool MemberLinked { get; init; }

    /// <summary>
    /// Why CHO could not read the document, where it could not. Prose, because
    /// its audience is a reviewer looking at a submission that did not open and
    /// deciding what to ask the sender for.
    /// </summary>
    public string? Refusal { get; init; }
}

/// <summary>
/// One section. A section CHO has no mapper for still appears here, named and
/// counted and carrying its narrative — a reviewer deciding an authorization
/// needs to know the document held something CHO skipped, and dropping it
/// would make the document look smaller than it is.
/// </summary>
public sealed class ClinicalSummarySection
{
    /// <summary>CHO's name for the section, so two senders' wordings read alike.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>The sender's own title, which is often the more specific one.</summary>
    public string? Title { get; init; }

    /// <summary>Whether CHO mapped this section's entries to FHIR resources.</summary>
    public bool Mapped { get; init; }

    /// <summary>
    /// How many coded entries the section holds. A section whose narrative
    /// looks full and whose entry count is zero is the case where a reader
    /// trusting the prose would invent data, so the number is shown.
    /// </summary>
    public int EntryCount { get; init; }

    /// <summary>
    /// The mapped entries as lines a reviewer reads. Derived from the same
    /// resources as <see cref="Resources"/> and never from the narrative, so
    /// what is on screen is what CHO actually understood from the coded
    /// entries — a screen built from the prose would show a reviewer facts
    /// CHO never parsed.
    /// </summary>
    public IReadOnlyList<ClinicalSummaryItem> Items { get; init; } = [];

    /// <summary>
    /// The mapped resources as FHIR JSON. Empty for a section CHO does not
    /// map, and for one whose entries carried nothing codeable.
    /// </summary>
    public IReadOnlyList<string> Resources { get; init; } = [];

    /// <summary>The section's narrative, as the sender wrote it.</summary>
    public string? NarrativeXml { get; init; }
}

/// <summary>
/// One mapped entry, in the words a reviewer needs: what it is, what state it
/// is in, and the one detail that changes a utilization decision.
///
/// Deliberately thin. It is a reading of the FHIR resource beside it, not a
/// second source of truth: anything a screen needs beyond these three fields
/// should come from <see cref="ClinicalSummarySection.Resources"/>.
/// </summary>
public sealed class ClinicalSummaryItem
{
    /// <summary>The problem, substance or drug, as the sender named it.</summary>
    public string Display { get; init; } = string.Empty;

    /// <summary>Active, resolved, stopped. Null where the sender left it unstated.</summary>
    public string? Status { get; init; }

    /// <summary>Onset, reaction or dose — whatever the resource's kind makes material.</summary>
    public string? Detail { get; init; }
}
