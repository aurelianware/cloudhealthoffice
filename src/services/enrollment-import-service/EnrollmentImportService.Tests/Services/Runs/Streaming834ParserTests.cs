using System.Reflection;
using System.Text;
using System.Text.Json;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services.Edi;

namespace EnrollmentImportService.Tests.Services.Runs;

/// <summary>The streaming parser yields what the whole-file parser does, one unit at a time.</summary>
public class Streaming834ParserTests
{
    private static readonly string SampleEdi = (string)typeof(Tests.Services.Edi.Enrollment834EdiParserTests)
        .GetField("SampleEdi", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

    private const string Isa =
        "ISA*00*          *00*          *ZZ*SPONSOR123     *ZZ*PAYER456       *260206*1200*^*00501*000000001*0*P*:~";

    public static TheoryData<string, string> Files() => new()
    {
        { "sample: three families, LS/LE, 2300 loops", SampleEdi },
        { "eligibility dates", Isa + "ST*834*0001*005010X220A1~INS*Y*18*001*AI*A~REF*0F*SUB1~DTP*356*D8*20250101~DTP*357*D8*20261231~NM1*IL*1*DOE*JANE~SE*7*0001~" },
        { "standalone newborn", Isa + "ST*834*0001*005010X220A1~N1*P5*Acme Corporation*FI*123456789~INS*N*19*021*02*A~REF*0F*BSCA123456789~REF*1L*GRP0001~NM1*IL*1*SMITH*BABY~DMG*D8*20260115*F~HD*021**HLT*Blue Shield PPO*FAM~DTP*348*D8*20260115~SE*10*0001~" },
        { "sets do not share subscribers", Isa + "ST*834*0001*005010X220A1~N1*P5*First Sponsor*FI*111111111~INS*Y*18*021*28*A~REF*0F*SUB-A~NM1*IL*1*ALPHA*ANN~SE*5*0001~ST*834*0002*005010X220A1~N1*P5*Second Sponsor*FI*222222222~INS*N*01*021*28*A~REF*0F*SUB-A~NM1*IL*1*ALPHA*AL~SE*5*0002~" },
        { "legacy LS/LE member data", Isa + "ST*834*0001*005010X220A1~INS*Y*18*021*28*A~REF*0F*SUB1~NM1*IL*1*DOE*JANE~LS*2700~NM1*IL*1*DOE*KID~LE*2700~SE*8*0001~" },
        { "pipe and newline delimiters", Isa.Replace('*', '|').Replace('~', '\n') + "ST|834|0001\nINS|Y|18|021|28|A\nREF|0F|SUB1\nNM1|IL|1|DOE|JANE\nSE|5|0001\n" },
    };

    [Theory]
    [MemberData(nameof(Files))]
    public async Task TheUnits_AddUpToTheWholeFileParse(string name, string content)
    {
        var whole = new Enrollment834EdiParser().Parse(content, "f.834");
        var units = await Units(content);

        Json(units.Where(u => u.Kind == EnrollmentUnitKind.Subscriber).SelectMany(u => u.Batch.Enrollments))
            .Should().Be(Json(whole.Enrollments), name);
        Json(units.Where(u => u.Kind == EnrollmentUnitKind.StandaloneDependent).SelectMany(u => u.Batch.DependentEnrollments))
            .Should().Be(Json(whole.DependentEnrollments), name);
        units.SelectMany(u => u.Batch.ParseWarnings).Should().Equal(whole.ParseWarnings, name);
        units.Select(u => u.Sequence).Should().Equal(Enumerable.Range(0, units.Count), name);
    }

    [Fact]
    public async Task Units_CarryTheirFilePositions_AndSubscribersComeBeforeStandaloneDependents()
    {
        var content = Isa + "ST*834*0001~INS*N*19*021*02*A~REF*0F*SUB-X~NM1*IL*1*X*BABY~INS*Y*18*021*28*A~REF*0F*SUB1~NM1*IL*1*A~" +
                      "INS*Y*18*021*28*A~REF*0F*SUB2~NM1*IL*1*B~INS*N*01*021*28*A~REF*0F*SUB2~NM1*IL*1*DOE*B2~SE*1*0001~";

        var units = await Units(content);

        units.Select(u => (u.FilePosition, u.OrderingKey, u.Kind)).Should().Equal(
            ("INS 1", "SUB1", EnrollmentUnitKind.Subscriber),
            ("INS 2", "SUB2", EnrollmentUnitKind.Subscriber),
            ("dependent 1 without its subscriber", "SUB-X", EnrollmentUnitKind.StandaloneDependent));
        units[1].Batch.Enrollments.Single().Dependents.Should().ContainSingle(d => d.FirstName == "B2");
        units[1].Batch.SubscriberIndexOffset.Should().Be(1);
    }

    [Fact]
    public async Task ADependentAfterAnotherSubscribersLoop_IsAStandaloneDependent_TheOneDifference()
    {
        // The whole-file parser attaches AL to SUB-A; streamed, SUB-A's unit is already gone.
        var content = Isa + "ST*834*0001*005010X220A1~INS*Y*18*021*28*A~REF*0F*SUB-A~NM1*IL*1*ALPHA*ANN~INS*Y*18*021*28*A~REF*0F*SUB-B~NM1*IL*1*BETA*BOB~INS*N*01*021*28*A~REF*0F*SUB-A~NM1*IL*1*ALPHA*AL~SE*11*0001~";

        var units = await Units(content);

        units.Should().HaveCount(3);
        units[2].Kind.Should().Be(EnrollmentUnitKind.StandaloneDependent);
        units[2].Batch.DependentEnrollments.Single().FirstName.Should().Be("AL");
        units[2].OrderingKey.Should().Be("SUB-A", "it applies after SUB-A, on SUB-A's partition");
    }

    [Fact]
    public async Task RejectedInterchanges_AreSkipped()
    {
        var first = Isa + "ST*834*0001~INS*Y*18*021*28*A~REF*0F*SUB1~NM1*IL*1*A~SE*1*0001~IEA*1*000000001~";
        var second = Isa.Replace("000000001", "000000002") + "ST*834*0001~INS*Y*18*021*28*A~REF*0F*SUB2~NM1*IL*1*B~SE*1*0001~IEA*1*000000002~";

        var units = await Units(first + second, accepted: [1]);

        units.Should().ContainSingle().Which.OrderingKey.Should().Be("SUB2");
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("GS*BE*X~", "does not start with an ISA")]
    [InlineData("ISA*00*short~", "shorter than")]
    public async Task ContentThatIsNotX12_Throws(string content, string message)
    {
        var act = () => Units(content);
        (await act.Should().ThrowAsync<X12FormatException>()).Which.Message.Should().Contain(message);
    }

    [Fact]
    public async Task ALargeFile_IsParsedAsItIsRead_NotLoadedWhole()
    {
        const int members = 50_000;
        var body = new StringBuilder(Isa).Append("ST*834*0001*005010X220A1~N1*P5*Acme*FI*1~");
        for (var i = 0; i < members; i++)
            body.Append("INS*Y*18*021*28*A~REF*0F*SUB").Append(i).Append("~REF*1L*GRP1~NM1*IL*1*DOE*JANE~HD*021**HLT*PLAN1*EMP~DTP*348*D8*20260101~")
                .Append("INS*N*19*021*28*A~REF*0F*SUB").Append(i).Append("~NM1*IL*1*DOE*KID~");
        body.Append("SE*1*0001~");
        var bytes = Encoding.UTF8.GetBytes(body.ToString());

        await using var stream = new MeteredStream(bytes);
        long readAtFirstUnit = -1;
        var count = 0;
        await foreach (var unit in new Streaming834Parser().ParseAsync(stream, "big.834"))
        {
            if (count++ == 0) readAtFirstUnit = stream.BytesRead;
            unit.Batch.Enrollments.Single().Dependents.Should().ContainSingle();
        }

        count.Should().Be(members);
        readAtFirstUnit.Should().BeLessThan(256 * 1024, $"the first member is applied long before the {bytes.Length:N0}-byte file is read");
        stream.LargestRead.Should().BeLessThanOrEqualTo(64 * 1024);
    }

    private static async Task<List<ParsedEnrollmentUnit>> Units(string content, IReadOnlyCollection<int>? accepted = null)
    {
        var list = new List<ParsedEnrollmentUnit>();
        await foreach (var unit in new Streaming834Parser().ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)), "f.834", accepted))
            list.Add(unit);
        return list;
    }

    private static string Json<T>(IEnumerable<T> value) => JsonSerializer.Serialize(value.ToList());

    /// <summary>Forward-only; counts bytes read and the largest single read.</summary>
    private sealed class MeteredStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public long BytesRead { get; private set; }
        public int LargestRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            LargestRead = Math.Max(LargestRead, count);
            var n = _inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
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
