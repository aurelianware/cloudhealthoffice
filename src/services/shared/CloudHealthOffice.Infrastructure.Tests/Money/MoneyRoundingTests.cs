using CloudHealthOffice.Infrastructure.Money;
using FluentAssertions;

namespace CloudHealthOffice.Infrastructure.Tests.Money;

public class MoneyRoundingTests
{
    [Theory]
    [InlineData("0.005", "0.01")]
    [InlineData("-0.005", "-0.01")]
    [InlineData("0.015", "0.02")]
    [InlineData("-0.015", "-0.02")]
    [InlineData("0.025", "0.03")] // banker's rounding gives 0.02
    [InlineData("-0.025", "-0.03")] // banker's rounding gives -0.02
    [InlineData("10.125", "10.13")] // banker's rounding gives 10.12
    [InlineData("2.345", "2.35")]
    public void ToCents_RoundsMidpointsAwayFromZero(string amount, string expected)
    {
        MoneyRounding.ToCents(decimal.Parse(amount)).Should().Be(decimal.Parse(expected));
    }

    [Theory]
    [InlineData("0.0049", "0.00")]
    [InlineData("0.0051", "0.01")]
    [InlineData("-0.0049", "0.00")]
    [InlineData("-0.0051", "-0.01")]
    [InlineData("1.23499999", "1.23")]
    public void ToCents_RoundsNonMidpointsToNearestCent(string amount, string expected)
    {
        MoneyRounding.ToCents(decimal.Parse(amount)).Should().Be(decimal.Parse(expected));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.01")]
    [InlineData("-0.01")]
    [InlineData("12.34")]
    [InlineData("-12.34")]
    [InlineData("100")]
    [InlineData("1999.90")]
    public void ToCents_LeavesWholeCentAmountsUnchanged(string amount)
    {
        var value = decimal.Parse(amount);

        MoneyRounding.ToCents(value).Should().Be(value);
    }

    [Fact]
    public void ToCents_HandlesLargeAmounts()
    {
        MoneyRounding.ToCents(123_456_789_012.345m).Should().Be(123_456_789_012.35m);
        MoneyRounding.ToCents(-123_456_789_012.345m).Should().Be(-123_456_789_012.35m);
        MoneyRounding.ToCents(7_922_816_251_426_433_759_354_395.035m)
            .Should().Be(7_922_816_251_426_433_759_354_395.04m);
        MoneyRounding.ToCents(decimal.MaxValue).Should().Be(decimal.MaxValue);
        MoneyRounding.ToCents(decimal.MinValue).Should().Be(decimal.MinValue);
    }

    [Fact]
    public void ToCents_DiffersFromDefaultRoundingOnlyAtEvenHalfCents()
    {
        // The default Math.Round(x, 2) is banker's rounding: 0.125 → 0.12.
        Math.Round(0.125m, 2).Should().Be(0.12m);
        MoneyRounding.ToCents(0.125m).Should().Be(0.13m);

        // Where the cent below the midpoint is odd, both rules agree.
        Math.Round(0.135m, 2).Should().Be(0.14m);
        MoneyRounding.ToCents(0.135m).Should().Be(0.14m);
    }
}
