using CloudHealthOffice.Infrastructure.Edi.Interchange;
using static CloudHealthOffice.Infrastructure.Tests.Edi.X12.X12Samples;

namespace CloudHealthOffice.Infrastructure.Tests.Edi.X12;

public class X12EnvelopeReaderTests
{
    private static Ta1Decision Decide(string content, X12EnvelopeValidationOptions? options = null)
    {
        var read = X12EnvelopeReader.Read(content, options);
        read.IsReadable.Should().BeTrue();
        return Ta1Decision.For(read.Interchanges.Single().Findings, []);
    }

    [Fact]
    public void ValidInterchange_HasNoFindings_AndIsAccepted()
    {
        var read = X12EnvelopeReader.Read(Interchange());

        var env = read.Interchanges.Should().ContainSingle().Subject;
        env.Findings.Should().BeEmpty();
        env.Header!.ControlNumber.Should().Be("000000101");
        env.Header.SenderKey.Should().Be("ZZ:SUBMITTER01");
        env.GroupCount.Should().Be(1);
        env.TransactionSetIds.Should().Equal("837");
        Ta1Decision.For(env.Findings, []).Should().Be(new Ta1Decision("A", "000"));
    }

    public static TheoryData<string, string> IsaFieldCases() => new()
    {
        // TA105, ISA element (1-based) = value
        { "002", "12=00401|11=^" },     // 4010: ISA11 must be the standards identifier U
        { "003", "12=00601" },          // a version this system does not support
        { "005", "5=XX" },
        { "006", "6=" + new string(' ', 15) },
        { "006", "6=SUBMITTER01   " },  // 14 characters: wrong fixed width
        { "007", "7=QQ" },
        { "008", "8=" + new string(' ', 15) },
        { "010", "1=99" },
        { "011", "2=SECRET    " },      // ISA01 = 00 means no authorization information
        { "012", "3=99" },
        { "013", "4=PASSWORD  " },      // ISA03 = 00 means no security information
        { "014", "9=261340" },          // month 13
        { "015", "10=2561" },           // hour 25
        { "016", "11=A" },              // 5010 repetition separator must not be alphanumeric
        { "017", "12=0050A" },
        { "018", "13=00000012A" },
        { "019", "14=2" },
        { "020", "15=X" },
        { "027", "16=*" },              // component separator equal to the element separator
    };

    [Theory]
    [MemberData(nameof(IsaFieldCases))]
    public void IsaFieldErrors_MapToTheirNoteCode_AndReject(string noteCode, string edits)
    {
        var content = Interchange(f =>
        {
            foreach (var edit in edits.Split('|'))
            {
                var parts = edit.Split('=', 2);
                f[int.Parse(parts[0]) - 1] = parts[1];
            }
        });
        if (noteCode == "018")
            content = content.Replace("IEA*1*000000101", "IEA*1*00000012A");

        Decide(content).Should().Be(new Ta1Decision("R", noteCode));
    }

    [Fact]
    public void ControlNumberMismatch_Is001()
    {
        Decide(Interchange(iea: "IEA*1*000000999")).Should().Be(new Ta1Decision("R", "001"));
    }

    [Fact]
    public void InvalidSegmentTerminator_Is004()
    {
        // The character after ISA16 is the segment terminator: a letter is not allowed.
        var content = Interchange().Replace("*:~GS", "*:XGS");
        Decide(content).NoteCode.Should().Be("004");
    }

    [Fact]
    public void UnknownReceiver_Is009_WhenReceiversAreConfigured()
    {
        var options = new X12EnvelopeValidationOptions { KnownReceiverIds = ["PAYER01"] };
        Decide(Interchange(), options).Should().Be(new Ta1Decision("R", "009"));
        Decide(Interchange(f => f[7] = Field("payer01", 15)), options).AckCode.Should().Be("A");
    }

    [Fact]
    public void GroupCountMismatch_Is021()
    {
        Decide(Interchange(iea: "IEA*2*000000101")).Should().Be(new Ta1Decision("R", "021"));
        Decide(Interchange(iea: "IEA*X*000000101")).NoteCode.Should().Be("021");
    }

