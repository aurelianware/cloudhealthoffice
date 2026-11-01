using EncounterSubmissionService.StateRoutes;
using static CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes.StateRouteTestData;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes;

public class Encounter837WriterTests
{
    private static string[] Segments(string content) =>
        content.Split('~', StringSplitOptions.RemoveEmptyEntries);

    private static string[] Ids(string content) =>
        Segments(content).Select(s => s.Split('*')[0]).ToArray();

    [Fact]
    public void Isa_IsFixedWidth_TestUsage_AndControlNumbersMatchTrailers()
    {
        var file = Encounter837Writer.Write(EncounterFormType.Professional, new[] { Professional() }, Envelope(42));
        var seg = Segments(file.Content);

        seg[0].Length.Should().Be(105, "ISA is 106 characters including its terminator");
        seg[0].Split('*')[13].Should().Be("000000042");
        seg[0].Split('*')[15].Should().Be("T");
        seg[1].Should().StartWith("GS*HC*TPID12345*TMHPTESTRCV*20261101*1430*42*X*005010X222A1");
        seg[^2].Should().Be("GE*1*42");
        seg[^1].Should().Be("IEA*1*000000042");
    }

    [Fact]
    public void ProductionUsage_OnlyWhenConfigured()
    {
        var file = Encounter837Writer.Write(EncounterFormType.Professional, new[] { Professional() },
            Envelope() with { UsageIndicator = "P" });
        Segments(file.Content)[0].Split('*')[15].Should().Be("P");

        var junk = Encounter837Writer.Write(EncounterFormType.Professional, new[] { Professional() },
            Envelope() with { UsageIndicator = "prod" });
        Segments(junk.Content)[0].Split('*')[15].Should().Be("T");
    }

    [Fact]
    public void Professional_HasEncounterShape_PlanAdjudicationAsOtherPayer()
    {
        var content = Encounter837Writer.Write(EncounterFormType.Professional, new[] { Professional() }, Envelope()).Content;
        var seg = Segments(content);

        seg.Should().Contain("BHT*0019*00*CLM-sub-1*20261101*1430*RP");
        seg.Should().Contain("NM1*40*2*TMHP*****46*TMHPTESTRCV");
        seg.Should().Contain("HL*1**20*1");
        seg.Should().Contain("NM1*85*2*AUSTIN PEDIATRICS PA*****XX*1234567893");
        seg.Should().Contain("N4*AUSTIN*TX*787014000");
        seg.Should().Contain("REF*EI*741234567");
        seg.Should().Contain("HL*2*1*22*0");
        seg.Should().Contain("SBR*S*18*STAR******MC");
        seg.Should().Contain("NM1*IL*1*DOE*ALEX****MI*512345678");
        seg.Should().Contain("DMG*D8*20150402*F");
        seg.Should().Contain("NM1*PR*2*TEXAS HHSC*****PI*TXMCD");
        seg.Should().Contain("CLM*CLM-sub-1*250***11:B:1*Y*A*Y*Y");
        seg.Should().Contain("REF*G1*PA-1");
        seg.Should().Contain("HI*ABK:J45909*ABF:R059");
        seg.Should().Contain("NM1*82*1*SMITH*JANE****XX*1245319599");
        seg.Should().Contain("SBR*P*18*STAR******HM");
        seg.Should().Contain("AMT*D*120");
        seg.Should().Contain("OI***Y*P**Y");
        seg.Should().Contain("NM1*PR*2*SAMPLE HEALTH PLAN*****PI*MCO123");
        seg.Should().Contain("SV1*HC:99213:25*150*UN*1***1:2");
        seg.Should().Contain("SVD*MCO123*90*HC:99213:25**1");
        seg.Should().Contain("CAS*CO*45*60");
        seg.Should().Contain("CAS*CO*45*65");
        seg.Should().Contain("CAS*PR*3*5");
        seg.Should().Contain("DTP*573*D8*20261020");
    }

    [Fact]
    public void Professional_LoopOrder_FollowsTheImplementationGuide()
    {
        var ids = Ids(Encounter837Writer.Write(EncounterFormType.Professional, new[] { Professional() }, Envelope()).Content);
        var order = string.Join(",", ids);

        order.Should().StartWith("ISA,GS,ST,BHT,NM1,PER,NM1,HL,PRV,NM1,N3,N4,REF,HL,SBR,NM1,N3,N4,DMG,NM1,CLM,REF,HI,NM1,SBR,AMT,OI,NM1,NM1,DTP,LX,SV1,DTP,SVD,CAS,DTP,LX,SV1,DTP,SVD,CAS,CAS,DTP,SE,GE,IEA");
    }

