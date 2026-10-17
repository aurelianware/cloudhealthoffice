namespace CloudHealthOffice.Infrastructure.Money;

/// <summary>
/// The one rounding rule for money in CHO: amounts are rounded to the cent,
/// half away from zero (<see cref="MidpointRounding.AwayFromZero"/>).
/// </summary>
/// <remarks>
/// <para>
/// <c>Math.Round(x, 2)</c> and <c>decimal.Round(x, 2)</c> default to banker's
/// rounding (<see cref="MidpointRounding.ToEven"/>), which rounds 0.125 to 0.12
/// but 0.135 to 0.14. Mixing the two rules makes the same amount round
/// differently in different services, so every money-to-cents rounding calls
/// <see cref="ToCents"/> instead.
/// </para>
/// <para>
/// Only money uses this. Factors and ratios (proration, adjustment factors),
/// scores, percentages for display and counts keep their own precision.
/// </para>
/// </remarks>
public static class MoneyRounding
{
    /// <summary>
    /// Rounds <paramref name="amount"/> to the cent, half away from zero:
    /// 0.125 → 0.13, -0.125 → -0.13, 0.135 → 0.14.
    /// </summary>
    public static decimal ToCents(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}