    [Fact]
    public void GeWithoutGs_Is022()
    {
        string[] body = ["GE*1*101"];
        Decide(Interchange(body: body, iea: "IEA*0*000000101")).NoteCode.Should().Be("022");
    }

    [Fact]
    public void SegmentOutsideAGroup_Is022()
    {
        string[] body = ["ST*837*0001*005010X222A1", "SE*2*0001"];
        Decide(Interchange(body: body, iea: "IEA*0*000000101")).NoteCode.Should().Be("022");
    }

    [Fact]
    public void MissingIea_IsPrematureEndOfFile_023()
    {
        var content = Interchange();
        content = content[..content.IndexOf("IEA", StringComparison.Ordinal)];
        Decide(content).Should().Be(new Ta1Decision("R", "023"));
    }

    [Fact]
    public void TruncatedIsa_IsPrematureEndOfFile_WithNoHeader()
    {
        var read = X12EnvelopeReader.Read("ISA*00*          *00*    ");
        var env = read.Interchanges.Should().ContainSingle().Subject;
        env.Header.Should().BeNull();
        env.Findings[0].NoteCode.Should().Be("023");
    }

    [Fact]
    public void GsMissingElements_Is024()
    {
        string[] body = ["GS*HC*SUBMITTER01", "ST*837*0001", "SE*2*0001", "GE*1*101"];
        Decide(Interchange(body: body)).NoteCode.Should().Be("024");
    }

    [Fact]
    public void NoFunctionalGroups_Is024()
    {
        Decide(Interchange(body: [], iea: "IEA*0*000000101")).NoteCode.Should().Be("024");
    }

    [Fact]
    public void AlphanumericElementSeparator_Is026()
    {
        var content = Interchange().Replace('*', 'Q');
        Decide(content).NoteCode.Should().Be("026");
    }

    [Fact]
    public void NotedCodes_AreAcceptedWithErrors()
    {
        var env = X12EnvelopeReader.Read(Interchange(iea: "IEA*2*000000101")).Interchanges.Single();
        Ta1Decision.For(env.Findings, ["021"]).Should().Be(new Ta1Decision("E", "021"));
    }

    [Fact]
    public void DelimitersAreTakenFromTheIsa()
    {
        var content = Interchange().Replace('*', '|').Replace('~', '\n');
        var read = X12EnvelopeReader.Read(content);
        var env = read.Interchanges.Single();
        env.Findings.Should().BeEmpty();
        env.Header!.ElementSeparator.Should().Be('|');
        env.Header.SegmentTerminator.Should().Be('\n');
    }

    [Fact]
    public void SeveralInterchanges_AreReadSeparately()
    {
        var second = Interchange(f => f[12] = "000000102");
        var content = Interchange() + "\r\n" + second;

        var read = X12EnvelopeReader.Read(content);

        read.Interchanges.Select(i => i.ControlNumber).Should().Equal("000000101", "000000102");
        read.Interchanges[1].RawText.Should().Be(second);
        read.Interchanges.Should().OnlyContain(i => i.Findings.Count == 0);
    }

    [Fact]
    public void NewIsaBeforeIea_ClosesTheFirstInterchangeAsPremature()
    {
        var first = Interchange();
        first = first[..first.IndexOf("IEA", StringComparison.Ordinal)];
        var read = X12EnvelopeReader.Read(first + Interchange(f => f[12] = "000000102"));

        read.Interchanges.Should().HaveCount(2);
        read.Interchanges[0].Findings.Select(f => f.NoteCode).Should().Contain("023");
        read.Interchanges[1].Findings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("GS*HC*X~")]
    public void ContentWithoutIsa_IsUnreadable(string content)
    {
        var read = X12EnvelopeReader.Read(content);
        read.IsReadable.Should().BeFalse();
        read.UnreadableReason.Should().NotBeNull();
    }

    [Fact]
    public void EveryNoteCode_HasADescription()
    {
        Ta1NoteCodes.All.Should().HaveCount(32);
        Enumerable.Range(0, 32).Select(i => i.ToString("D3"))
            .Should().OnlyContain(code => Ta1NoteCodes.Describe(code) != null);
    }
}
