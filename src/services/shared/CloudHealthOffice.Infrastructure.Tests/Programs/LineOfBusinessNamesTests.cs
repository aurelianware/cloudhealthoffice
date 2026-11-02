using CloudHealthOffice.Infrastructure.Programs;

namespace CloudHealthOffice.Infrastructure.Tests.Programs;

public class LineOfBusinessNamesTests
{
    [Theory]
    [InlineData("Commercial", LineOfBusiness.Commercial)]
    [InlineData("medicare", LineOfBusiness.Medicare)]
    [InlineData(" Medicaid ", LineOfBusiness.Medicaid)]
    [InlineData("Exchange", LineOfBusiness.Exchange)]
    [InlineData("TRICARE", LineOfBusiness.TRICARE)]
    [InlineData("Tricare", LineOfBusiness.TRICARE)]
    [InlineData("VA", LineOfBusiness.VA)]
    // Legacy spellings still stored in appeals documents and payment runs, and in flight on events.
    [InlineData("Marketplace", LineOfBusiness.Exchange)]
    [InlineData("QHP", LineOfBusiness.Exchange)]
    [InlineData("MedicarePartD", LineOfBusiness.Medicare)]
    // Numbers as the 1-6 services send them in JSON.
    [InlineData("1", LineOfBusiness.Commercial)]
    [InlineData("4", LineOfBusiness.Exchange)]
    [InlineData("6", LineOfBusiness.VA)]
    public void Reads_names_legacy_spellings_and_numbers(string value, LineOfBusiness expected)
    {
        LineOfBusinessNames.TryParse(value, out var lob).Should().BeTrue();
        lob.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("Unknown")]
    [InlineData("Dental")]
    [InlineData("STAR")] // a program, not a line of business
    public void Anything_else_is_not_a_line_of_business(string? value)
    {
        LineOfBusinessNames.TryParse(value, out var lob).Should().BeFalse();
        lob.Should().Be(LineOfBusiness.Unknown);
        LineOfBusinessNames.Normalize(value).Should().BeNull();
    }

    [Fact]
    public void Normalize_writes_the_canonical_name()
    {
        LineOfBusinessNames.Normalize("marketplace").Should().Be("Exchange");
        LineOfBusinessNames.Normalize("2").Should().Be("Medicare");
        LineOfBusinessNames.AllNames.Should().Equal("Commercial", "Medicare", "Medicaid", "Exchange", "TRICARE", "VA");
    }

    [Theory]
    [InlineData("MedicarePartD", true)]
    [InlineData("PartD", true)]
    [InlineData("Medicare", false)]
    [InlineData("Marketplace", false)]
    [InlineData(null, false)]
    public void Part_D_spellings_are_recognised_as_a_benefit_type(string? value, bool partD)
        => LineOfBusinessNames.IsPartD(value).Should().Be(partD);

    [Fact]
    public void The_numbers_never_change()
    {
        // 15 services store these numbers in Mongo; renumbering would silently move members.
        Enum.GetValues<LineOfBusiness>().Select(l => (l.ToString(), (int)l)).Should().Equal(
            ("Unknown", 0), ("Commercial", 1), ("Medicare", 2), ("Medicaid", 3), ("Exchange", 4), ("TRICARE", 5), ("VA", 6));
    }
}
