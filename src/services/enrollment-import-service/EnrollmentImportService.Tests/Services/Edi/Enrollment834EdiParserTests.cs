using EnrollmentImportService.Services.Edi;

namespace EnrollmentImportService.Tests.Services.Edi;

public class Enrollment834EdiParserTests
{
    // Same content as docs/testing/test-x12-834-enrollment-sample.edi — keep
    // the two in sync. A 005010X220A1-shaped file: every member (subscriber
    // or dependent) is its own Loop 2000 starting at INS, dependents are tied
    // to their subscriber by REF*0F, and LS/LE wraps Loop 2700 reporting
    // categories (not dependents).
    //   SMITH    subscriber + spouse + child, all 021 (add); subscriber also
    //            carries a 2100C mailing address, a 2310 PCP and a 2700
    //            reporting category, none of which may leak into demographics.
    //   JOHNSON  subscriber + spouse, 001 (change).
    //   WILLIAMS subscriber + child, 024 (term) with 2300 DTP*349.
    private const string SampleEdi = """
        ISA*00*          *00*          *ZZ*SPONSOR123     *ZZ*PAYER456       *260206*1200*^*00501*000000001*0*P*:~
        GS*BE*SPONSOR123*PAYER456*20260206*1200*1*X*005010X220A1~
        ST*834*0001*005010X220A1~
        BGN*00*ABC123456*20260206*120000*ET***2~
        REF*38*1234567890~
        DTP*007*D8*20260201~
        N1*P5*Acme Corporation*FI*123456789~
        N1*IN*Blue Shield of California*FI*987654321~
        INS*Y*18*021*28*A***FT~
        REF*0F*BSCA123456789~
        REF*1L*GRP0001~
        REF*ZZ*EMP001234~
        DTP*303*D8*20260201~
        DTP*336*D8*20200115~
        NM1*IL*1*SMITH*JOHN*A***34*123456789~
        PER*IP**HP*4155550100~
        N3*123 MAIN STREET~
        N4*SAN FRANCISCO*CA*94102~
        DMG*D8*19850315*M~
        NM1*31*1~
        N3*PO BOX 900~
        N4*SAN FRANCISCO*CA*94120~
        HD*021**HLT*Blue Shield PPO*FAM~
        DTP*348*D8*20260201~
        LX*1~
        NM1*P3*1*NGUYEN*LINDA****XX*1234567893~
        HD*021**DEN*Dental Basic*FAM~
        DTP*348*D8*20260201~
        HD*021**VIS*Vision Standard*FAM~
        DTP*348*D8*20260201~
        LS*2700~
        LX*1~
        N1*75*DEPARTMENT~
        REF*17*ENGINEERING~
        DTP*007*D8*20260201~
        LE*2700~
        INS*N*01*021*28*A~
        REF*0F*BSCA123456789~
        REF*1L*GRP0001~
        REF*23*BSCA123456789-02~
        DTP*303*D8*20260201~
        NM1*IL*1*SMITH*JANE*M***34*234567890~
        N3*123 MAIN STREET~
        N4*SAN FRANCISCO*CA*94102~
        DMG*D8*19870520*F~
        HD*021**HLT*Blue Shield PPO*FAM~
        DTP*348*D8*20260201~
        INS*N*19*021*28*A~
        REF*0F*BSCA123456789~
        REF*1L*GRP0001~
        DTP*303*D8*20260201~
        NM1*IL*1*SMITH*MICHAEL*J~
        N3*123 MAIN STREET~
        N4*SAN FRANCISCO*CA*94102~
        DMG*D8*20150610*M~
        HD*021**HLT*Blue Shield PPO*FAM~
        DTP*348*D8*20260201~
        INS*Y*18*001*AI*A***FT~
        REF*0F*BSCA987654321~
        REF*1L*GRP0001~
        REF*ZZ*EMP005678~
        DTP*303*D8*20260301~
        NM1*IL*1*JOHNSON*SARAH*L***34*234567891~
        N3*456 OAK AVENUE*APT 2B~
        N4*LOS ANGELES*CA*90012~
        DMG*D8*19920408*F~
        HD*001**HLT*Blue Shield HMO*ESP~
        DTP*348*D8*20250101~
        INS*N*01*001*AI*A~
        REF*0F*BSCA987654321~
        REF*1L*GRP0001~
        REF*23*BSCA987654321-02~
        DTP*303*D8*20260301~
        NM1*IL*1*JOHNSON*ROBERT*K***34*345678902~
        N3*456 OAK AVENUE*APT 2B~
        N4*LOS ANGELES*CA*90012~
        DMG*D8*19900115*M~
        HD*001**HLT*Blue Shield HMO*ESP~
        DTP*348*D8*20250101~
        INS*Y*18*024*07*A***TE~
        REF*0F*BSCA555666777~
        REF*1L*GRP0001~
        DTP*303*D8*20260131~
        NM1*IL*1*WILLIAMS*ROBERT*T***34*456789012~
        N3*789 ELM STREET~
        N4*SAN DIEGO*CA*92101~
        DMG*D8*19780922*M~
        HD*024**HLT*Blue Shield PPO*ECH~
        DTP*348*D8*20250115~
        DTP*349*D8*20260131~
        INS*N*19*024*07*A~
        REF*0F*BSCA555666777~
        REF*1L*GRP0001~
        REF*23*BSCA555666777-03~
        DTP*303*D8*20260131~
        NM1*IL*1*WILLIAMS*EMMA*R~
        N3*789 ELM STREET~
        N4*SAN DIEGO*CA*92101~
        DMG*D8*20120304*F~
        HD*024**HLT*Blue Shield PPO*ECH~
        DTP*348*D8*20250115~
        DTP*349*D8*20260131~
        SE*101*0001~
        GE*1*1~
        IEA*1*000000001~
        """;

