using System.Text;
using CloudHealthOffice.Ccda;
using DocumentService.Services;
using Hl7.Fhir.Model;

namespace DocumentService.Tests.Services;

/// <summary>
/// The reader that turns a stored C-CDA into what a UM reviewer sees.
///
/// What is guarded here is honesty rather than coverage: a document CHO cannot
/// read says so, a section CHO does not map still appears, and a document
/// filed against nobody produces no clinical resource attributed to anyone.
/// </summary>
public class ClinicalSummaryReaderTests
{
    private static readonly ResourceReference Member = new("Patient/M-1");

    private readonly ClinicalSummaryReader _reader = new();

    private const string Problem =
        """
        <entry>
          <act classCode="ACT" moodCode="EVN">
            <templateId root="2.16.840.1.113883.10.20.22.4.3"/>
            <statusCode code="active"/>
            <entryRelationship typeCode="SUBJ">
              <observation classCode="OBS" moodCode="EVN">
                <templateId root="2.16.840.1.113883.10.20.22.4.4"/>
                <code code="55607006" codeSystem="2.16.840.1.113883.6.96" displayName="Problem"/>
                <effectiveTime><low value="20200115"/></effectiveTime>
                <value xsi:type="CD" code="38341003" codeSystem="2.16.840.1.113883.6.96"
                       displayName="Hypertension"
                       xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"/>
              </observation>
            </entryRelationship>
          </act>
        </entry>
        """;

    private const string AllergyEntry =
        """
        <entry>
          <act classCode="ACT" moodCode="EVN">
            <templateId root="2.16.840.1.113883.10.20.22.4.30"/>
            <statusCode code="active"/>
            <entryRelationship typeCode="SUBJ">
              <observation classCode="OBS" moodCode="EVN">
                <templateId root="2.16.840.1.113883.10.20.22.4.7"/>
                <value xsi:type="CD" code="416098002" codeSystem="2.16.840.1.113883.6.96"
                       displayName="Drug allergy"
                       xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"/>
                <participant typeCode="CSM">
                  <participantRole classCode="MANU">
                    <playingEntity classCode="MMAT">
                      <code code="7980" codeSystem="2.16.840.1.113883.6.88" displayName="Penicillin G"/>
                    </playingEntity>
                  </participantRole>
                </participant>
                <entryRelationship typeCode="MFST">
                  <observation classCode="OBS" moodCode="EVN">
                    <templateId root="2.16.840.1.113883.10.20.22.4.9"/>
                    <value xsi:type="CD" code="247472004" codeSystem="2.16.840.1.113883.6.96"
                           displayName="Hives"
                           xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"/>
                  </observation>
                </entryRelationship>
              </observation>
            </entryRelationship>
          </act>
        </entry>
        """;

