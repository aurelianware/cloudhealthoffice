using CloudHealthOffice.Ccda;
using FluentAssertions;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.Ccda.Tests;

/// <summary>
/// Medications, as MedicationRequests. The trap this section sets is that the
/// element naming WHEN a drug is taken and the element naming WHEN it was
/// prescribed are both <c>effectiveTime</c> — so the tests below are mostly
/// about reading the right one.
/// </summary>
public class CcdaMedicationMapperTests
{
    private static readonly ResourceReference Member = new("Patient/member-1");

    private static CcdaSection MedicationsSection(string entries)
        => CcdaReader.Read($"""
            <?xml version="1.0"?>
            <ClinicalDocument xmlns="{CcdaTemplates.CdaNamespace}">
              <templateId root="{CcdaTemplates.UsRealmHeader}"/>
              <component><structuredBody><component>
                <section>
                  <templateId root="2.16.840.1.113883.10.20.22.2.1"/>
                  <title>Medications</title>
                  {entries}
                </section>
              </component></structuredBody></component>
            </ClinicalDocument>
            """).Document!.Sections.Single();

    private static string Activity(
        string status = "active",
        string drug = "197361",
        string drugName = "Lisinopril 10 MG Oral Tablet",
        string effectiveTimes = """
            <effectiveTime xsi:type="IVL_TS" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <low value="20240301"/>
            </effectiveTime>
            <effectiveTime xsi:type="PIVL_TS" operator="A"
                           xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <period value="24" unit="h"/>
            </effectiveTime>
            """,
        string extras = """
            <routeCode code="C38288" codeSystem="2.16.840.1.113883.3.26.1.1" displayName="Oral"/>
            <doseQuantity value="1" unit="{tablet}"/>
            """) =>
        $"""
        <entry>
          <substanceAdministration classCode="SBADM" moodCode="EVN">
            <templateId root="2.16.840.1.113883.10.20.22.4.16"/>
            <statusCode code="{status}"/>
            {effectiveTimes}
            {extras}
            <consumable>
              <manufacturedProduct classCode="MANU">
                <manufacturedMaterial>
                  <code code="{drug}" codeSystem="2.16.840.1.113883.6.88" displayName="{drugName}"/>
                </manufacturedMaterial>
              </manufacturedProduct>
            </consumable>
          </substanceAdministration>
        </entry>
        """;

    [Fact]
    public void AMedicationBecomesAMedicationRequest()
    {
        var request = CcdaMedicationMapper.Map(MedicationsSection(Activity()), Member)
            .Should().ContainSingle().Subject;

        request.Subject.Reference.Should().Be("Patient/member-1");
        var drug = request.Medication.Should().BeOfType<CodeableConcept>().Subject;
        drug.Coding.Single().Code.Should().Be("197361");
        drug.Coding.Single().System.Should().Be("http://www.nlm.nih.gov/research/umls/rxnorm");
        request.Intent.Should().Be(MedicationRequest.MedicationRequestIntent.Order);
    }

    [Fact]
    public void TheDrugSerialisesAsR4sMedicationCodeableConcept()
    {
        // R4 spells `medication[x]` as a choice; R5 replaced it with a
        // CodeableReference. Both compile against the shared datatypes, and the
        // wrong one produces an element no R4 consumer or validator accepts —
        // which only shows up in the JSON, so this is asserted there.
        var request = CcdaMedicationMapper.Map(MedicationsSection(Activity()), Member).Single();

        var json = new Hl7.Fhir.Serialization.FhirJsonSerializer().SerializeToString(request);

        json.Should().Contain("medicationCodeableConcept");
        json.Should().NotContain("\"concept\"");
    }

    [Fact]
    public void TheDosingScheduleIsReadFromThePeriodicEffectiveTime()
    {
        // Both elements are called effectiveTime. Taking the first would turn a
        // start date into a dosing frequency.
        var dosage = CcdaMedicationMapper.Map(MedicationsSection(Activity()), Member)
            .Single().DosageInstruction.Should().ContainSingle().Subject;

        dosage.Timing.Repeat.Period.Should().Be(24);
        dosage.Timing.Repeat.PeriodUnit.Should().Be(Timing.UnitsOfTime.H);
    }

