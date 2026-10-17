using System.Globalization;
using System.Text.Json;
using StackExchange.Redis;

namespace CloudHealthOffice.BenefitEngine.Services;

/// <summary>
/// The Redis accumulator store's unit: integer hundredths (cents for money,
/// 1/100 for visit and day counts). Integer arithmetic (HINCRBY, Lua integer
/// math) cannot drift the way HINCRBYFLOAT doubles did, where 10,000 × $0.10
/// did not reliably sum to $1,000.00 and a deductible could read as just
/// below its limit.
/// </summary>
public static class AccumulatorCents
{
    /// <summary>
    /// <paramref name="amount"/> in cents. Throws when it carries precision
    /// below a cent: the store never rounds an amount silently, and an amount
    /// that is not a whole number of cents means the pricing upstream is wrong.
    /// </summary>
    public static long ToCents(decimal amount)
    {
        var rounded = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (rounded != amount)
        {
            throw new ArgumentException(
                $"Accumulator amount {amount.ToString(CultureInfo.InvariantCulture)} has precision below one cent; " +
                "amounts reaching the accumulator store must be whole cents.", nameof(amount));
        }

        return checked((long)(rounded * 100m));
    }

    public static decimal FromCents(long cents) => cents / 100m;

    /// <summary>A stored hash value: an integer number of cents.</summary>
    public static decimal FromRedis(RedisValue value, string field)
    {
        if (!long.TryParse(value.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var cents))
        {
            throw new InvalidOperationException(
                $"Accumulator field '{field}' holds '{value}', not an integer number of cents.");
        }

        return FromCents(cents);
    }

    /// <summary>A journalled or clamp amount written by the Lua scripts: an integer number of cents.</summary>
    public static decimal FromJson(JsonElement value)
    {
        var cents = value.GetDecimal();
        if (cents != decimal.Truncate(cents))
            throw new InvalidOperationException($"Accumulator journal amount {cents} is not an integer number of cents.");
        return cents / 100m;
    }
}
