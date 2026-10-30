using EligibilityService.Models;
using EligibilityService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.Edi.Tests;

/// <summary>
/// CAQH CORE 271 data content: the 270's service types and trace numbers
/// carried through, the CORE service types answered for a generic inquiry,
/// and the patient's financial responsibility in the right EB elements.
/// </summary>
public class Core271ContentTests
{
    private static readonly Edi270Parser Parser = new(NullLogger<Edi270Parser>.Instance);
    private static readonly Edi271Generator Generator = new(NullLogger<Edi271Generator>.Instance);

    private static string Build270(string eq, string? trn = "TRN*1*93175-012547*9877281234~")
    {
        var isa = "ISA*00*          *00*          *ZZ*SUBMITTER      *ZZ*CHOPAYER       *261030*1200*^*00501*000000101*0*P*:~";
        return isa +
            "GS*HS*SUBMITTER*CHOPAYER*20261030*1200*101*X*005010X279A1~" +
            "ST*270*0001*005010X279A1~" +
            "BHT*0022*13*REF-270-77*20261030*1200~" +
            "HL*1**20*1~" +
            "NM1*PR*2*PAYER*****PI*PAY01~" +
            "HL*2*1*21*1~" +
            "NM1*1P*2*PROVIDER*****XX*1234567890~" +
            "HL*3*2*22*0~" +
            (trn ?? string.Empty) +
            "NM1*IL*1*DOE*JANE****MI*SUB123~" +
            "DMG*D8*19800115*F~" +
            eq +
            "SE*12*0001~" +
            "GE*1*101~" +
            "IEA*1*000000101~";
    }

    private static EligibilityResponse Covered(params EligibilityBenefit[] benefits) => new()
    {
        ControlNumber = "CTRL",
        IsCovered = true,
        CoverageLevel = "IND",
        InsurancePlanName = "Silver PPO",
        Deductible = new DeductibleInfo
        {
            IndividualDeductible = 1500m, IndividualDeductibleMet = 400m, IndividualDeductibleRemaining = 1100m,
        },
        OutOfPocket = new OutOfPocketInfo
        {
            IndividualOOPMax = 6000m, IndividualOOPMet = 6000m, IndividualOOPRemaining = 0m,
        },
        Benefits = benefits.ToList(),
    };

    private static string Generate(EligibilityInquiry inquiry, EligibilityResponse response) =>
        Generator.Generate(inquiry, response, isaSenderId: "CHOPAYER", isaReceiverId: "SUBMITTER");

    private static List<string> Segments(string edi) => edi.Split('~', StringSplitOptions.RemoveEmptyEntries).ToList();

    [Fact]
    public void Parser_ReadsEveryServiceType_FromRepeatsAndFromSeveralEqSegments()
    {
        var parsed = Parser.Parse(Build270("EQ*98^UC~EQ*33~EQ*98~"));

        Assert.Equal(["98", "UC", "33"], parsed.Inquiry.ServiceTypeCodes);
        Assert.Equal("98", parsed.Inquiry.ServiceTypeCode);
    }

    [Fact]
    public void Parser_ReadsTheBhtReference_AndTheSubscriberTraceNumbers()
    {
        var parsed = Parser.Parse(Build270("EQ*30~"));

        Assert.Equal("REF-270-77", parsed.Inquiry.ReferenceId);
        var trace = Assert.Single(parsed.Inquiry.TraceNumbers);
        Assert.Equal("93175-012547", trace.Number);
        Assert.Equal("9877281234", trace.Originator);
    }

    [Fact]
    public void Generator_EchoesTheBhtReference_AndReturnsTheSubmittersTraceAsTrn2()
    {
        var parsed = Parser.Parse(Build270("EQ*30~"));
        var edi = Generate(parsed.Inquiry, Covered());

        Assert.Contains("BHT*0022*11*REF-270-77*", edi);
        Assert.Contains("TRN*2*93175-012547*9877281234~", edi);
        Assert.DoesNotContain("TRN*1*", edi);
    }

    [Fact]
    public void Generator_WithoutATraceFromTheSubmitter_FallsBackToItsOwnTrn()
    {
        var parsed = Parser.Parse(Build270("EQ*30~", trn: null));
        var edi = Generate(parsed.Inquiry, Covered());

        Assert.Contains("TRN*2*CTRL*PAY01~", edi);
    }

