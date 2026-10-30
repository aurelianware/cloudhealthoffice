using ClaimsService.EDI.Inbound;
using ClaimsService.Models;
using ClaimsService.Services.Adjudication.Mapping;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.EDI;

/// <summary>
/// 837D (005010X224A2) service lines: SV3 procedure, charge, oral cavity,
/// prosthesis code, quantity and diagnosis pointers, plus the TOO tooth and
/// surface segments that follow it. All data below is synthetic.
/// </summary>
public class X12837DentalTests
{
    // Line 1: a crown on tooth 19 (replacement) with REF*G1.
    // Line 2: a two-surface composite on tooth 14 and a one-surface one on 15.
    // Line 3: full-mouth debridement billed to the maxillary arch, no TOO.
    internal const string Dental =
        "ISA*00*          *00*          *ZZ*SUBMITTER01    *ZZ*CHOPAYER       *260301*1200*^*00501*000000301*0*T*:~" +
        "GS*HC*SUBMITTER01*CHOPAYER*20260301*1200*301*X*005010X224A2~" +
        "ST*837*0301*005010X224A2~" +
        "BHT*0019*00*DEN-0001*20260301*1200*CH~" +
        "NM1*41*2*SYNTHETIC DENTAL GROUP*****46*SUBMITTER01~" +
        "PER*IC*BILLING OFFICE*TE*5555550100~" +
        "NM1*40*2*CHO TEST DENTAL PLAN*****46*CHOPAYER~" +
        "HL*1**20*1~" +
        "NM1*85*2*SYNTHETIC DENTAL GROUP*****XX*1234567893~" +
        "N3*1 SMILE WAY~" +
        "N4*ANYTOWN*TX*75001~" +
        "REF*EI*990000002~" +
        "HL*2*1*22*0~" +
        "SBR*P*18*GRP-DEN-01******MC~" +
        "NM1*IL*1*TESTPATIENT*JORDAN****MI*MBR-DEN-0001~" +
        "DMG*D8*20120604*F~" +
        "NM1*PR*2*CHO TEST DENTAL PLAN*****PI*DENPAYER01~" +
        "CLM*DEN-0001*1585.00***11:B:1*Y*A*Y*Y~" +
        "HI*ABK:K0262~" +
        "LX*1~" +
        "SV3*AD:D2740*1200.00*11**R*1*****1~" +
        "TOO*JP*19~" +
        "DTP*472*D8*20260226~" +
        "REF*G1*AUTH-DEN-0001~" +
        "LX*2~" +
        "SV3*AD:D2392*260.00****2~" +
        "TOO*JP*14*M:O~" +
        "TOO*JP*15*O~" +
        "DTP*472*D8*20260226~" +
        "LX*3~" +
        "SV3*AD:D4355*125.00**01~" +
        "DTP*472*D8*20260226~" +
        "SE*31*0301~" +
        "GE*1*301~" +
        "IEA*1*000000301~";

    [Fact]
    public void Sv3_ParsesEveryDentalServiceLine()
    {
        var claim = Assert.Single(X12837Parser.Parse(Dental));

        Assert.Equal(CloudHealthOffice.ClaimsScrubEngine.Models.ClaimType.Dental, claim.ClaimType);
        Assert.Equal(3, claim.ServiceLines.Count);
        Assert.Equal(["D2740", "D2392", "D4355"], claim.ServiceLines.Select(l => l.ProcedureCode));
        Assert.All(claim.ServiceLines, l => Assert.Equal("AD", l.ProcedureCodeQualifier));
        Assert.Equal(1585.00m, claim.ServiceLines.Sum(l => l.ChargeAmount));
    }

