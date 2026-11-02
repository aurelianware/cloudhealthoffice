using System.Text.Json;
using EligibilityService.Models;

namespace CloudHealthOffice.EligibilityService.Tests;

/// <summary>An inquiry's line of business on the wire: shared names, never numbers.</summary>
public class EligibilityLineOfBusinessJsonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("Exchange", LineOfBusiness.Exchange)]
    [InlineData("Marketplace", LineOfBusiness.Exchange)]
    [InlineData("va", LineOfBusiness.VA)]
    public void An_inquiry_reads_the_shared_names(string sent, LineOfBusiness expected)
        => Assert.Equal(expected, JsonSerializer.Deserialize<EligibilityInquiry>($$"""{"lineOfBusiness":"{{sent}}"}""", Web)!.LineOfBusiness);

    [Theory]
    [InlineData("3")]   // was Exchange here, is Medicaid in the shared numbering: refused, not guessed
    [InlineData("\"Unknown\"")]
    public void An_inquiry_refuses_numbers_and_Unknown(string sent)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EligibilityInquiry>("{\"lineOfBusiness\":" + sent + "}", Web));

    [Fact]
    public void An_inquiry_writes_the_shared_name()
        => Assert.Contains("\"lineOfBusiness\":\"TRICARE\"",
            JsonSerializer.Serialize(new EligibilityInquiry { LineOfBusiness = LineOfBusiness.TRICARE }, Web));
}
