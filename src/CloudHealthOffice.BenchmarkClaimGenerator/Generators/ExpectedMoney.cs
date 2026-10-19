namespace CloudHealthOffice.BenchmarkClaimGenerator.Generators;

/// <summary>
/// Cent rounding for the expected outcomes (the Million Claim Challenge answer key).
/// </summary>
/// <remarks>
/// The MCC validator compares each expected plan payment with what the platform's
/// engines pay, within one cent, so the answer key rounds money the way the engines
/// do: to the cent, half away from zero. This is the same rule as
/// <c>CloudHealthOffice.Infrastructure.Money.MoneyRounding.ToCents</c>; the generator
/// keeps its own copy because it does not reference the service infrastructure
/// library, and a test pins the two together.
/// </remarks>
internal static class ExpectedMoney
{
    public static decimal ToCents(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}
