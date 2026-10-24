using CloudHealthOffice.Ccda;
using FluentAssertions;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.Ccda.Tests;

/// <summary>
/// Problems, as Conditions. The failures guarded here are the ones that reach a
/// UM reviewer as a wrong clinical fact: a resolved problem shown as active, a
/// statement type recorded as a diagnosis, an onset date more precise than the
/// one the clinician wrote.
/// </summary>
public class CcdaProblemMapperTests
{
    private static readonly ResourceReference Member = new("Patient/member-1");

    private static CcdaSection ProblemsSection(string entries)
    {
        var xml = $"""
            <?xml version="1.0"?>
            <ClinicalDocument xmlns="{CcdaTemplates.CdaNamespace}">
              <templateId root="{CcdaTemplates.UsRealmHeader}"/>
              <component><structuredBody><component>
                <section>
                  <templateId root="2.16.840.1.113883.10.20.22.2.5"/>
                  <title>Problems</title>
                  {entries}
                </section>
              </component></structuredBody></component>
            </ClinicalDocument>
            """;

        return CcdaReader.Read(xml).Document!.Sections.Single();
    }

    private static string Concern(
        string status = "active",
        string code = "38341003",
        string system = "2.16.840.1.113883.6.96",
        string display = "Hypertension",
        string effective = """<low value="20200115"/>""") =>
        $"""
        <entry>
          <act classCode="ACT" moodCode="EVN">
            <templateId root="2.16.840.1.113883.10.20.22.4.3"/>
            <statusCode code="{status}"/>
            <entryRelationship typeCode="SUBJ">
              <observation classCode="OBS" moodCode="EVN">
                <templateId root="2.16.840.1.113883.10.20.22.4.4"/>
                <code code="55607006" codeSystem="2.16.840.1.113883.6.96" displayName="Problem"/>
                <effectiveTime>{effective}</effectiveTime>
                <value xsi:type="CD" code="{code}" codeSystem="{system}" displayName="{display}"
                       xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"/>
              </observation>
            </entryRelationship>
          </act>
        </entry>
        """;

    [Fact]
    public void AProblemBecomesACondition()
    {
        var condition = CcdaProblemMapper.Map(ProblemsSection(Concern()), Member).Should().ContainSingle().Subject;

        condition.Subject.Reference.Should().Be("Patient/member-1");
        condition.Code.Coding.Single().System.Should().Be("http://snomed.info/sct");
        condition.Code.Coding.Single().Code.Should().Be("38341003");
    }

    [Fact]
    public void TheDiagnosisComesFromValue_NeverFromCode()
    {
        // `code` says what KIND of statement this is ("Problem", "Diagnosis").
        // A reader that fell back to it would record every problem in every
        // document as the same condition.
        var condition = CcdaProblemMapper.Map(ProblemsSection(Concern()), Member).Single();

        condition.Code.Coding.Should().NotContain(c => c.Code == "55607006");
        condition.Category.Single().Coding.Single().Code.Should().Be("55607006");
    }

    [Theory]
    [InlineData("active", "active")]
    [InlineData("completed", "inactive")]
    [InlineData("aborted", "inactive")]
    [InlineData("suspended", "inactive")]
    public void TheConcernsStatusDecidesClinicalStatus(string concernStatus, string expected)
    {
        // The status lives on the CONCERN, not the observation. Flattening to
        // the observation loses it entirely and shows every problem as active.
        var condition = CcdaProblemMapper.Map(ProblemsSection(Concern(status: concernStatus)), Member).Single();

        condition.ClinicalStatus.Coding.Single().Code.Should().Be(expected);
    }

    [Fact]
    public void ACompletedConcernIsInactive_NotResolved()
    {
        // A completed concern means the clinician stopped tracking it. Reading
        // that as `resolved` states a clinical outcome from an administrative
        // fact.
        CcdaProblemMapper.Map(ProblemsSection(Concern(status: "completed")), Member).Single()
            .ClinicalStatus.Coding.Single().Code.Should().NotBe("resolved");
    }