    [Fact]
    public void SegmentCount_InSe_CountsStThroughSe()
    {
        var content = Encounter837Writer.Write(EncounterFormType.Professional,
            new[] { Professional("a"), Professional("b") }, Envelope()).Content;
        var seg = Segments(content);

        foreach (var stIndex in seg.Select((s, i) => (s, i)).Where(x => x.s.StartsWith("ST*")).Select(x => x.i))
        {
            var seIndex = Array.FindIndex(seg, stIndex, s => s.StartsWith("SE*"));
            var se = seg[seIndex].Split('*');
            int.Parse(se[1]).Should().Be(seIndex - stIndex + 1);
            se[2].Should().Be(seg[stIndex].Split('*')[2]);
        }
    }

    [Fact]
    public void EachEncounter_IsItsOwnTransactionSet_AndPlacementsRecordIt()
    {
        var file = Encounter837Writer.Write(EncounterFormType.Professional,
            new[] { Professional("a"), Professional("b") }, Envelope(7));

        Segments(file.Content).Count(s => s.StartsWith("ST*")).Should().Be(2);
        Segments(file.Content).Should().Contain("GE*2*7");
        file.Placements.Select(p => p.TransactionSetControlNumber).Should().Equal("0001", "0002");
        file.Placements.Select(p => p.SubmissionId).Should().Equal("a", "b");
        file.Placements.Should().OnlyContain(p => p.InterchangeControlNumber == "000000007" && p.GroupControlNumber == "7");
    }

    [Fact]
    public void Institutional_UsesFacilityType_StatementDates_ClAndRevenueCodes()
    {
        var content = Encounter837Writer.Write(EncounterFormType.Institutional, new[] { Institutional() }, Envelope()).Content;
        var seg = Segments(content);

        seg.Should().Contain(s => s.StartsWith("GS*") && s.EndsWith("*005010X223A2"));
        seg.Should().Contain("CLM*CLM-sub-2*5000***11:A:1**A*Y*Y");
        seg.Should().Contain("DTP*435*DT*202609280830");
        seg.Should().Contain("DTP*434*RD8*20260928-20261001");
        seg.Should().Contain("CL1*1*7*01");
        seg.Should().Contain("HI*ABK:J189:::::::Y");
        seg.Should().Contain("HI*ABF:E119:::::::N");
        seg.Should().Contain("NM1*71*1*JONES*AL****XX*1245319599");
        seg.Should().Contain("OI***Y***Y");
        seg.Should().Contain("SV2*0120**4000*DA*3");
        seg.Should().Contain("SVD*MCO123*2800*NU:0120**3");
        seg.Should().Contain("SV2*0324*HC:71046*1000*UN*1");
        seg.Should().Contain("SVD*MCO123*400*HC:71046*0324*1");
        seg.Should().Contain("DTP*472*RD8*20260928-20261001");
    }

    [Fact]
    public void ReplacementAndVoid_CarryTheStateReference()
    {
        var replacement = Professional() with { FrequencyCode = "7", OriginalStateReference = "TX-ICN-998877" };
        var seg = Segments(Encounter837Writer.Write(EncounterFormType.Professional, new[] { replacement }, Envelope()).Content);

        seg.Should().Contain("CLM*CLM-sub-1*250***11:B:7*Y*A*Y*Y");
        seg.Should().Contain("REF*F8*TX-ICN-998877");
    }

    [Fact]
    public void Delimiters_InData_CannotSplitSegments()
    {
        var hostile = Professional() with
        {
            Member = Member() with { LastName = "O*BRIEN~", FirstName = "A:B^C" }
        };
        var seg = Segments(Encounter837Writer.Write(EncounterFormType.Professional, new[] { hostile }, Envelope()).Content);

        seg.Should().Contain("NM1*IL*1*OBRIEN*ABC****MI*512345678");
    }

    [Fact]
    public void MixedForms_OrDental_AreRefused()
    {
        var mixed = () => Encounter837Writer.Write(EncounterFormType.Professional,
            new[] { Professional(), Institutional() }, Envelope());
        mixed.Should().Throw<ArgumentException>();

        var dental = () => Encounter837Writer.Write(EncounterFormType.Dental,
            new[] { Professional() with { Form = EncounterFormType.Dental } }, Envelope());
        dental.Should().Throw<NotSupportedException>();
    }
}
