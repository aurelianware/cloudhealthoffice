using EligibilityService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.Edi.Tests;

public class Edi270ParserTests
{
    [Fact]
    public void Parse_Valid270_ExtractsCoreFields()
    {
        var parser = new Edi270Parser(NullLogger<Edi270Parser>.Instance);

        var isa = BuildIsa("SENDERID", "RECEIVERID", "000000001");
        var edi =
            isa +
            "GS*HS*APP_SEND*APP_RECV*20260309*1230*1*X*005010X279A1~" +
            "ST*270*CTRL123~" +
            "HL*1**20*1~" +
            "NM1*PR*2*PAYER*****PI*PAY01~" +
            "HL*2*1*21*1~" +
            "NM1*1P*2*PROVIDER*****XX*1234567890~" +
            "HL*3*2*22*0~" +
            "NM1*IL*1*DOE*JANE****MI*SUB123~" +
            "REF*1L*GRP99~" +
            "DMG*D8*19800115*F~" +
            "DTP*291*D8*20260301~" +
            "EQ*30~" +
            "SE*12*CTRL123~" +
            "GE*1*1~" +
            "IEA*1*000000001~";

        var parsed = parser.Parse(edi);

        Assert.Equal("SENDERID", parsed.InterchangeSenderId);
        Assert.Equal("RECEIVERID", parsed.InterchangeReceiverId);
        Assert.Equal("APP_SEND", parsed.ApplicationSenderId);
        Assert.Equal("APP_RECV", parsed.ApplicationReceiverId);

        Assert.Equal("CTRL123", parsed.Inquiry.ControlNumber);
        Assert.Equal("PAY01", parsed.Inquiry.PayerId);
        Assert.Equal("PAYER", parsed.Inquiry.PayerName);
        Assert.Equal("1234567890", parsed.Inquiry.ProviderNPI);
        Assert.Equal("SUB123", parsed.Inquiry.SubscriberId);
        Assert.Equal("DOE", parsed.Inquiry.SubscriberLastName);
        Assert.Equal("JANE", parsed.Inquiry.SubscriberFirstName);
        Assert.Equal("GRP99", parsed.Inquiry.GroupNumber);
        Assert.Equal("F", parsed.Inquiry.SubscriberGender);
        Assert.Equal(new DateTime(1980, 1, 15), parsed.Inquiry.SubscriberDOB);
        Assert.Equal(new DateTime(2026, 3, 1), parsed.Inquiry.ServiceDateFrom);
        Assert.Equal(new DateTime(2026, 3, 1), parsed.Inquiry.ServiceDateTo);
        Assert.Equal("30", parsed.Inquiry.ServiceTypeCode);
    }

    [Fact]
    public void Parse_EmptyInput_ThrowsArgumentException()
    {
        var parser = new Edi270Parser(NullLogger<Edi270Parser>.Instance);
        Assert.Throws<ArgumentException>(() => parser.Parse(""));
    }

    private static string BuildIsa(string sender, string receiver, string controlNumber)
    {
        var parts = new[]
        {
            "ISA",
            "00",
            "          ",
            "00",
            "          ",
            "ZZ",
            sender.PadRight(15)[..15],
            "ZZ",
            receiver.PadRight(15)[..15],
            "260309",
            "1230",
            "^",
            "00501",
            controlNumber.PadLeft(9, '0'),
            "0",
            "P",
            ":"
        };

        var isa = string.Join("*", parts) + "~";
        Assert.Equal(106, isa.Length);
        return isa;
    }
}