    [Fact]
    public void GenericInquiry_AnswersTheCoreServiceTypesThePlanCovers_AndLeavesOutUnmappedCategories()
    {
        var parsed = Parser.Parse(Build270("EQ*30~"));
        var edi = Generate(parsed.Inquiry, Covered(
            new EligibilityBenefit { ServiceTypeCode = "Office Visit", MonetaryAmount = 30m },
            new EligibilityBenefit { ServiceTypeCode = "Urgent Care", MonetaryAmount = 75m },
            new EligibilityBenefit { ServiceTypeCode = "Emergency Room", Percentage = 20m },
            new EligibilityBenefit { ServiceTypeCode = "Acupuncture", MonetaryAmount = 10m }));

        var ebs = Segments(edi).Where(s => s.StartsWith("EB*1*")).ToList();
        Assert.Equal(["EB*1*IND*30**Silver PPO", "EB*1*IND*86", "EB*1*IND*98", "EB*1*IND*UC"], ebs);
        // A label is never sent in EB03.
        Assert.DoesNotContain("Acupuncture", edi);
        Assert.DoesNotContain("Office Visit", edi);
    }

    [Fact]
    public void SpecificInquiry_AnswersOnlyTheServiceTypesAskedFor()
    {
        var parsed = Parser.Parse(Build270("EQ*UC~"));
        var edi = Generate(parsed.Inquiry, Covered(
            new EligibilityBenefit { ServiceTypeCode = "Office Visit", MonetaryAmount = 30m },
            new EligibilityBenefit { ServiceTypeCode = "UC", MonetaryAmount = 75m }));

        Assert.Contains("EB*1*IND*UC~", edi);
        Assert.Contains("EB*B*IND*UC***27*75.00*****Y~", edi);
        Assert.DoesNotContain("EB*1*IND*98", edi);
    }

    [Fact]
    public void PatientFinancials_UseTheCoreElementPositions()
    {
        var parsed = Parser.Parse(Build270("EQ*98~"));
        var edi = Generate(parsed.Inquiry, Covered(
            new EligibilityBenefit { ServiceTypeCode = "98", MonetaryAmount = 30m, NetworkIndicator = "Y" },
            new EligibilityBenefit { ServiceTypeCode = "98", Percentage = 40m, NetworkIndicator = "N" },
            new EligibilityBenefit { ServiceTypeCode = "98", Quantity = 20m, AuthorizationRequired = "Y" }));

        // Deductible: plan amount, met year to date, remaining.
        Assert.Contains("EB*C*IND*30***23*1500.00*****Y~", edi);
        Assert.Contains("EB*C*IND*30***24*400.00*****Y~", edi);
        Assert.Contains("EB*C*IND*30***29*1100.00*****Y~", edi);
        // Out of pocket met in full: remaining is still sent, as zero.
        Assert.Contains("EB*G*IND*30***29*0.00*****Y~", edi);
        // Active coverage for the service type, prior authorization in EB11.
        Assert.Contains("EB*1*IND*98********Y~", edi);
        // Co-payment in EB07, co-insurance as a decimal in EB08, visits in EB09/EB10.
        Assert.Contains("EB*B*IND*98***27*30.00*****Y~", edi);
        Assert.Contains("EB*A*IND*98*****0.4****N~", edi);
        Assert.Contains("EB*F*IND*98***23***VS*20**Y~", edi);
    }

    [Fact]
    public void TheSeCount_StillMatches_WithManyServiceTypes()
    {
        var parsed = Parser.Parse(Build270("EQ*30~"));
        var edi = Generate(parsed.Inquiry, Covered(
            new EligibilityBenefit { ServiceTypeCode = "Office Visit", MonetaryAmount = 30m },
            new EligibilityBenefit { ServiceTypeCode = "Pharmacy", MonetaryAmount = 10m },
            new EligibilityBenefit { ServiceTypeCode = "Mental Health", Percentage = 20m }));

        var segments = Segments(edi);
        var st = segments.FindIndex(s => s.StartsWith("ST*"));
        var se = segments.FindIndex(s => s.StartsWith("SE*"));
        Assert.Equal(se - st + 1, int.Parse(segments[se].Split('*')[1]));
    }

    [Theory]
    [InlineData("Office Visit", "98")]
    [InlineData("urgent-care", "UC")]
    [InlineData("Behavioral Health", "MH")]
    [InlineData("Dental", "35")]
    [InlineData("al", "AL")]
    [InlineData("Acupuncture", null)]
    [InlineData("", null)]
    public void PlanCategories_MapToX12ServiceTypes(string category, string? expected)
    {
        Assert.Equal(expected, X12ServiceTypes.FromCategory(category));
    }
}
