namespace DemoPersonaSeeder;

/// <summary>Check-digit math for synthetic identifiers, so they pass the services' format checks.</summary>
public static class Identifiers
{
    /// <summary>
    /// Builds a valid 10-digit NPI from a 9-digit base: the Luhn check digit is computed
    /// over "80840" + base, as CMS specifies.
    /// </summary>
    public static string Npi(string nineDigitBase)
    {
        if (nineDigitBase.Length != 9 || !nineDigitBase.All(char.IsDigit))
            throw new ArgumentException("NPI base must be 9 digits.", nameof(nineDigitBase));
        return nineDigitBase + LuhnCheckDigit("80840" + nineDigitBase);
    }

    /// <summary>True when <paramref name="npi"/> is 10 digits with a valid CMS Luhn check digit.</summary>
    public static bool IsValidNpi(string? npi)
        => npi is { Length: 10 } && npi.All(char.IsDigit) && Npi(npi[..9]) == npi;

    private static char LuhnCheckDigit(string digits)
    {
        var sum = 0;
        var doubleIt = true; // rightmost digit of the payload is doubled when a check digit will follow
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            doubleIt = !doubleIt;
        }
        return (char)('0' + (10 - sum % 10) % 10);
    }
}
