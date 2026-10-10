using CloudHealthOffice.Infrastructure.Edi.Interchange;
using static CloudHealthOffice.Infrastructure.Tests.Edi.X12.X12Samples;

namespace CloudHealthOffice.Infrastructure.Tests.Edi.X12;

public class Ta1BuilderParserTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 14, 30, 0, DateTimeKind.Utc);

    private static string BuildFor(string content, Ta1Decision? decision = null)
    {
        var env = X12EnvelopeReader.Read(content).Interchanges.Single();
        return Ta1Builder.Build(env, decision ?? Ta1Decision.For(env.Findings, []), new Ta1Builder.Options { ControlNumber = 42, Now = Now });
    }

    [Fact]
    public void Ta1_GoesBackToTheSender_WithTheReceivedControlNumberDateAndTime()
    {
        var ta1 = BuildFor(Interchange(iea: "IEA*1*000000999"));

        ta1.Should().Be(
            "ISA*00*          *00*          *ZZ*CHO            *ZZ*SUBMITTER01    *261010*1430*^*00501*000000042*0*T*:~" +
            "TA1*000000101*260115*1200*R*001~" +
            "IEA*0*000000042~");
    }

    [Fact]
    public void Ta1Interchange_IsItselfAValidEnvelope()
    {
        var ta1 = BuildFor(Interchange());
        var env = X12EnvelopeReader.Read(ta1).Interchanges.Single();

        env.Findings.Should().BeEmpty();
        env.HasTa1Segments.Should().BeTrue();
        env.GroupCount.Should().Be(0);
        env.Header!.Elements.Select(e => e.Length).Should().Equal(2, 10, 2, 10, 2, 15, 2, 15, 6, 4, 1, 5, 9, 1, 1, 1);
    }

    [Fact]
    public void RoundTrip_ParserReadsWhatTheBuilderWrote()
    {
        var ta1 = BuildFor(Interchange(), new Ta1Decision("E", "021"));

        var parsed = Ta1Parser.Parse(ta1).Single();

        parsed.Header.SenderId.Should().Be("CHO");
        parsed.Header.ReceiverId.Should().Be("SUBMITTER01");
        var ack = parsed.Acknowledgments.Single();
        ack.Should().Be(new ParsedTa1("000000101", "260115", "1200", "E", "021"));
        ack.NoteDescription.Should().Be(Ta1NoteCodes.Describe("021"));
        parsed.RawText.Should().Be(ta1);
    }

    [Fact]
    public void ReceivedDelimiters_AreEchoed()
    {
        var content = Interchange().Replace('*', '|');
        var ta1 = BuildFor(content);
        ta1.Should().StartWith("ISA|00|").And.Contain("TA1|000000101|260115|1200|A|000~");
    }

    [Fact]
    public void BadReceivedDateTime_UsesTheTa1Time()
    {
        var ta1 = BuildFor(Interchange(f => { f[8] = "261340"; f[9] = "2561"; }));
        ta1.Should().Contain("TA1*000000101*261010*1430*R*014~");
    }

    [Fact]
    public void BadSeparators_FallBackToDefaults()
    {
        var ta1 = BuildFor(Interchange(f => f[15] = "*"));
        ta1.Should().EndWith("*:~TA1*000000101*260115*1200*R*027~IEA*0*000000042~");
    }

    [Fact]
    public void Parser_ReadsTa1sAheadOfFunctionalGroups()
    {
        var partnerTa1 =
            "ISA*00*          *00*          *ZZ*PARTNER        *ZZ*CHO            *261010*0900*^*00501*000000777*0*P*:~" +
            "TA1*123456789*261009*1700*R*006~" +
            "TA1*123456790*261009*1701*A*000~" +
            "IEA*0*000000777~";

        var parsed = Ta1Parser.Parse(partnerTa1).Single();

        parsed.Acknowledgments.Select(a => (a.AcknowledgedControlNumber, a.AckCode, a.NoteCode))
            .Should().Equal(("123456789", "R", "006"), ("123456790", "A", "000"));
    }

    [Theory]
    [InlineData("not x12")]
    [InlineData("ISA*00*          *00*          *ZZ*PARTNER        *ZZ*CHO            *261010*0900*^*00501*000000777*0*P*:~TA1*123456789*261009*1700*Q*006~IEA*0*000000777~")]
    [InlineData("ISA*00*          *00*          *ZZ*PARTNER        *ZZ*CHO            *261010*0900*^*00501*000000777*0*P*:~TA1*123456789*261009*1700*A*6~IEA*0*000000777~")]
    public void Parser_RefusesWhatIsNotATa1(string content)
    {
        var act = () => Ta1Parser.Parse(content);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Parser_RefusesAFileWithNoTa1()
    {
        var act = () => Ta1Parser.Parse(Interchange());
        act.Should().Throw<FormatException>().WithMessage("*no TA1*");
    }
}
