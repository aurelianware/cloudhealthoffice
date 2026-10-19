using System.Globalization;
using ClaimsService.EDI.Inbound;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.EDI;

/// <summary>
/// X12 amounts always use '.' as the decimal mark. CLM02 and the SV1/SV2
/// charge and units were parsed with the thread's current culture, so on a
/// host whose culture uses '.' as the group separator (de-DE) "180.00" read
/// as 18000. Every amount now parses with the invariant culture, like the
/// rest of the parser. All data below is synthetic.
/// </summary>
public class X12837CultureTests
{
    private const string Professional =
        "ISA*00*          *00*          *ZZ*SUBMITTER01    *ZZ*CHOPAYER       *260301*1200*^*00501*000000101*0*T*:~" +
        "GS*HC*SUBMITTER01*CHOPAYER*20260301*1200*101*X*005010X222A1~" +
        "ST*837*0101*005010X222A1~" +
        "BHT*0019*00*BATCH-0001*20260301*1200*CH~" +
        "NM1*41*2*SYNTHETIC CLINIC*****46*SUBMITTER01~" +
        "NM1*40*2*CHO TEST PAYER*****46*CHOPAYER~" +
        "HL*1**20*1~" +
        "NM1*85*2*SYNTHETIC CLINIC*****XX*1999999984~" +
        "HL*2*1*22*0~" +
        "SBR*P*18*GRP-0001******CI~" +
        "NM1*IL*1*TESTPATIENT*ALEX****MI*MBR-0001~" +
        "CLM*CULT-0001*180.50***11:B:1*Y*A*Y*Y~" +
        "HI*ABK:J069~" +
        "LX*1~" +
        "SV1*HC:99213*180.50*UN*1.5*11**1~" +
        "DTP*472*D8*20260215~" +
        "SE*15*0101~GE*1*101~IEA*1*000000101~";

    private const string Institutional =
        "ISA*00*          *00*          *ZZ*SUBMITTER01    *ZZ*CHOPAYER       *260301*1200*^*00501*000000102*0*T*:~" +
        "GS*HC*SUBMITTER01*CHOPAYER*20260301*1200*102*X*005010X223A2~" +
        "ST*837*0102*005010X223A2~" +
        "BHT*0019*00*BATCH-0002*20260301*1200*CH~" +
        "NM1*41*2*SYNTHETIC HOSPITAL*****46*SUBMITTER01~" +
        "NM1*40*2*CHO TEST PAYER*****46*CHOPAYER~" +
        "HL*1**20*1~" +
        "NM1*85*2*SYNTHETIC HOSPITAL*****XX*1999999984~" +
        "HL*2*1*22*0~" +
        "SBR*P*18*GRP-0001******CI~" +
        "NM1*IL*1*TESTPATIENT*ALEX****MI*MBR-0002~" +
        "CLM*CULT-0002*1500.25***11:A:1**A*Y*Y~" +
        "HI*ABK:M1711~" +
        "LX*1~" +
        "SV2*0250**1500.25*UN*2.5~" +
        "SE*14*0102~GE*1*102~IEA*1*000000102~";

    private static T UnderCulture<T>(string name, Func<T> act)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try { return act(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void ProfessionalAmounts_ParseTheSame_WhateverTheHostCulture(string culture)
    {
        var claim = UnderCulture(culture, () => X12837Parser.Parse(Professional)).Single();

        Assert.Equal(180.50m, claim.TotalClaimedAmount);
        var line = Assert.Single(claim.ServiceLines);
        Assert.Equal((180.50m, 1.5m), (line.ChargeAmount, line.Units));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void InstitutionalAmounts_ParseTheSame_WhateverTheHostCulture(string culture)
    {
        var claim = UnderCulture(culture, () => X12837Parser.Parse(Institutional)).Single();

        Assert.Equal(1500.25m, claim.TotalClaimedAmount);
        var line = Assert.Single(claim.ServiceLines);
        Assert.Equal((1500.25m, 2.5m), (line.ChargeAmount, line.Units));
    }
}
