using System.Xml.Linq;
using CloudHealthOffice.Ccda;
using FluentAssertions;

namespace CloudHealthOffice.Ccda.Tests;

/// <summary>
/// The CDA datatypes, read into FHIR. These are the cases where a careless
/// reader states something the sender did not: a date more precise than the one
/// recorded, a code in a system that was never named, a value read out of an
/// element that declared itself absent.
/// </summary>
public class CcdaValuesTests
{
    private static XElement Xml(string xml) =>
        XElement.Parse(xml.Replace("<e ", $"<e xmlns=\"{CcdaTemplates.CdaNamespace}\" "));

    // ── Dates keep the precision they were given ──────────────────────────────

    [Theory]
    [InlineData("2024", "2024")]
    [InlineData("202403", "2024-03")]
    [InlineData("20240315", "2024-03-15")]
    public void APartialDate_StaysPartial(string value, string expected)
    {
        // A reviewer uses an onset date to decide whether a condition predates
        // coverage. Widening "2024" to 2024-01-01 invents the day it turns on.
        CcdaValues.Timestamp(value).Should().Be(expected);
    }

    [Fact]
    public void AFullTimestamp_KeepsItsOffset()
        => CcdaValues.Timestamp("20260115103000-0700").Should().Be("2026-01-15T10:30:00-07:00");

    [Fact]
    public void ATimestampWithNoOffset_IsReadAsUtc()
        => CcdaValues.Timestamp("20260115103000").Should().Be("2026-01-15T10:30:00Z");

    [Theory]
    [InlineData("20")]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData(null)]
    public void SomethingThatIsNotADate_IsNull_NotGuessed(string? value)
        => CcdaValues.Timestamp(value).Should().BeNull("'20' is not the year 20");

    [Fact]
    public void AnIntervalsBoundsAreRead()
    {
        var interval = Xml("""<e ><low value="20200101"/><high value="20231231"/></e>""");

        CcdaValues.Low(interval).Should().Be("2020-01-01");
        CcdaValues.High(interval).Should().Be("2023-12-31");
    }

    [Fact]
    public void AnOpenIntervalHasNoHigh()
    {
        // An ongoing condition. A high bound invented here would say it resolved.
        var interval = Xml("""<e ><low value="20200101"/></e>""");

        CcdaValues.High(interval).Should().BeNull();
    }

    // ── Codes ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AKnownOid_BecomesItsFhirSystem()
    {
        var concept = CcdaValues.Code(Xml(
            """<e code="38341003" codeSystem="2.16.840.1.113883.6.96" displayName="Hypertension"/>"""))!;

        var coding = concept.Coding.Should().ContainSingle().Subject;
        coding.System.Should().Be("http://snomed.info/sct");
        coding.Code.Should().Be("38341003");
        coding.Display.Should().Be("Hypertension");
    }

    [Fact]
    public void AnUnknownOid_TravelsAsAUrnRatherThanAGuess()
    {
        // urn:oid: is a real form. Inventing a http:// canonical would produce a
        // coding that looks resolvable and is not.
        var concept = CcdaValues.Code(Xml(
            """<e code="LOCAL-7" codeSystem="1.2.3.4.5.6.7" displayName="Local code"/>"""))!;

        concept.Coding.Single().System.Should().Be("urn:oid:1.2.3.4.5.6.7");
    }

    [Fact]
    public void TranslationsAreKeptAsCodingsOfTheSameConcept()
    {
        // Senders disagree about which coding leads. Dropping translations
        // discards the standard code about as often as the local one.
        var concept = CcdaValues.Code(Xml(
            """
            <e code="LOCAL-7" codeSystem="1.2.3.4.5.6.7" displayName="Local hypertension">
              <translation code="38341003" codeSystem="2.16.840.1.113883.6.96" displayName="Hypertension"/>
            </e>
            """))!;

        concept.Coding.Should().HaveCount(2);
        concept.Coding.Should().Contain(c => c.System == "http://snomed.info/sct" && c.Code == "38341003");
    }

    [Fact]
    public void ANullFlavorIsAnAbsence_NotACode()
    {
        // The sender is saying "this is absent, and here is why". Reading the
        // other attributes anyway turns a declared absence into data.
        var concept = CcdaValues.Code(Xml("""<e nullFlavor="UNK" code="x" codeSystem="2.16.840.1.113883.6.96"/>"""));

        concept?.Coding.Should().BeNullOrEmpty();
    }

    [Fact]
    public void OriginalTextIsKeptSoAnUnresolvableCodeStillReads()
        => CcdaValues.Code(Xml(
            """<e code="LOCAL-7" codeSystem="1.2.3.4.5.6.7"><originalText>Essential hypertension</originalText></e>"""))!
            .Text.Should().Be("Essential hypertension");

    [Fact]
    public void AnEmptyCodedElement_IsNull()
        => CcdaValues.Code(Xml("<e />")).Should().BeNull();

    // ── Quantities ────────────────────────────────────────────────────────────

    [Fact]
    public void AQuantityCarriesItsUnitAsUcum()
    {
        var quantity = CcdaValues.Quantity(Xml("""<e value="120" unit="mm[Hg]"/>"""))!;

        quantity.Value.Should().Be(120);
        quantity.Unit.Should().Be("mm[Hg]");
        quantity.System.Should().Be("http://unitsofmeasure.org");
    }

    [Fact]
    public void AQuantityWithNoNumber_IsNull()
        => CcdaValues.Quantity(Xml("""<e unit="mg"/>""")).Should().BeNull();
}