    private const string Isa =
        "ISA*00*          *00*          *ZZ*SPONSOR123     *ZZ*PAYER456       *260206*1200*^*00501*000000001*0*P*:~";

    private static Enrollment834EdiParser MakeParser() => new();

    [Fact]
    public void Parse_EachInsWithInsY_IsOneSubscriberEnrollment()
    {
        var result = MakeParser().Parse(SampleEdi, "sample.edi");

        result.Enrollments.Should().HaveCount(3);
        result.Enrollments.Select(e => e.SubscriberId)
            .Should().Equal("BSCA123456789", "BSCA987654321", "BSCA555666777");
        result.DependentEnrollments.Should().BeEmpty();
        result.TransactionCount.Should().Be(3);
    }

    [Fact]
    public void Parse_DependentInsLoops_AreAttachedToTheirSubscriberByRef0F_NotFlattenedIntoTheSubscriber()
    {
        var result = MakeParser().Parse(SampleEdi, "sample.edi");

        var smith = result.Enrollments[0];
        smith.Demographics!.FirstName.Should().Be("JOHN");
        smith.Demographics.DateOfBirth.Should().Be("19850315");
        smith.Relationship.Should().Be("18");
        smith.Dependents.Should().HaveCount(2);

        var jane = smith.Dependents[0];
        jane.FirstName.Should().Be("JANE");
        jane.LastName.Should().Be("SMITH");
        jane.DateOfBirth.Should().Be("19870520");
        jane.Gender.Should().Be("F");
        jane.Relationship.Should().Be("01");
        jane.MaintenanceType.Should().Be("021");
        jane.SubscriberId.Should().Be("BSCA123456789");
        jane.MemberIdentifier.Should().Be("BSCA123456789-02");
        jane.IdQualifier.Should().Be("34");
        jane.Id.Should().Be("234567890");

        var michael = smith.Dependents[1];
        michael.FirstName.Should().Be("MICHAEL");
        michael.Relationship.Should().Be("19");
        michael.DateOfBirth.Should().Be("20150610");
        michael.MemberIdentifier.Should().BeNull();

        var johnson = result.Enrollments[1];
        johnson.Demographics!.FirstName.Should().Be("SARAH");
        johnson.Dependents.Should().ContainSingle();
        johnson.Dependents[0].FirstName.Should().Be("ROBERT");
        johnson.Dependents[0].Relationship.Should().Be("01");
        johnson.Dependents[0].MaintenanceType.Should().Be("001");

        var williams = result.Enrollments[2];
        williams.Dependents.Should().ContainSingle();
        williams.Dependents[0].FirstName.Should().Be("EMMA");
        williams.Dependents[0].MaintenanceType.Should().Be("024");
    }