    [Fact]
    public void AYearOnlyOnsetStaysAYear()
    {
        // A reviewer uses onset to decide whether a condition predates
        // coverage. Widening "2020" to 2020-01-01 invents the day it turns on.
        var condition = CcdaProblemMapper.Map(
            ProblemsSection(Concern(effective: """<low value="2020"/>""")), Member).Single();

        condition.Onset.Should().BeOfType<Date>().Which.Value.Should().Be("2020");
    }

    [Fact]
    public void AnOngoingProblemHasNoAbatement()
        => CcdaProblemMapper.Map(ProblemsSection(Concern()), Member).Single()
            .Abatement.Should().BeNull("an abatement invented here would say the problem resolved");

    [Fact]
    public void AResolvedProblemCarriesItsAbatement()
    {
        var condition = CcdaProblemMapper.Map(ProblemsSection(
            Concern(effective: """<low value="20200115"/><high value="20231231"/>""")), Member).Single();

        condition.Abatement.Should().BeOfType<Date>().Which.Value.Should().Be("2023-12-31");
    }

    [Fact]
    public void AnObservationWithNoValue_YieldsNothing()
    {
        // Rather than a Condition with no code, which would show a reviewer a
        // diagnosis that says nothing.
        var withoutValue = """
            <entry><act classCode="ACT" moodCode="EVN">
              <templateId root="2.16.840.1.113883.10.20.22.4.3"/>
              <statusCode code="active"/>
              <entryRelationship typeCode="SUBJ"><observation classCode="OBS" moodCode="EVN">
                <templateId root="2.16.840.1.113883.10.20.22.4.4"/>
              </observation></entryRelationship>
            </act></entry>
            """;

        CcdaProblemMapper.Map(ProblemsSection(withoutValue), Member).Should().BeEmpty();
    }

    [Fact]
    public void AConcernCarryingSeveralObservations_YieldsOneConditionEach()
    {
        // One concern can track more than one problem. Taking only the first
        // observation would silently drop the rest.
        const string two = """
            <entry>
              <act classCode="ACT" moodCode="EVN">
                <templateId root="2.16.840.1.113883.10.20.22.4.3"/>
                <statusCode code="active"/>
                <entryRelationship typeCode="SUBJ">
                  <observation classCode="OBS" moodCode="EVN">
                    <templateId root="2.16.840.1.113883.10.20.22.4.4"/>
                    <value xsi:type="CD" code="38341003" codeSystem="2.16.840.1.113883.6.96"
                           displayName="Hypertension" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"/>
                  </observation>
                </entryRelationship>
                <entryRelationship typeCode="SUBJ">
                  <observation classCode="OBS" moodCode="EVN">
                    <templateId root="2.16.840.1.113883.10.20.22.4.4"/>
                    <value xsi:type="CD" code="73211009" codeSystem="2.16.840.1.113883.6.96"
                           displayName="Diabetes" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"/>
                  </observation>
                </entryRelationship>
              </act>
            </entry>
            """;

        var conditions = CcdaProblemMapper.Map(ProblemsSection(two), Member);

        conditions.Should().HaveCount(2);
        conditions.Select(c => c.Code.Coding.Single().Code)
            .Should().BeEquivalentTo(["38341003", "73211009"]);
    }

    [Fact]
    public void ASectionOfAnotherKind_MapsNothing()
    {
        // The mapper is asked for problems; handed medications it returns
        // nothing rather than reading entries it has no rules for.
        var medications = CcdaReader.Read($"""
            <ClinicalDocument xmlns="{CcdaTemplates.CdaNamespace}">
              <templateId root="{CcdaTemplates.UsRealmHeader}"/>
              <component><structuredBody><component><section>
                <templateId root="2.16.840.1.113883.10.20.22.2.1"/>
              </section></component></structuredBody></component>
            </ClinicalDocument>
            """).Document!.Sections.Single();

        CcdaProblemMapper.Map(medications, Member).Should().BeEmpty();
    }
}
