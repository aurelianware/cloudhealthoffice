using ClaimsService.EDI.Inbound;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.EDI;

/// <summary>
/// D-05 / G-25 (docs/epa-gap-matrix.md): REF*G1 (prior authorization
/// number) at claim level (loop 2300) and line level (loop 2400) on an 837I
/// and an 837P. All data below is synthetic.
/// </summary>
public class X12837PriorAuthorizationTests
{
    // 837I: claim-level REF*G1 in 2300; line 2 carries its own REF*G1 in
    // 2400; line 1 has none (inherits the claim's). A 2330B REF*G1 (other
    // payer's prior authorization number) must not be taken as this
    // payer's.
    private const string Institutional =
        "ISA*00*          *00*          *ZZ*SUBMITTER01    *ZZ*CHOPAYER       *260301*1200*^*00501*000000201*0*T*:~" +
        "GS*HC*SUBMITTER01*CHOPAYER*20260301*1200*201*X*005010X223A2~" +
        "ST*837*0201*005010X223A2~" +
        "BHT*0019*00*PA-INST-0001*20260301*1200*CH~" +
        "NM1*41*2*SYNTHETIC GENERAL HOSPITAL*****46*SUBMITTER01~" +
        "NM1*40*2*CHO TEST PAYER*****46*CHOPAYER~" +
        "HL*1**20*1~" +
        "NM1*85*2*SYNTHETIC GENERAL HOSPITAL*****XX*1999999984~" +
        "N3*100 MAIN ST~" +
        "N4*ANYTOWN*TX*75001~" +
        "REF*EI*990000001~" +
        "HL*2*1*22*0~" +
        "SBR*P*18*GRP-0001******MC~" +
        "NM1*IL*1*TESTPATIENT*ALEX****MI*MBR-PA-0001~" +
        "DMG*D8*19600115*F~" +
        "NM1*PR*2*CHO TEST PAYER*****PI*PAYER01~" +
        "CLM*PA-INST-0001*31000.00***11:A:1**A*Y*Y~" +
        "DTP*434*RD8*20260210-20260214~" +
        "DTP*435*DT*202602100815~" +
        "CL1*3*1*01~" +
        "REF*G1*AUTH-CLAIM-0001~" +
        "HI*ABK:M1711~" +
        "SBR*S*18*GRP-OTHER******CI~" +
        "NM1*IL*1*TESTPATIENT*ALEX****MI*OTHER-MBR~" +
        "NM1*PR*2*OTHER PAYER*****PI*OTHER01~" +
        "REF*G1*OTHER-PAYER-AUTH~" +
        "LX*1~" +
        "SV2*0120**2000.00*DA*4~" +
        "DTP*472*RD8*20260210-20260214~" +
        "LX*2~" +
        "SV2*0360*HC:27447*29000.00*UN*1~" +
        "DTP*472*D8*20260210~" +
        "REF*G1*AUTH-LINE-0002~" +
        "SE*31*0201~" +
        "GE*1*201~" +
        "IEA*1*000000201~";

    // 837P: claim-level REF*G1 in 2300; line 1 overrides it in 2400.
    private const string Professional =
        "ISA*00*          *00*          *ZZ*SUBMITTER01    *ZZ*CHOPAYER       *260301*1200*^*00501*000000202*0*T*:~" +
        "GS*HC*SUBMITTER01*CHOPAYER*20260301*1200*202*X*005010X222A1~" +
        "ST*837*0202*005010X222A1~" +
        "BHT*0019*00*PA-PROF-0001*20260301*1200*CH~" +
        "NM1*41*2*SYNTHETIC CLINIC*****46*SUBMITTER01~" +
        "NM1*40*2*CHO TEST PAYER*****46*CHOPAYER~" +
        "HL*1**20*1~" +
        "NM1*85*2*SYNTHETIC CLINIC*****XX*1234567890~" +
        "N3*1 CLINIC WAY~" +
        "N4*ANYTOWN*TX*75001~" +
        "HL*2*1*22*0~" +
        "SBR*P*18*GRP-0002******MC~" +
        "NM1*IL*1*TESTPATIENT*SAM****MI*MBR-PA-0002~" +
        "NM1*PR*2*CHO TEST PAYER*****PI*PAYER01~" +
        "CLM*PA-PROF-0001*1250.00***11:B:1*Y*A*Y*Y~" +
        "REF*G1*AUTH-CLAIM-0002~" +
        "HI*ABK:M5416~" +
        "LX*1~" +
        "SV1*HC:72148*1000.00*UN*1*11**1~" +
        "DTP*472*D8*20260305~" +
        "REF*G1*AUTH-LINE-0001~" +
        "LX*2~" +
        "SV1*HC:99214*250.00*UN*1*11**1~" +
        "DTP*472*D8*20260305~" +
        "SE*22*0202~" +
        "GE*1*202~" +
        "IEA*1*000000202~";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ServiceQuantityUnit_SurvivesEdiNativeAndEngineRoundTrip(bool institutional)
    {
        var source = Assert.Single(X12837Parser.Parse(institutional ? Institutional : Professional));
        var native = X12837ClaimMapper.Map(source, "tenant-1");
        var expected = institutional ? "DA" : "UN";
        Assert.Equal(expected, native.ClaimLines[0].UnitType);
        var line = native.ClaimLines[0].ToClaimLine();
        var reloaded = global::ClaimsService.Models.AdapterClaimLine.From(line);
        Assert.Equal(expected, reloaded.UnitType);
        Assert.Equal(source.ServiceLines[0].Units, reloaded.Units);
        native.ClaimLines[0] = reloaded;
        var engine = global::ClaimsService.Services.Adjudication.Mapping.ClaimToX12837Mapper.Map(native, null);
        Assert.Equal(expected, engine.ServiceLines[0].UnitType);
        Assert.Null(reloaded.DrugUnitOfMeasure);
    }

