using EnrollmentImportService.Services.Edi;

namespace EnrollmentImportService.Tests.Services.Edi;

public class Enrollment834EdiParserTests
{
    // Same content as docs/testing/test-x12-834-enrollment-sample.edi:
    // 3 subscribers, two of whom have nested LS/LE dependent loops.
    private const string SampleEdi = """
        ISA*00*          *00*          *ZZ*SPONSOR123     *ZZ*PAYER456       *260206*1200*^*00501*000000001*0*P*:~
        GS*BE*SPONSOR123*PAYER456*20260206*1200*1*X*005010X220A1~
        ST*834*0001*005010X220A1~
        BGN*00*ABC123456*20260206*120000*ET***2~
        REF*38*1234567890~
        DTP*007*D8*20260201~
        N1*P5*Acme Corporation*FI*123456789~
        N1*IN*Blue Shield of California*FI*987654321~
        INS*Y*18*025*20*A***FT~
        REF*0F*BSCA123456789~
        REF*1L*GRP0001~
        REF*ZZ*EMP001234~
        DTP*303*D8*20260201~
        NM1*IL*1*SMITH*JOHN*A***34*123456789~
        N3*123 MAIN STREET~
        N4*SAN FRANCISCO*CA*94102~
        DMG*D8*19850315*M~
        HD*025**HLT*Blue Shield PPO*EMP~
        HD*025**DEN*Dental Basic*EMP~
        HD*025**VIS*Vision Standard*EMP~
        LS*2700~
        NM1*70*1*SMITH*JANE*M***34*234567890~
        DMG*D8*19870520*F~
        HD*025**HLT*Blue Shield PPO~
        LE*2700~
        LS*2700~
        NM1*70*1*SMITH*MICHAEL*J***34*345678901~
        N3*123 MAIN STREET~
        N4*SAN FRANCISCO*CA*94102~
        DMG*D8*20150610*M~
        HD*025**HLT*Blue Shield PPO~
        LE*2700~
        INS*Y*18*025*20*A***FT~
        REF*0F*BSCA987654321~
        REF*1L*GRP0001~
        REF*ZZ*EMP005678~
        DTP*303*D8*20260201~
        NM1*IL*1*JOHNSON*SARAH*L***34*234567890~
        N3*456 OAK AVENUE*APT 2B~
        N4*LOS ANGELES*CA*90012~
        DMG*D8*19920408*F~
        HD*025**HLT*Blue Shield HMO*ESP~
        LS*2700~
        NM1*70*1*JOHNSON*ROBERT*K***34*345678901~
        DMG*D8*19900115*M~
        HD*025**HLT*Blue Shield HMO~
        LE*2700~
        INS*Y*18*001*25*T***FT~
        REF*0F*BSCA555666777~
        DTP*303*D8*20250115~
        DTP*356*D8*20260131~
        NM1*IL*1*WILLIAMS*ROBERT*T***34*456789012~
        N3*789 ELM STREET~
        N4*SAN DIEGO*CA*92101~
        DMG*D8*19780922*M~
        SE*49*0001~
        GE*1*1~
        IEA*1*000000001~
        """;

    private static Enrollment834EdiParser MakeParser() => new();

    [Fact]
    public void Parse_FindsAllThreeSubscribers()
    {
        var result = MakeParser().Parse(SampleEdi, "sample.edi");

        result.Enrollments.Should().HaveCount(3);
        result.TransactionCount.Should().Be(3);
    }

    [Fact]
    public void Parse_SubscriberDemographics_AreNotOverwrittenByNestedDependentLoop()
    {
        // This is the exact bug the Indice.Edi spike surfaced: a naive
        // scalar binding on the subscriber's NM1/DMG gets silently
        // overwritten by the LAST NM1/DMG seen, including ones inside the
        // nested LS/LE dependent loop. The subscriber must keep his own
        // name and DOB, not his younger dependent's.
        var result = MakeParser().Parse(SampleEdi, "sample.edi");

        var smith = result.Enrollments[0];
        smith.Demographics!.LastName.Should().Be("SMITH");
        smith.Demographics.FirstName.Should().Be("JOHN");
        smith.Demographics.DateOfBirth.Should().Be("19850315");
        smith.Demographics.Gender.Should().Be("M");
    }

    [Fact]
    public void Parse_NestedDependents_AreAttributedToTheCorrectSubscriber()
    {
        var result = MakeParser().Parse(SampleEdi, "sample.edi");

        var smith = result.Enrollments[0];
        smith.Dependents.Should().HaveCount(2);

        smith.Dependents[0].LastName.Should().Be("SMITH");
        smith.Dependents[0].FirstName.Should().Be("JANE");
        smith.Dependents[0].DateOfBirth.Should().Be("19870520");
        smith.Dependents[0].Gender.Should().Be("F");

        smith.Dependents[1].LastName.Should().Be("SMITH");
        smith.Dependents[1].FirstName.Should().Be("MICHAEL");
        smith.Dependents[1].DateOfBirth.Should().Be("20150610");

        var johnson = result.Enrollments[1];
        johnson.Demographics!.LastName.Should().Be("JOHNSON");
        johnson.Demographics.FirstName.Should().Be("SARAH");
        johnson.Dependents.Should().ContainSingle();
        johnson.Dependents[0].FirstName.Should().Be("ROBERT");
        johnson.Dependents[0].LastName.Should().Be("JOHNSON");

        var williams = result.Enrollments[2];
        williams.Demographics!.LastName.Should().Be("WILLIAMS");
        williams.Dependents.Should().BeEmpty();
    }

    [Fact]
    public void Parse_CoverageLines_AreScopedToTheRightPerson_NotFlattenedAcrossTheWholeFamily()
    {
        var result = MakeParser().Parse(SampleEdi, "sample.edi");

        var smith = result.Enrollments[0];
        smith.Coverage.Should().HaveCount(3); // subscriber: HLT, DEN, VIS
        smith.Coverage.Select(c => c.InsuranceLineCode).Should().BeEquivalentTo(["HLT", "DEN", "VIS"]);

        smith.Dependents[0].Coverage.Should().ContainSingle(c => c.InsuranceLineCode == "HLT");
        smith.Dependents[1].Coverage.Should().ContainSingle(c => c.InsuranceLineCode == "HLT");
    }

    [Fact]
    public void Parse_ReferenceAndDateFields_MapToTheOwningSubscriber()
    {
        var result = MakeParser().Parse(SampleEdi, "sample.edi");

        var smith = result.Enrollments[0];
        smith.SubscriberId.Should().Be("BSCA123456789");
        smith.GroupNumber.Should().Be("GRP0001");
        smith.EmployeeId.Should().Be("EMP001234");
        smith.EnrollmentDate.Should().Be("20260201");

        var williams = result.Enrollments[2];
        williams.BenefitStatus.Should().Be("T");
        williams.EnrollmentDate.Should().Be("20250115");
        williams.TerminationDate.Should().Be("20260131");
    }

    [Fact]
    public void Parse_SponsorFromHeaderLoop_IsAppliedToEveryMember()
    {
        var result = MakeParser().Parse(SampleEdi, "sample.edi");

        result.Enrollments.Should().OnlyContain(m => m.Sponsor!.Name == "Acme Corporation");
    }

    [Fact]
    public void Parse_RejectsContentThatIsNotX12()
    {
        var act = () => MakeParser().Parse("not an edi file", "bad.edi");

        act.Should().Throw<X12FormatException>();
    }
}