    [Fact]
    public void Parse_OtherNm1Loops_DoNotOverwriteMemberDemographics()
    {
        // 2100C mailing address (NM1*31) and the 2310 PCP (NM1*P3) reuse
        // NM1/N3/N4 — they must not replace the 2100A residence/name.
        var result = MakeParser().Parse(SampleEdi, "sample.edi");

        var smith = result.Enrollments[0].Demographics!;
        smith.LastName.Should().Be("SMITH");
        smith.FirstName.Should().Be("JOHN");
        smith.MiddleName.Should().Be("A");
        smith.Address1.Should().Be("123 MAIN STREET");
        smith.Zip.Should().Be("94102");
        smith.Id.Should().Be("123456789");
    }

    [Fact]
    public void Parse_LsLe_IsLoop2700ReportingCategories_NotADependent()
    {
        var result = MakeParser().Parse(SampleEdi, "sample.edi");

        var smith = result.Enrollments[0];
        smith.ReportingCategories.Should().ContainSingle();
        var category = smith.ReportingCategories![0];
        category.Name.Should().Be("DEPARTMENT");
        category.ReferenceQualifier.Should().Be("17");
        category.ReferenceValue.Should().Be("ENGINEERING");
        category.Date.Should().Be("20260201");

        // 2700's REF/DTP must not leak into member-level fields.
        smith.SubscriberId.Should().Be("BSCA123456789");
        smith.EnrollmentDate.Should().Be("20260201");
    }

    [Fact]
    public void Parse_CoverageLines_AreScopedPerMember_WithBenefitDatesFromLoop2300()
    {
        var result = MakeParser().Parse(SampleEdi, "sample.edi");

        var smith = result.Enrollments[0];
        smith.Coverage.Select(c => c.InsuranceLineCode).Should().Equal("HLT", "DEN", "VIS");
        smith.Coverage.Should().OnlyContain(c => c.BenefitBeginDate == "20260201" && c.BenefitEndDate == null);
        smith.Coverage[0].PlanCoverageDescription.Should().Be("Blue Shield PPO");
        smith.Coverage[0].CoverageLevel.Should().Be("FAM");
        smith.Coverage[0].MaintenanceType.Should().Be("021");

        smith.Dependents[0].Coverage.Should().ContainSingle(c => c.InsuranceLineCode == "HLT");
        smith.Dependents[1].Coverage.Should().ContainSingle(c => c.InsuranceLineCode == "HLT");

        var emma = result.Enrollments[2].Dependents[0];
        emma.Coverage.Should().ContainSingle();
        emma.Coverage![0].BenefitBeginDate.Should().Be("20250115");
        emma.Coverage[0].BenefitEndDate.Should().Be("20260131");
    }

    [Fact]
    public void Parse_Termination_TakesTerminationDateFromCoverageDtp349()
    {
        var result = MakeParser().Parse(SampleEdi, "sample.edi");

        var williams = result.Enrollments[2];
        williams.MaintenanceType.Should().Be("024");
        williams.MaintenanceReason.Should().Be("07");
        williams.TerminationDate.Should().Be("20260131");
        williams.Dependents[0].TerminationDate.Should().Be("20260131");
    }

    [Fact]
    public void Parse_ReferenceAndDateFields_MapToTheOwningMember()
    {
        var result = MakeParser().Parse(SampleEdi, "sample.edi");

        var smith = result.Enrollments[0];
        smith.SubscriberId.Should().Be("BSCA123456789");
        smith.GroupNumber.Should().Be("GRP0001");
        smith.EmployeeId.Should().Be("EMP001234");
        smith.EnrollmentDate.Should().Be("20260201");
        smith.EmploymentStartDate.Should().Be("20200115");
        smith.TerminationDate.Should().BeNull();

        var johnson = result.Enrollments[1];
        johnson.MaintenanceType.Should().Be("001");
        johnson.EnrollmentDate.Should().Be("20260301");
        johnson.Dependents[0].EnrollmentDate.Should().Be("20260301");
        johnson.Dependents[0].GroupNumber.Should().Be("GRP0001");
    }