    private static string Document(string sections) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <ClinicalDocument xmlns="{CcdaTemplates.CdaNamespace}">
          <templateId root="{CcdaTemplates.UsRealmHeader}"/>
          <id root="2.16.840.1.113883.19.5" extension="DOC-1"/>
          <title>Discharge Summary</title>
          <effectiveTime value="20260115103000-0700"/>
          <component><structuredBody>{sections}</structuredBody></component>
        </ClinicalDocument>
        """;

    private static string Section(string templateId, string title, string entries = "") =>
        $"""
        <component>
          <section>
            <templateId root="{templateId}"/>
            <title>{title}</title>
            <text><table><tr><td>seen on the ward</td></tr></table></text>
            {entries}
          </section>
        </component>
        """;

    private static string Problems(string entries = Problem) =>
        Section("2.16.840.1.113883.10.20.22.2.5", "Problems", entries);

    private static byte[] Bytes(string xml) => Encoding.UTF8.GetBytes(xml);

    // ── What the reader will look at ──────────────────────────────────────

    [Theory]
    [InlineData("application/hl7-cda+xml", true)]
    [InlineData("APPLICATION/HL7-CDA+XML", true)]
    [InlineData("application/hl7-cda+xml; charset=utf-8", true)]
    [InlineData("text/xml", true)]
    [InlineData("application/xml", true)]
    [InlineData("application/pdf", false)]
    [InlineData("image/png", false)]
    [InlineData(null, false)]
    public void OnlyXmlClinicalDocumentsAreRead(string? contentType, bool expected)
        => ClinicalSummaryReader.CanRead(contentType).Should().Be(expected);

    // ── Reading ───────────────────────────────────────────────────────────

    [Fact]
    public void TheHeaderIsCarriedThroughAsTheSenderWroteIt()
    {
        var summary = _reader.Read(Bytes(Document(Problems())), Member);

        summary.Refusal.Should().BeNull();
        // Root and extension together: an extension alone is only unique
        // inside the assigning authority's namespace.
        summary.DocumentId.Should().Be("2.16.840.1.113883.19.5^DOC-1");
        summary.Title.Should().Be("Discharge Summary");
        // Verbatim: parsing it to a timestamp would state a precision the
        // document does not carry.
        summary.EffectiveTime.Should().Be("20260115103000-0700");
    }

    [Fact]
    public void AProblemBecomesACondition()
    {
        var summary = _reader.Read(Bytes(Document(Problems())), Member);

        var section = summary.Sections.Should().ContainSingle().Subject;
        section.Kind.Should().Be(nameof(CcdaSectionKind.Problems));
        section.Mapped.Should().BeTrue();
        section.EntryCount.Should().Be(1);
        section.Resources.Should().ContainSingle()
            .Which.Should().Contain("\"resourceType\":\"Condition\"")
            .And.Contain("38341003")
            .And.Contain("Patient/M-1");
    }

    [Fact]
    public void EachMappedEntryAlsoArrivesAsALineAReviewerCanRead()
    {
        // The screen reads Items; Resources is the same content for anything
        // that wants FHIR. Both come from the coded entries, never the prose.
        var item = _reader.Read(Bytes(Document(Problems())), Member).Sections.Single().Items
            .Should().ContainSingle().Subject;

        item.Display.Should().Be("Hypertension");
        item.Status.Should().Be("active");
        item.Detail.Should().Contain("2020-01-15");
    }

    [Fact]
    public void AnAllergyReadsAsWhatItActuallyDid()
    {
        var allergies = Section("2.16.840.1.113883.10.20.22.2.6.1", "Allergies", AllergyEntry);

        var item = _reader.Read(Bytes(Document(allergies)), Member).Sections.Single().Items
            .Should().ContainSingle().Subject;

        item.Display.Should().Be("Penicillin G");
        // A rash and anaphylaxis are the same resource and not the same decision.
        item.Detail.Should().Contain("Hives");
    }

    [Fact]
    public void ASectionChoDoesNotMapIsStillNamed_CountedAndReadable()
    {
        // Dropping it would make the document look smaller than it is, and a
        // reviewer would decide an authorization without knowing what was in it.
        var immunizations = Section("2.16.840.1.113883.10.20.22.2.2.1", "Immunizations",
            "<entry><substanceAdministration/></entry>");

        var section = _reader.Read(Bytes(Document(immunizations)), Member).Sections.Single();

        section.Mapped.Should().BeFalse();
        section.Title.Should().Be("Immunizations");
        section.EntryCount.Should().Be(1);
        section.Resources.Should().BeEmpty();
        section.NarrativeXml.Should().Contain("seen on the ward");
    }

    [Fact]
    public void ADocumentFiledAgainstNoMemberYieldsNoResources_AndSaysWhy()
    {
        // A Condition attributed to nobody is a clinical fact about an unnamed
        // person. The narrative is still there to read, and MemberLinked tells
        // the reviewer the empty panel is a filing problem, not an empty document.
        var summary = _reader.Read(Bytes(Document(Problems())), subject: null);

        summary.MemberLinked.Should().BeFalse();
        var section = summary.Sections.Single();
        section.EntryCount.Should().Be(1);
        section.Resources.Should().BeEmpty();
        section.NarrativeXml.Should().Contain("seen on the ward");
    }

    [Fact]
    public void AMemberLinkedDocumentSaysSo()
        => _reader.Read(Bytes(Document(Problems())), Member).MemberLinked.Should().BeTrue();

    // ── What cannot be read ───────────────────────────────────────────────

    [Fact]
    public void SomethingThatIsNotAClinicalDocumentIsRefusedInProse_NotThrown()
    {
        var summary = _reader.Read(Bytes("<Bundle xmlns=\"http://hl7.org/fhir\"/>"), Member);

        summary.Refusal.Should().NotBeNullOrWhiteSpace();
        summary.Sections.Should().BeEmpty();
    }

    [Fact]
    public void BytesThatAreNotXmlAreRefused()
    {
        var summary = _reader.Read("%PDF-1.7 not xml at all"u8, Member);

        summary.Refusal.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AByteOrderMarkDoesNotStopTheDocumentBeingRead()
    {
        // Senders that write a BOM are common; an XML reader handed one as text
        // refuses the prolog, which would read to a reviewer as a bad document.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes(Document(Problems()))).ToArray();

        _reader.Read(bytes, Member).Refusal.Should().BeNull();
    }
}
