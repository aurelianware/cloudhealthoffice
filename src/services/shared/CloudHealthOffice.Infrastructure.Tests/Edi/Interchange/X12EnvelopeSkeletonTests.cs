using System.Security.Cryptography;
using System.Text;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using static CloudHealthOffice.Infrastructure.Tests.Edi.X12.X12Samples;

namespace CloudHealthOffice.Infrastructure.Tests.Edi.X12;

/// <summary>A streamed file's envelope skeleton reads to the same envelope decisions as the file itself.</summary>
public class X12EnvelopeSkeletonTests
{
    private static string Without(string content, string from) => content[..content.IndexOf(from, StringComparison.Ordinal)];

    private static readonly string[] Members =
    [
        "GS*BE*SPONSOR01*CHO*20260115*1200*7*X*005010X220A1",
        "ST*834*0001*005010X220A1",
        "BGN*00*REF1*20260115*1200****2",
        "INS*Y*18*021*28*A",
        "REF*0F*SUB1",
        "NM1*IL*1*DOE*JANE",
        "INS*N*19*021*28*A",
        "REF*0F*SUB1",
        "SE*8*0001",
        "ST*834*0002*005010X220A1",
        "INS*Y*18*021*28*A",
        "SE*3*0002",
        "GE*2*7",
    ];

    public static TheoryData<string, string> Files()
    {
        var data = new TheoryData<string, string>
        {
            { "valid", Interchange() },
            { "members, two transaction sets", Interchange(body: Members) },
            { "IEA02 mismatch", Interchange(iea: "IEA*1*000000999") },
            { "group count", Interchange(iea: "IEA*2*000000101") },
            { "GE without GS", Interchange(body: ["GE*1*101"], iea: "IEA*0*000000101") },
            { "segment outside a group", Interchange(body: ["ST*837*0001*005010X222A1", "SE*2*0001"], iea: "IEA*0*000000101") },
            { "GS missing elements", Interchange(body: ["GS*HC*SUBMITTER01", "ST*837*0001", "SE*2*0001", "GE*1*101"]) },
            { "no groups", Interchange(body: [], iea: "IEA*0*000000101") },
            { "missing IEA", Without(Interchange(), "IEA") },
            { "missing GE", Interchange(body: ["GS*HC*SUBMITTER01*CHO*20260115*1200*101*X*005010X222A1", "ST*837*0001", "SE*2*0001"]) },
            { "truncated ISA", "ISA*00*          *00*    " },
            { "alphanumeric separator", Interchange().Replace('*', 'Q') },
            { "newline terminator, pipe separator", Interchange().Replace('*', '|').Replace('~', '\n') },
            { "two interchanges", Interchange() + "\r\n" + Interchange(f => f[12] = "000000102") },
            { "new ISA before IEA", Without(Interchange(), "IEA") + Interchange(f => f[12] = "000000102") },
            { "content after IEA", Interchange() + "GS*HC*X~" },
            { "whitespace between segments", Interchange().Replace("~", "~\r\n") },
            { "leading whitespace", "\r\n  " + Interchange() },
            { "not X12", "GS*HC*X~" },
            { "empty", "" },
            { "test indicator", Interchange(f => f[14] = "T") },
            { "bad ISA field", Interchange(f => f[4] = "XX") },
            { "TA1 inside", Interchange(body: ["TA1*000000001*260115*1200*A*000", .. Body], iea: "IEA*1*000000101") },
            { "empty segment id", Interchange(body: ["GS*HC*SUBMITTER01*CHO*20260115*1200*101*X*005010X222A1", "*X*Y", "GE*1*101"]) },
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task TheSkeleton_ReadsToTheSameEnvelopeDecisions(string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var result = await X12EnvelopeSkeleton.BuildAsync(new MemoryStream(bytes));

        var expected = X12EnvelopeReader.Read(content);
        var actual = X12EnvelopeReader.Read(result.Skeleton);

        actual.UnreadableReason.Should().Be(expected.UnreadableReason, name);
        actual.Interchanges.Should().HaveCount(expected.Interchanges.Count, name);
        for (var i = 0; i < expected.Interchanges.Count; i++)
        {
            var (e, a) = (expected.Interchanges[i], actual.Interchanges[i]);
            a.Findings.Select(f => (f.NoteCode, f.Message)).Should().Equal(e.Findings.Select(f => (f.NoteCode, f.Message)), name);
            a.Header?.Elements.Should().Equal(e.Header?.Elements, name);
            a.GroupCount.Should().Be(e.GroupCount, name);
            a.TransactionSetIds.Should().Equal(e.TransactionSetIds, name);
            a.FunctionalIdentifierCodes.Should().Equal(e.FunctionalIdentifierCodes, name);
            a.HasTrailer.Should().Be(e.HasTrailer, name);
            a.HasTa1Segments.Should().Be(e.HasTa1Segments, name);
        }

        result.SizeBytes.Should().Be(bytes.Length);
        result.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Fact]
    public async Task TheSkeleton_KeepsGroupAndTransactionSetControlNumbers()
    {
        var result = await X12EnvelopeSkeleton.BuildAsync(new MemoryStream(Encoding.UTF8.GetBytes(Interchange(body: Members))));

        result.Skeleton.Should().Contain("GS*BE*SPONSOR01*CHO*20260115*1200*7*X*005010X220A1~");
        result.Skeleton.Should().Contain("ST*834*0001*005010X220A1~").And.Contain("ST*834*0002*005010X220A1~");
    }

    [Fact]
    public async Task ALargeFile_StaysSmall_AndIsNeverReadWhole()
    {
        // 50,000 members in one transaction set: the skeleton keeps a handful of segments.
        var body = new StringBuilder();
        body.Append("GS*BE*SPONSOR01*CHO*20260115*1200*7*X*005010X220A1~ST*834*0001*005010X220A1~BGN*00*R*20260115*1200****2~");
        for (var i = 0; i < 50_000; i++)
            body.Append("INS*Y*18*021*28*A~REF*0F*SUB").Append(i).Append("~NM1*IL*1*DOE*JANE~HD*021**HLT*PLAN1*EMP~");
        body.Append("SE*1*0001~GE*1*7~");
        var isa = Isa(IsaFields());
        var content = isa + body + "IEA*1*000000101~";

        await using var stream = new ForwardOnlyStream(Encoding.UTF8.GetBytes(content));
        var result = await X12EnvelopeSkeleton.BuildAsync(stream);

        result.Skeleton.Length.Should().BeLessThan(400);
        X12EnvelopeReader.Read(result.Skeleton).Interchanges.Single().Findings.Should().BeEmpty();
        stream.LargestRead.Should().BeLessThanOrEqualTo(64 * 1024);
    }

    /// <summary>A stream that cannot seek or report its length, and records its largest read.</summary>
    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public int LargestRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            LargestRead = Math.Max(LargestRead, count);
            return _inner.Read(buffer, offset, count);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
