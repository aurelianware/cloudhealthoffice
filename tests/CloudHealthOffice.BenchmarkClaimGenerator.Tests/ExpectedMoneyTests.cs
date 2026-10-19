using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.Infrastructure.Money;
using Xunit;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

/// <summary>
/// The answer key's expected amounts are compared with what the engines pay
/// within one cent, so the generator rounds money exactly as the engines do.
/// </summary>
public class ExpectedMoneyTests
{
    [Theory]
    [InlineData("0.125", "0.13")] // banker's rounding gives 0.12
    [InlineData("-0.125", "-0.13")]
    [InlineData("65.0325", "65.03")]
    [InlineData("9814.845", "9814.85")]
    public void ToCents_RoundsHalfAwayFromZero(string amount, string expected)
    {
        Assert.Equal(decimal.Parse(expected), ExpectedMoney.ToCents(decimal.Parse(amount)));
    }

    [Fact]
    public void ToCents_MatchesTheEnginesMoneyRounding()
    {
        for (var thousandths = -20_000; thousandths <= 20_000; thousandths++)
        {
            var amount = thousandths / 1000m;
            Assert.Equal(MoneyRounding.ToCents(amount), ExpectedMoney.ToCents(amount));
        }
    }
}