    [Fact]
    public void Parse_Dtp356IsEligibilityBegin_AndDtp357IsEligibilityEnd()
    {
        var edi = Isa + """
            ST*834*0001*005010X220A1~
            INS*Y*18*001*AI*A~
            REF*0F*SUB1~
            DTP*356*D8*20250101~
            DTP*357*D8*20261231~
            NM1*IL*1*DOE*JANE~
            SE*7*0001~
            """;

        var member = MakeParser().Parse(edi, "x.edi").Enrollments.Single();

        member.EligibilityBeginDate.Should().Be("20250101");
        member.TerminationDate.Should().Be("20261231");
    }

    [Fact]
    public void Parse_DependentWithoutItsSubscriberInTheFile_IsReturnedAsADependentEnrollment()
    {
        // A newborn add sent on its own, without resending the subscriber.
        var edi = Isa + """
            ST*834*0001*005010X220A1~
            N1*P5*Acme Corporation*FI*123456789~
            INS*N*19*021*02*A~
            REF*0F*BSCA123456789~
            REF*1L*GRP0001~
            NM1*IL*1*SMITH*BABY~
            DMG*D8*20260115*F~
            HD*021**HLT*Blue Shield PPO*FAM~
            DTP*348*D8*20260115~
            SE*10*0001~
            """;

        var result = MakeParser().Parse(edi, "x.edi");

        result.Enrollments.Should().BeEmpty();
        result.DependentEnrollments.Should().ContainSingle();
        var baby = result.DependentEnrollments[0];
        baby.SubscriberId.Should().Be("BSCA123456789");
        baby.Relationship.Should().Be("19");
        baby.MaintenanceType.Should().Be("021");
        baby.GroupNumber.Should().Be("GRP0001");
        baby.Coverage.Should().ContainSingle(c => c.BenefitBeginDate == "20260115");
        result.TransactionCount.Should().Be(1);
    }

    [Fact]
    public void Parse_DependentIsMatchedBySubscriberId_NotByPosition()
    {
        // Dependent of SUB-A arrives after SUB-B's loop.
        var edi = Isa + """
            ST*834*0001*005010X220A1~
            INS*Y*18*021*28*A~
            REF*0F*SUB-A~
            NM1*IL*1*ALPHA*ANN~
            INS*Y*18*021*28*A~
            REF*0F*SUB-B~
            NM1*IL*1*BETA*BOB~
            INS*N*01*021*28*A~
            REF*0F*SUB-A~
            NM1*IL*1*ALPHA*AL~
            SE*11*0001~
            """;

        var result = MakeParser().Parse(edi, "x.edi");

        result.Enrollments[0].Dependents.Should().ContainSingle(d => d.FirstName == "AL");
        result.Enrollments[1].Dependents.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SubscriberLookup_DoesNotCrossTransactionSets()
    {
        var edi = Isa + """
            ST*834*0001*005010X220A1~
            N1*P5*First Sponsor*FI*111111111~
            INS*Y*18*021*28*A~
            REF*0F*SUB-A~
            NM1*IL*1*ALPHA*ANN~
            SE*5*0001~
            ST*834*0002*005010X220A1~
            N1*P5*Second Sponsor*FI*222222222~
            INS*N*01*021*28*A~
            REF*0F*SUB-A~
            NM1*IL*1*ALPHA*AL~
            SE*5*0002~
            """;

        var result = MakeParser().Parse(edi, "x.edi");

        result.Enrollments.Should().ContainSingle();
        result.Enrollments[0].Sponsor!.Name.Should().Be("First Sponsor");
        result.Enrollments[0].Dependents.Should().BeEmpty();
        result.DependentEnrollments.Should().ContainSingle(d => d.FirstName == "AL");
    }

    [Fact]
    public void Parse_SponsorFromHeaderLoop_IsAppliedToEverySubscriber()
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