    [Fact]
    public void Sv3_ReadsPlaceOfServiceProsthesisQuantityAndPointers()
    {
        var lines = Assert.Single(X12837Parser.Parse(Dental)).ServiceLines;

        Assert.Equal("11", lines[0].PlaceOfService);
        Assert.Equal("R", lines[0].ProsthesisCrownInlayCode);
        Assert.Equal(1m, lines[0].Units);
        Assert.Equal([1], lines[0].DiagnosisPointers!);
        Assert.Equal("AUTH-DEN-0001", lines[0].PriorAuthorizationNumber);
        Assert.Equal("20260226", lines[0].ServiceDate);

        Assert.Null(lines[1].PlaceOfService);
        Assert.Equal(2m, lines[1].Units);
        Assert.Null(lines[1].DiagnosisPointers);

        // SV306 absent: one procedure.
        Assert.Equal(1m, lines[2].Units);
    }

    [Fact]
    public void Too_AttachesEachToothAndItsSurfacesToItsLine()
    {
        var lines = Assert.Single(X12837Parser.Parse(Dental)).ServiceLines;

        var crown = Assert.Single(lines[0].Teeth!);
        Assert.Equal("19", crown.ToothCode);
        Assert.Null(crown.Surfaces);

        Assert.Equal(2, lines[1].Teeth!.Count);
        Assert.Equal("14", lines[1].Teeth![0].ToothCode);
        Assert.Equal(["M", "O"], lines[1].Teeth![0].Surfaces!);
        Assert.Equal("15", lines[1].Teeth![1].ToothCode);
        Assert.Equal(["O"], lines[1].Teeth![1].Surfaces!);

        Assert.Null(lines[2].Teeth);
        Assert.Equal(["01"], lines[2].OralCavityDesignations!);
    }

    [Fact]
    public void DentalDetail_SurvivesTheMapperAndTheDomainRoundTrip()
    {
        var mapped = X12837ClaimMapper.Map(Assert.Single(X12837Parser.Parse(Dental)), "tenant-1");

        Assert.Equal(ClaimType.Dental, mapped.ClaimType);
        Assert.Equal("R", mapped.ClaimLines[0].ProsthesisCrownInlayCode);

        var roundTripped = AdapterClaim.From(mapped.ToClaim());

        var filling = roundTripped.ClaimLines[1];
        Assert.Equal(["14", "15"], filling.Teeth.Select(t => t.ToothCode));
        Assert.Equal(["M", "O"], filling.Teeth[0].Surfaces);
        Assert.Equal(["01"], roundTripped.ClaimLines[2].OralCavityDesignations);
        Assert.Equal("R", roundTripped.ClaimLines[0].ProsthesisCrownInlayCode);
    }

    [Fact]
    public void DentalLines_ReachTheScrubEngineAsCdtWithTeeth()
    {
        var mapped = X12837ClaimMapper.Map(Assert.Single(X12837Parser.Parse(Dental)), "tenant-1");

        var engine = ClaimToX12837Mapper.Map(mapped, null);

        Assert.All(engine.ServiceLines, l => Assert.Equal("AD", l.ProcedureCodeQualifier));
        Assert.Equal(["14", "15"], engine.ServiceLines[1].Teeth!.Select(t => t.ToothCode));
        Assert.Equal(["01"], engine.ServiceLines[2].OralCavityDesignations!);
    }

    [Fact]
    public void ProfessionalLines_KeepTheHcQualifierAndNoTeeth()
    {
        var professional = Dental
            .Replace("005010X224A2", "005010X222A1")
            .Replace("SV3*AD:D2740*1200.00*11**R*1*****1~TOO*JP*19~", "SV1*HC:99213*1200.00*UN*1*11**1~")
            .Replace("SV3*AD:D2392*260.00****2~TOO*JP*14*M:O~TOO*JP*15*O~", "SV1*HC:99212*260.00*UN*2*11**1~")
            .Replace("SV3*AD:D4355*125.00**01~", "SV1*HC:99211*125.00*UN*1*11**1~");
        var mapped = X12837ClaimMapper.Map(Assert.Single(X12837Parser.Parse(professional)), "tenant-1");

        var engine = ClaimToX12837Mapper.Map(mapped, null);

        Assert.All(engine.ServiceLines, l => Assert.Equal("HC", l.ProcedureCodeQualifier));
        Assert.All(engine.ServiceLines, l => Assert.Null(l.Teeth));
        Assert.All(mapped.ClaimLines, l => Assert.Empty(l.Teeth));
    }
}
