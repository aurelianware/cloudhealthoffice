using ClaimsService.EDI.Inbound;
using ClaimsService.EDI.Validation;
using Xunit;
using static CloudHealthOffice.ClaimsService.Tests.EDI.Snip.Snip837Samples;

namespace CloudHealthOffice.ClaimsService.Tests.EDI.Snip;

public class X12999AcknowledgmentBuilderTests
{
    private static readonly X12999AcknowledgmentBuilder.Options Fixed = new()
    {
        ControlNumber = 42,
        Timestamp = new DateTime(2026, 1, 15, 13, 5, 0, DateTimeKind.Utc),
    };

    private static (string Edi, List<X12Segment> Segments) Build(string inbound, Snip837ValidationOptions? options = null)
    {
        var result = new X12837SnipValidator(options).Validate(inbound);
        var edi = X12999AcknowledgmentBuilder.Build(result, Fixed)!;
        return (edi, X12Tokenizer.Tokenize(edi).Segments.ToList());
    }

    private static string Seg(List<X12Segment> segments, string id) =>
        id + "*" + string.Join('*', segments.First(s => s.Id == id).Elements);

    [Fact]
    public void Accepted_Produces999WithIk5AAndAk9A()
    {
        var (_, segs) = Build(Professional());

        Assert.Equal("ISA*00*          *00*          *ZZ*CHO            *ZZ*SUB001         *260115*1305*^*00501*000000042*0*T*:", Seg(segs, "ISA"));
        Assert.Equal("GS*FA*CHO*SUB001*20260115*1305*42*X*005010X231A1", Seg(segs, "GS"));
        Assert.Equal("ST*999*0001*005010X231A1", Seg(segs, "ST"));
        Assert.Equal("AK1*HC*101*005010X222A1", Seg(segs, "AK1"));
        Assert.Equal("AK2*837*0001*005010X222A1", Seg(segs, "AK2"));
        Assert.DoesNotContain(segs, s => s.Id is "IK3" or "IK4");
        Assert.Equal("IK5*A", Seg(segs, "IK5"));
        Assert.Equal("AK9*A*1*1*1", Seg(segs, "AK9"));
        Assert.Equal("IEA*1*000000042", Seg(segs, "IEA"));
    }

    [Fact]
    public void SeCountIsCorrect()
    {
        var (_, segs) = Build(Professional(b => b.Replace("CLM*", "CLM*PCN0001*175.00***11:B:1*Y*A*Y*Y")));
        var st = segs.FindIndex(s => s.Id == "ST");
        var se = segs.FindIndex(s => s.Id == "SE");
        Assert.Equal((se - st + 1).ToString(), segs[se].Elements[0]);
        Assert.Equal("0001", segs[se].Elements[1]);
    }

    [Fact]
    public void BalancingError_RejectedWithIk3Ik4AndClaimContext()
    {
        var (_, segs) = Build(Professional(b => b.Replace("CLM*", "CLM*PCN0001*175.00***11:B:1*Y*A*Y*Y")));

        Assert.Equal("IK3*CLM*16*2300*8", Seg(segs, "IK3"));
        Assert.Equal("CTX*CLM01:PCN0001", Seg(segs, "CTX"));
        Assert.Equal("IK4*2*782*I12", Seg(segs, "IK4"));
        Assert.Equal("IK5*R*I5", Seg(segs, "IK5"));
        Assert.Equal("AK9*R*1*1*0", Seg(segs, "AK9"));
    }

    [Fact]
    public void WarnLevelIssue_AcceptedWithErrors()
    {
        // Level 5 warns by default: the decimal-point ICD code is reported, the set is accepted.
        var (_, segs) = Build(Professional(b => b.Replace("HI*", "HI*ABK:J06.9*ABF:R05")));

        Assert.Equal("IK3*HI*17*2300*8", Seg(segs, "IK3"));
        Assert.Equal("IK4*1:2*1271*7*J06.9", Seg(segs, "IK4"));
        Assert.Equal("IK5*E*I5", Seg(segs, "IK5"));
        Assert.Equal("AK9*E*1*1*1", Seg(segs, "AK9"));
    }

    [Fact]
    public void SyntaxError_UsesSyntaxCodes()
    {
        var (_, segs) = Build(Professional().Replace("SE*24*0001", "SE*20*0001"));
        Assert.Equal("IK3*SE*24**8", Seg(segs, "IK3"));
        Assert.Equal("IK4*1*96*7", Seg(segs, "IK4"));
        Assert.Equal("IK5*R*4*5", Seg(segs, "IK5"));
    }

    [Fact]
    public void OneOfTwoTransactionSetsRejected_PartiallyAccepted()
    {
        var bad = ProfessionalBody("PCN0002");
        bad.Replace("CLM*", "CLM*PCN0002*999.00***11:B:1*Y*A*Y*Y");
        var (_, segs) = Build(Wrap(ProfessionalVersion, ProfessionalBody(), bad));

        Assert.Equal(["0001", "0002"], segs.Where(s => s.Id == "AK2").Select(s => s.Elements[1]));
        Assert.Equal(["A", "R"], segs.Where(s => s.Id == "IK5").Select(s => s.Elements[0]));
        Assert.Equal("AK9*P*2*2*1", Seg(segs, "AK9"));
    }

    [Fact]
    public void MissingSegment_ReportsRequiredSegmentMissing()
    {
        var (_, segs) = Build(Professional(b => b.RemoveSegment("REF*EI")));
        var ik3 = segs.First(s => s.Id == "IK3");
        // Reported at the position where the missing segment was expected (after NM1*85 at 7).
        Assert.Equal(["REF", "8", "2010AA", "3"], ik3.Elements);
        Assert.DoesNotContain(segs, s => s.Id == "IK4");
    }

    [Fact]
    public void UnreadableFile_HasNo999()
    {
        var result = new X12837SnipValidator().Validate("NOT AN 837 FILE");
        Assert.Null(X12999AcknowledgmentBuilder.Build(result, Fixed));
    }

    [Fact]
    public void EchoedValuesCannotBreakDelimiters()
    {
        var (edi, segs) = Build(Professional(b => b.Replace("BHT*", "BHT*00^9*00*BATCH0001*20260115*1200*CH")));
        Assert.DoesNotContain("00^9", edi);
        Assert.Equal("IK4*1*1005*7*00 9", Seg(segs, "IK4"));
    }
}