    [Fact]
    public void TheScheduleIsFoundWhicheverOrderTheSenderWroteThem()
    {
        // Senders disagree about the order, so selection is by attribute.
        var reversed = Activity(effectiveTimes: """
            <effectiveTime xsi:type="PIVL_TS" operator="A"
                           xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <period value="8" unit="h"/>
            </effectiveTime>
            <effectiveTime xsi:type="IVL_TS" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <low value="20240301"/>
            </effectiveTime>
            """);

        CcdaMedicationMapper.Map(MedicationsSection(reversed), Member).Single()
            .DosageInstruction.Single().Timing.Repeat.Period.Should().Be(8);
    }

    [Fact]
    public void AnUnrecognisedTimeUnit_YieldsNoTimingRatherThanADefault()
    {
        // A schedule in the wrong unit is a dosing error on a reviewer's screen.
        var oddUnit = Activity(effectiveTimes: """
            <effectiveTime xsi:type="PIVL_TS" operator="A"
                           xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <period value="3" unit="fortnight"/>
            </effectiveTime>
            """);

        CcdaMedicationMapper.Map(MedicationsSection(oddUnit), Member).Single()
            .DosageInstruction.Single().Timing.Should().BeNull();
    }

    [Fact]
    public void TheDoseAndRouteAreCarried()
    {
        var dosage = CcdaMedicationMapper.Map(MedicationsSection(Activity()), Member)
            .Single().DosageInstruction.Single();

        dosage.DoseAndRate.Single().Dose.Should().BeOfType<Quantity>()
            .Which.Value.Should().Be(1);
        dosage.Route.Coding.Single().Code.Should().Be("C38288");
    }

    [Theory]
    [InlineData("active", MedicationRequest.MedicationrequestStatus.Active)]
    [InlineData("completed", MedicationRequest.MedicationrequestStatus.Completed)]
    [InlineData("aborted", MedicationRequest.MedicationrequestStatus.Stopped)]
    [InlineData("suspended", MedicationRequest.MedicationrequestStatus.OnHold)]
    public void TheActivitysStatusIsCarried(string status, MedicationRequest.MedicationrequestStatus expected)
        => CcdaMedicationMapper.Map(MedicationsSection(Activity(status: status)), Member).Single()
            .Status.Should().Be(expected);

    [Fact]
    public void AMissingStatus_IsUnknown_NotActive()
    {
        // Guessing active would put a discontinued drug on a reviewer's
        // current-medications list.
        var noStatus = Activity().Replace("""<statusCode code="active"/>""", string.Empty);

        CcdaMedicationMapper.Map(MedicationsSection(noStatus), Member).Single()
            .Status.Should().Be(MedicationRequest.MedicationrequestStatus.Unknown);
    }

    [Fact]
    public void AMedicationNamedOnlyInProse_YieldsNothing()
    {
        // A MedicationRequest with no coded drug cannot be checked against
        // anything, and a reviewer reading one would assume it had been coded.
        var uncoded = """
            <entry><substanceAdministration classCode="SBADM" moodCode="EVN">
              <templateId root="2.16.840.1.113883.10.20.22.4.16"/>
              <statusCode code="active"/>
              <text>Lisinopril, one daily</text>
              <consumable><manufacturedProduct classCode="MANU">
                <manufacturedMaterial/>
              </manufacturedProduct></consumable>
            </substanceAdministration></entry>
            """;

        CcdaMedicationMapper.Map(MedicationsSection(uncoded), Member).Should().BeEmpty();
    }

    [Fact]
    public void AnActivityWithNoDosingDetail_HasNoDosageInstruction()
    {
        var bare = Activity(effectiveTimes: string.Empty, extras: string.Empty);

        CcdaMedicationMapper.Map(MedicationsSection(bare), Member).Single()
            .DosageInstruction.Should().BeEmpty("an empty dosage would read as a dose of nothing");
    }

    [Fact]
    public void ASectionOfAnotherKind_MapsNothing()
    {
        var problems = CcdaReader.Read($"""
            <ClinicalDocument xmlns="{CcdaTemplates.CdaNamespace}">
              <templateId root="{CcdaTemplates.UsRealmHeader}"/>
              <component><structuredBody><component><section>
                <templateId root="2.16.840.1.113883.10.20.22.2.5"/>
              </section></component></structuredBody></component>
            </ClinicalDocument>
            """).Document!.Sections.Single();

        CcdaMedicationMapper.Map(problems, Member).Should().BeEmpty();
    }
}
