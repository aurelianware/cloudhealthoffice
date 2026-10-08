using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Models;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// 005010X221A1 segment order of the 2100/2110 loops in both 835 generators
/// (CLP, CAS, NM1, MIA/MOA, DTM; SVC, DTM, CAS, LQ), and MIA for
/// institutional claims vs MOA for professional ones.
/// </summary>
public class Era835ClaimLoopsTests
{
    private static TradingPartnerInfo Partner() => new()
    {
        InterchangeSenderId = "S", InterchangeReceiverId = "R",
        ApplicationSenderId = "S", ApplicationReceiverId = "R",
        OriginatingCompanyId = "1123456789",
    };

    private static ClaimPayment Claim(bool institutional = false, params string[] remarks) => new()
    {
        ClaimId = "c1",
        PatientControlNumber = "CLM-1",
        ClaimStatusCode = "1",
        ChargeAmount = 300m,
        PaymentAmount = 150m,
        PatientResponsibilityAmount = 50m,
        MemberId = "M1",
        RenderingProviderNPI = "1999999999",
        ClaimReceivedDate = new DateTime(2026, 4, 1),
        IsInstitutional = institutional,
        RemarkCodes = remarks.ToList(),
        ClaimAdjustments = new List<ClaimAdjustment>
        {
            new() { GroupCode = "PR", ReasonCode = "1", Amount = 50m },
            new() { GroupCode = "CO", ReasonCode = "45", Amount = 100m },
        },
        ServiceLines = new List<ServiceLinePayment>
        {
            new()
            {
                LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 300m, PaymentAmount = 150m, Units = 1,
                ServiceDateFrom = new DateTime(2026, 3, 1),
                Adjustments = new List<ServiceLineAdjustment>
                {
                    new() { GroupCode = "CO", ReasonCode = "45", Amount = 100m, RemarkCode = "M15" },
                    new() { GroupCode = "PR", ReasonCode = "1", Amount = 50m },
                },
            },
        },
    };

    private static Payment PaymentOf(ClaimPayment cp) => new()
    {
        CheckNumber = "0001000001",
        PaymentMethod = "CHK",
        TotalPaymentAmount = cp.PaymentAmount,
        PaymentDate = new DateTime(2026, 4, 2),
        PayeeNPI = "1234567890",
        ClaimPayments = new List<ClaimPayment> { cp },
    };

    private static string Batch(ClaimPayment cp) =>
        new BatchEraGeneratorService(NullLogger<BatchEraGeneratorService>.Instance)
            .GenerateBatch(new[] { new EraPaymentInput { TradingPartnerId = "TP", Payment = PaymentOf(cp) } },
                new Dictionary<string, TradingPartnerInfo> { ["TP"] = Partner() })
            .Single().EdiContent;

    private static string Single(ClaimPayment cp) =>
        new EraGeneratorService(NullLogger<EraGeneratorService>.Instance).Generate835(PaymentOf(cp), Partner());

    public static TheoryData<string> Generators => new() { "batch", "single" };

    private static string Generate(string generator, ClaimPayment cp) => generator == "batch" ? Batch(cp) : Single(cp);

    /// <summary>Segment ids (with the qualifier for NM1/DTM/LQ) from CLP up to PLB/SE.</summary>
    private static List<string> LoopIds(string edi) =>
        edi.Split('~', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Split('*'))
            .SkipWhile(s => s[0] != "CLP")
            .TakeWhile(s => s[0] is not ("PLB" or "SE"))
            .Select(s => s[0] is "NM1" or "DTM" or "LQ" ? $"{s[0]}*{s[1]}" : s[0])
            .ToList();

    [Theory]
    [MemberData(nameof(Generators))]
    public void ClaimAndServiceLoops_FollowGuideOrder(string generator)
    {
        var ids = LoopIds(Generate(generator, Claim(false, "N130")));

        Assert.Equal(
            new[]
            {
                "CLP", "CAS", "CAS", "NM1*QC", "NM1*82", "MOA", "DTM*050",
                "SVC", "DTM*472", "CAS", "CAS", "LQ*HE",
            },
            ids);
    }

    [Theory]
    [MemberData(nameof(Generators))]
    public void Professional_RemarksInMoa03To07(string generator)
    {
        var segments = Generate(generator, Claim(false, "N1", "N2", "N3", "N4", "N5", "N6"))
            .Split('~').Select(s => s.Split('*')).ToList();

        Assert.Equal(new[] { "MOA", "", "", "N1", "N2", "N3", "N4", "N5" }, segments.Single(s => s[0] == "MOA"));
        Assert.DoesNotContain(segments, s => s[0] == "MIA");
    }

    [Theory]
    [MemberData(nameof(Generators))]
    public void Institutional_RemarksInMia05AndMia20To23(string generator)
    {
        var segments = Generate(generator, Claim(true, "MA01", "N2", "N3"))
            .Split('~').Select(s => s.Split('*')).ToList();

        var mia = segments.Single(s => s[0] == "MIA");
        Assert.Equal("0", mia[1]);       // MIA01 covered days (required)
        Assert.Equal("MA01", mia[5]);    // MIA05
        Assert.Equal("N2", mia[20]);     // MIA20
        Assert.Equal("N3", mia[21]);     // MIA21
        Assert.Equal(22, mia.Length);    // trailing empty elements trimmed
        Assert.All(mia.Skip(6).Take(14), e => Assert.Equal(string.Empty, e));
        Assert.DoesNotContain(segments, s => s[0] == "MOA");
    }

    [Fact]
    public void Institutional_FiveRemarks_FillMia05AndMia20To23()
    {
        var mia = Era835ClaimLoops.RemarkSegment(Claim(true, "A", "B", "C", "D", "E", "F"))!.Split('*');

        Assert.Equal(24, mia.Length);
        Assert.Equal(new[] { "A", "B", "C", "D", "E" }, new[] { mia[5], mia[20], mia[21], mia[22], mia[23] });
    }

    [Fact]
    public void NoRemarks_NoMiaOrMoa()
    {
        Assert.Null(Era835ClaimLoops.RemarkSegment(Claim(true)));
        Assert.Null(Era835ClaimLoops.RemarkSegment(Claim(false)));
    }

    [Theory]
    [MemberData(nameof(Generators))]
    public void LineRemark_IsLqHe_NotCas04(string generator)
    {
        var edi = Generate(generator, Claim());

        Assert.Contains("CAS*CO*45*100.00~", edi);
        Assert.Contains("LQ*HE*M15~", edi);
        Assert.DoesNotContain("*M15*", edi);
    }
}
