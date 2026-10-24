using CloudHealthOffice.Ccda;
using FluentAssertions;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.Ccda.Tests;

/// <summary>
/// Allergies, as AllergyIntolerances. The failure that matters most here is
/// losing the substance or the severity: a reviewer approving a drug needs to
/// see that the member reacts to it, and how badly.
/// </summary>
public class CcdaAllergyMapperTests
{
    private static readonly ResourceReference Member = new("Patient/member-1");

    private static CcdaSection AllergiesSection(string entries)
        => CcdaReader.Read($"""
            <?xml version="1.0"?>
            <ClinicalDocument xmlns="{CcdaTemplates.CdaNamespace}">
              <templateId root="{CcdaTemplates.UsRealmHeader}"/>
              <component><structuredBody><component>
                <section>
                  <templateId root="2.16.840.1.113883.10.20.22.2.6"/>
                  <title>Allergies</title>
                  {entries}
                </section>
              </component></structuredBody></component>
            </ClinicalDocument>
            """).Document!.Sections.Single();

    private static string Concern(
        string status = "active",
        string type = "416098002",
        string substance = "7980",
        string substanceSystem = "2.16.840.1.113883.6.88",
        string substanceName = "Penicillin",
        string reactions = "") =>
        $"""
        <entry>
          <act classCode="ACT" moodCode="EVN">
            <templateId root="2.16.840.1.113883.10.20.22.4.30"/>
            <statusCode code="{status}"/>
            <entryRelationship typeCode="SUBJ">
              <observation classCode="OBS" moodCode="EVN">
                <templateId root="2.16.840.1.113883.10.20.22.4.7"/>
                <effectiveTime><low value="20180612"/></effectiveTime>
                <value xsi:type="CD" code="{type}" codeSystem="2.16.840.1.113883.6.96"
                       xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"/>
                <participant typeCode="CSM">
                  <participantRole classCode="MANU">
                    <playingEntity classCode="MMAT">
                      <code code="{substance}" codeSystem="{substanceSystem}" displayName="{substanceName}"/>
                    </playingEntity>
                  </participantRole>
                </participant>
                {reactions}
              </observation>
            </entryRelationship>
          </act>
        </entry>
        """;

    private static string Reaction(string manifestation, string? severity = null)
    {
        var severityXml = severity is null
            ? string.Empty
            : $"""
               <entryRelationship typeCode="SUBJ">
                 <observation classCode="OBS" moodCode="EVN">
                   <templateId root="2.16.840.1.113883.10.20.22.4.8"/>
                   <value xsi:type="CD" code="{severity}" codeSystem="2.16.840.1.113883.6.96"
                          xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"/>
                 </observation>
               </entryRelationship>
               """;

        return $"""
            <entryRelationship typeCode="MFST">
              <observation classCode="OBS" moodCode="EVN">
                <templateId root="2.16.840.1.113883.10.20.22.4.9"/>
                <value xsi:type="CD" code="{manifestation}" codeSystem="2.16.840.1.113883.6.96"
                       xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"/>
                {severityXml}
              </observation>
            </entryRelationship>
            """;
    }

    [Fact]
    public void TheSubstanceComesFromThePlayingEntity_NotFromValue()
    {
        // `value` holds the KIND of intolerance. A mapper that used it as the
        // substance would record every allergy as "drug allergy" with nothing
        // the member is actually allergic to.
        var allergy = CcdaAllergyMapper.Map(AllergiesSection(Concern()), Member)
            .Should().ContainSingle().Subject;

        allergy.Patient.Reference.Should().Be("Patient/member-1");
        allergy.Code.Coding.Single().Code.Should().Be("7980");
        allergy.Code.Coding.Single().System.Should().Be("http://www.nlm.nih.gov/research/umls/rxnorm");
        allergy.Code.Coding.Should().NotContain(c => c.Code == "416098002");
    }

