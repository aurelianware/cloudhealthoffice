using CloudHealthOffice.Ccda;
using FluentAssertions;

namespace CloudHealthOffice.Ccda.Tests;

/// <summary>
/// What CHO will and will not accept as a C-CDA, and what it reports about one
/// it accepts.
///
/// Every refusal below is a document that arrived from an outside organisation
/// over CDex or a 275. The failure each one prevents is the same: CHO showing a
/// UM reviewer clinical content it inferred from a document that never promised
/// it.
/// </summary>
public class CcdaReaderTests
{
    private const string Ns = "urn:hl7-org:v3";

    private const string DefaultHeader =
        "<id root=\"2.16.840.1.113883.19.5\" extension=\"DOC-1\"/>"
        + "<title>Discharge Summary</title>"
        + "<effectiveTime value=\"20260115103000-0700\"/>";

    private static string Document(string body = "", string? header = null) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <ClinicalDocument xmlns="{Ns}">
          <templateId root="{CcdaTemplates.UsRealmHeader}"/>
          {header ?? DefaultHeader}
          <component>
            <structuredBody>
              {body}
            </structuredBody>
          </component>
        </ClinicalDocument>
        """;

    private static string Section(string templateId, string title = "A section", int entries = 0)
    {
        var entryXml = string.Concat(Enumerable.Repeat("<entry><observation/></entry>", entries));
        return $"""
        <component>
          <section>
            <templateId root="{templateId}"/>
            <title>{title}</title>
            <text><table><tr><td>narrative</td></tr></table></text>
            {entryXml}
          </section>
        </component>
        """;
    }

    // ── What is not a C-CDA ───────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyContent_IsRefused(string xml)
        => CcdaReader.Read(xml).Refusal.Should().Contain("empty");

    [Fact]
    public void ContentThatIsNotXml_IsRefusedWithoutThrowing()
    {
        // Malformed input is ordinary on this path, not a defect, so it must be
        // a refusal a caller can show and record rather than an exception every
        // call site has to catch.
        var result = CcdaReader.Read("%PDF-1.4 this is a pdf someone mislabelled");

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Contain("well-formed");
    }

    [Fact]
    public void AnXmlDocumentThatIsNotACda_IsRefused()
        => CcdaReader.Read("<Bundle xmlns=\"http://hl7.org/fhir\"/>").Refusal
            .Should().Contain("not an HL7 CDA ClinicalDocument");

    [Fact]
    public void ACdaWithoutTheUsRealmHeader_IsRefused()
    {
        // The section templateIds only mean what C-CDA says they mean. Reading
        // them out of a plain CDA would invent a guarantee the sender never
        // gave, so the document is refused rather than read as far as it goes.
        var plainCda = $"""
            <ClinicalDocument xmlns="{Ns}">
              <id root="1.2.3"/>
              <component><structuredBody>{Section("2.16.840.1.113883.10.20.22.2.5")}</structuredBody></component>
            </ClinicalDocument>
            """;

        CcdaReader.Read(plainCda).Refusal.Should().Contain("not a C-CDA");
    }

    [Fact]
    public void AnExternalEntityIsNotResolved()
    {
        // XXE on a document path that accepts files from outside organisations.
        // A reader that expanded this would read the host's filesystem on the
        // sender's instruction.
        var xxe = $"""
            <?xml version="1.0"?>
            <!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <ClinicalDocument xmlns="{Ns}">
              <templateId root="{CcdaTemplates.UsRealmHeader}"/>
              <title>&xxe;</title>
            </ClinicalDocument>
            """;

        var result = CcdaReader.Read(xxe);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().NotContain("root:");
    }

    // ── What it reports about a document it accepts ───────────────────────────

    [Fact]
    public void TheDocumentsOwnIdentityIsRead()
    {
        var document = CcdaReader.Read(Document()).Document!;

        // Root and extension together: a root alone, from a system that always
        // sends an extension, would collide across that system's documents.
        document.DocumentId.Should().Be("2.16.840.1.113883.19.5^DOC-1");
        document.Title.Should().Be("Discharge Summary");
        // Verbatim, not parsed: C-CDA times carry varying precision, and
        // reading one as a timestamp states a precision the document lacks.
        document.EffectiveTime.Should().Be("20260115103000-0700");
    }

    [Fact]
    public void AMissingDocumentIdIsNotInvented()
    {
        var headerWithoutId = "<title>Untitled</title>";

        CcdaReader.Read(Document(header: headerWithoutId)).Document!
            .DocumentId.Should().BeNull("a generated id would be indistinguishable from the sender's");
    }

    [Fact]
    public void ASectionIsIdentifiedByTemplateIdRatherThanTitle()
    {
        // "Problems", "Problem List", "Active Problems" and "Diagnoses" are one
        // section. A reader keyed on the words drops the wordings it has not
        // seen, silently.
        var document = CcdaReader.Read(Document(
            Section("2.16.840.1.113883.10.20.22.2.5", title: "Active Problems as of discharge"))).Document!;

        var section = document.Sections.Should().ContainSingle().Subject;
        section.Kind.Should().Be(CcdaSectionKind.Problems);
        section.Title.Should().Be("Active Problems as of discharge", "the sender's wording is often the more specific one");
    }

    [Fact]
    public void TheEarlierReleasesTemplateIdIsReadAsTheSameSection()
        => CcdaReader.Read(Document(Section("2.16.840.1.113883.10.20.22.2.6"))).Document!
            .Sections.Single().Kind.Should().Be(CcdaSectionKind.Allergies,
                "a sender that has not migrated to R2.1 means the same section");

    [Fact]
    public void AnUnreadSectionIsCountedAndKept_NotDropped()
    {
        // A reviewer deciding an authorization needs to know a document carried
        // a section CHO skipped. Dropping it makes the document look smaller
        // than it is.
        var document = CcdaReader.Read(Document(
            Section("2.16.840.1.113883.10.20.22.2.5", title: "Problems")
            + Section("2.16.840.1.113883.10.20.22.2.17", title: "Social History"))).Document!;

        document.Sections.Should().HaveCount(2);
        document.Supported.Should().ContainSingle().Which.Kind.Should().Be(CcdaSectionKind.Problems);

        var skipped = document.Unsupported.Should().ContainSingle().Subject;
        skipped.Title.Should().Be("Social History");
        skipped.NarrativeXml.Should().Contain("narrative", "the prose is what a reviewer reads when CHO maps nothing");
    }

    [Fact]
    public void EntriesAreCountedSoAProseOnlySectionIsVisibleAsOne()
    {
        // The case where a reader that trusted the narrative would invent data:
        // a section whose prose looks complete and whose coded entries are
        // absent. Common in real exports.
        var document = CcdaReader.Read(Document(
            Section("2.16.840.1.113883.10.20.22.2.1", title: "Medications", entries: 0)
            + Section("2.16.840.1.113883.10.20.22.2.5", title: "Problems", entries: 3))).Document!;

        document.Sections.Single(s => s.Kind == CcdaSectionKind.Medications).EntryCount.Should().Be(0);
        document.Sections.Single(s => s.Kind == CcdaSectionKind.Problems).EntryCount.Should().Be(3);
    }

    [Fact]
    public void ADocumentWithNoBodyIsReadRatherThanRefused()
    {
        // Empty is a legitimate answer to "what did they send?", and distinct
        // from "this was not a C-CDA". Conflating them would tell a reviewer a
        // document failed when it merely said nothing.
        var result = CcdaReader.Read(Document());

        result.Succeeded.Should().BeTrue();
        result.Document!.Sections.Should().BeEmpty();
    }
}
