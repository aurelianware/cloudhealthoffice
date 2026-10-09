namespace CloudHealthOffice.ReferenceData.Domain;

/// <summary>
/// The NUBC type of bill (UB-04 FL 4; 837I CLM05-1 facility type + CLM05-3
/// frequency). One validity rule shared by the benefit engine (service-category
/// resolution) and the fee schedule engine (facility / non-facility pricing), so
/// both read the same value as "a type of bill was sent" or "none was".
/// </summary>
public static class NubcTypeOfBill
{
    /// <summary>
    /// "111" → "111", "0111" → "111"; null for anything else (blank, "0", "N/A",
    /// "11", "111garbage", or a four-digit value without the leading zero such as "1111").
    /// Surrounding whitespace is ignored.
    /// </summary>
    public static string? Normalize(string? typeOfBill)
    {
        if (string.IsNullOrWhiteSpace(typeOfBill)) return null;
        var tob = typeOfBill.Trim();
        if (tob.Length == 4)
        {
            if (tob[0] != '0') return null;
            tob = tob[1..];
        }
        return tob.Length == 3 && tob.All(char.IsAsciiDigit) ? tob : null;
    }

    /// <summary>True when <paramref name="typeOfBill"/> is a well-formed type of bill (see <see cref="Normalize"/>).</summary>
    public static bool IsValid(string? typeOfBill) => Normalize(typeOfBill) is not null;
}