    [Fact]
    public void AnAllergyWithNoSubstance_YieldsNothing()
    {
        // Better than an AllergyIntolerance naming nothing, which tells a
        // reviewer a member is allergic without saying to what.
        var withoutParticipant = """
            <entry><act classCode="ACT" moodCode="EVN">
              <templateId root="2.16.840.1.113883.10.20.22.4.30"/>
              <statusCode code="active"/>
              <entryRelationship typeCode="SUBJ"><observation classCode="OBS" moodCode="EVN">
                <templateId root="2.16.840.1.113883.10.20.22.4.7"/>
              </observation></entryRelationship>
            </act></entry>
            """;

        CcdaAllergyMapper.Map(AllergiesSection(withoutParticipant), Member).Should().BeEmpty();
    }

    [Theory]
    [InlineData("416098002", AllergyIntolerance.AllergyIntoleranceCategory.Medication)]
    [InlineData("59037007", AllergyIntolerance.AllergyIntoleranceCategory.Medication)]
    [InlineData("414285001", AllergyIntolerance.AllergyIntoleranceCategory.Food)]
    [InlineData("235719002", AllergyIntolerance.AllergyIntoleranceCategory.Food)]
    [InlineData("426232007", AllergyIntolerance.AllergyIntoleranceCategory.Environment)]
    public void TheIntoleranceTypeBecomesTheCategory(
        string type, AllergyIntolerance.AllergyIntoleranceCategory expected)
        => CcdaAllergyMapper.Map(AllergiesSection(Concern(type: type)), Member).Single()
            .Category.Should().ContainSingle().Which.Should().Be(expected);

    [Fact]
    public void AnUnrecognisedIntoleranceType_LeavesTheCategoryUnset()
    {
        // A food allergy recorded as a drug allergy is worse than one recorded
        // as neither, so the nearest-looking category is not chosen.
        CcdaAllergyMapper.Map(AllergiesSection(Concern(type: "999999999")), Member).Single()
            .Category.Should().BeNullOrEmpty();
    }

    [Fact]
    public void ReactionsAndTheirSeveritiesAreCarried()
    {
        var allergy = CcdaAllergyMapper.Map(
            AllergiesSection(Concern(reactions: Reaction("247472004", severity: "24484000"))), Member).Single();

        var reaction = allergy.Reaction.Should().ContainSingle().Subject;
        reaction.Manifestation.Single().Coding.Single().Code.Should().Be("247472004");
        reaction.Severity.Should().Be(AllergyIntolerance.AllergyIntoleranceSeverity.Severe);
    }

    [Fact]
    public void SeverityStaysWithItsOwnReaction()
    {
        // The same allergy can cause a rash once and anaphylaxis the next time.
        // Hoisting severity onto the allergy averages away the dangerous one.
        var allergy = CcdaAllergyMapper.Map(AllergiesSection(Concern(
            reactions: Reaction("271807003", severity: "255604002")
                       + Reaction("39579001", severity: "24484000"))), Member).Single();

        allergy.Reaction.Should().HaveCount(2);
        allergy.Reaction.Single(r => r.Manifestation.Single().Coding.Single().Code == "271807003")
            .Severity.Should().Be(AllergyIntolerance.AllergyIntoleranceSeverity.Mild);
        allergy.Reaction.Single(r => r.Manifestation.Single().Coding.Single().Code == "39579001")
            .Severity.Should().Be(AllergyIntolerance.AllergyIntoleranceSeverity.Severe);
    }

    [Fact]
    public void AReactionWithNoRecordedSeverity_HasNone()
        => CcdaAllergyMapper.Map(AllergiesSection(Concern(reactions: Reaction("247472004"))), Member)
            .Single().Reaction.Single()
            .Severity.Should().BeNull("an invented severity would be read as a clinical judgement");

    [Theory]
    [InlineData("active", "active")]
    [InlineData("completed", "inactive")]
    public void TheConcernsStatusDecidesClinicalStatus(string status, string expected)
        => CcdaAllergyMapper.Map(AllergiesSection(Concern(status: status)), Member).Single()
            .ClinicalStatus.Coding.Single().Code.Should().Be(expected);

    [Fact]
    public void TheOnsetIsCarried()
        => CcdaAllergyMapper.Map(AllergiesSection(Concern()), Member).Single()
            .Onset.Should().BeOfType<Date>().Which.Value.Should().Be("2018-06-12");

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

        CcdaAllergyMapper.Map(problems, Member).Should().BeEmpty();
    }
}
