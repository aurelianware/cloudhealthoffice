namespace CloudHealthOffice.Tools.MccPlatformValidator;

public static class MccValidationProviderIdentity
{
    private const int SyntheticNpiSpace = 10_000_000;

    public static string BuildNpi(int seed, Guid runId, int scenarioIndex, int role)
    {
        if (scenarioIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scenarioIndex), "Scenario index must be non-negative.");
        }

        var roleDigit = role switch
        {
            0 => 3, // billing providers for scoreable validation scenarios
            1 => 4, // adjudicatable rendering providers
            2 => 5, // intentionally excluded rendering providers
            _ => throw new ArgumentOutOfRangeException(nameof(role), "Validation provider role must be 0, 1, or 2.")
        };

        unchecked
        {
            var runHash = BitConverter.ToUInt32(runId.ToByteArray(), 0);
            var value = (runHash + (uint)(seed * 1_000_003) + (uint)scenarioIndex) % SyntheticNpiSpace;
            var baseNineDigits = $"9{roleDigit}{value:D7}";
            return $"{baseNineDigits}{CalculateNpiCheckDigit(baseNineDigits)}";
        }
    }

    private static int CalculateNpiCheckDigit(string baseNineDigits)
    {
        const string npiPrefix = "80840";
        var candidate = $"{npiPrefix}{baseNineDigits}0";
        var sum = 0;
        var doubleDigit = false;

        for (var i = candidate.Length - 1; i >= 0; i--)
        {
            var digit = candidate[i] - '0';
            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubleDigit = !doubleDigit;
        }

        return (10 - (sum % 10)) % 10;
    }
}