    [Fact]
    public void Institutional_ParsesClaimAndLineLevelRefG1()
    {
        var claim = Assert.Single(X12837Parser.Parse(Institutional));

        Assert.Equal("AUTH-CLAIM-0001", claim.ClaimHeader.PriorAuthorizationNumber);
        Assert.Null(claim.ServiceLines[0].PriorAuthorizationNumber);
        Assert.Equal("AUTH-LINE-0002", claim.ServiceLines[1].PriorAuthorizationNumber);
    }

    [Fact]
    public void Professional_ParsesClaimAndLineLevelRefG1()
    {
        var claim = Assert.Single(X12837Parser.Parse(Professional));

        Assert.Equal("AUTH-CLAIM-0002", claim.ClaimHeader.PriorAuthorizationNumber);
        Assert.Equal("AUTH-LINE-0001", claim.ServiceLines[0].PriorAuthorizationNumber);
        Assert.Null(claim.ServiceLines[1].PriorAuthorizationNumber);
    }

    [Fact]
    public void Institutional_MapsClaimLevelRefG1OntoTheClaim()
    {
        var mapped = X12837ClaimMapper.Map(Assert.Single(X12837Parser.Parse(Institutional)), "tenant-1");

        Assert.Equal("AUTH-CLAIM-0001", mapped.PriorAuthorizationNumber);
    }

    [Fact]
    public void Professional_MapsClaimLevelRefG1OntoTheClaim()
    {
        var mapped = X12837ClaimMapper.Map(Assert.Single(X12837Parser.Parse(Professional)), "tenant-1");

        Assert.Equal("AUTH-CLAIM-0002", mapped.PriorAuthorizationNumber);
    }

    [Fact]
    public void Institutional_MapsLineLevelRefG1OntoTheLine()
    {
        var mapped = X12837ClaimMapper.Map(Assert.Single(X12837Parser.Parse(Institutional)), "tenant-1");

        Assert.Null(mapped.ClaimLines[0].PriorAuthorizationNumber);
        Assert.Equal("AUTH-LINE-0002", mapped.ClaimLines[1].PriorAuthorizationNumber);
    }

    [Fact]
    public void Professional_MapsLineLevelRefG1OntoTheLine()
    {
        var mapped = X12837ClaimMapper.Map(Assert.Single(X12837Parser.Parse(Professional)), "tenant-1");

        Assert.Equal("AUTH-LINE-0001", mapped.ClaimLines[0].PriorAuthorizationNumber);
        Assert.Null(mapped.ClaimLines[1].PriorAuthorizationNumber);
    }

    [Fact]
    public void LineLevelRefG1_SurvivesTheDomainRoundTrip()
    {
        var mapped = X12837ClaimMapper.Map(Assert.Single(X12837Parser.Parse(Professional)), "tenant-1");

        var roundTripped = global::ClaimsService.Models.AdapterClaim.From(mapped.ToClaim());

        Assert.Equal("AUTH-CLAIM-0002", roundTripped.PriorAuthorizationNumber);
        Assert.Equal("AUTH-LINE-0001", roundTripped.ClaimLines[0].PriorAuthorizationNumber);
    }

    /// <summary>
    /// Nit 4: pins the <c>!InOtherPayerLoop()</c> guard on the 2300 REF*G1
    /// case. A REF*G1 under 2330A (other subscriber, NM1*IL) is not caught by
    /// the 2330B handler; without the guard it would be read as this claim's.
    /// </summary>
    [Fact]
    public void RefG1Under2330A_IsNotThisPayersAuthorization()
    {
        var edi = Institutional
            .Replace("REF*G1*AUTH-CLAIM-0001~", string.Empty)
            .Replace("NM1*IL*1*TESTPATIENT*ALEX****MI*OTHER-MBR~",
                     "NM1*IL*1*TESTPATIENT*ALEX****MI*OTHER-MBR~REF*G1*OTHER-SUBSCRIBER-AUTH~");

        var claim = Assert.Single(X12837Parser.Parse(edi));

        Assert.Null(claim.ClaimHeader.PriorAuthorizationNumber);
    }

    [Fact]
    public void RefG1OnlyInOtherPayerLoop_IsNotThisPayersAuthorization()
    {
        var withoutOwnRefG1 = Institutional.Replace("REF*G1*AUTH-CLAIM-0001~", string.Empty);

        var claim = Assert.Single(X12837Parser.Parse(withoutOwnRefG1));

        Assert.Null(claim.ClaimHeader.PriorAuthorizationNumber);
    }
}
