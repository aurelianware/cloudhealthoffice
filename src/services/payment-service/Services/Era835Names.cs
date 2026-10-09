using PaymentService.Models;

namespace PaymentService.Services;

/// <summary>
/// Name elements of the 835 1000A/1000B N1 segments, shared by
/// <see cref="EraGeneratorService"/> and <see cref="BatchEraGeneratorService"/>
/// (payments, denials and reversals).
/// </summary>
public static class Era835Names
{
    /// <summary>005010X221A1 N102 (name) maximum length.</summary>
    public const int MaxNameLength = 60;

    /// <summary>
    /// The N102 value for <paramref name="name"/>. Truncation policy: X12
    /// delimiters are replaced by spaces, the name is trimmed, cut at
    /// <see cref="MaxNameLength"/> characters and trimmed again (no trailing
    /// space left by the cut). A longer name (claims-service accepts 300) is
    /// shortened, never a reason to fail the 835; the payee is identified by
    /// its NPI in N104 regardless.
    /// </summary>
    public static string N102(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;
        var escaped = name.Replace("*", " ").Replace("~", " ").Replace(":", " ").Replace("\\", " ").Trim();
        return escaped.Length <= MaxNameLength ? escaped : escaped[..MaxNameLength].TrimEnd();
    }

    /// <summary>005010X221A1 N301/N302 maximum length.</summary>
    public const int MaxAddressLineLength = 55;

    /// <summary>005010X221A1 N401 maximum length.</summary>
    public const int MaxCityLength = 30;

    /// <summary>
    /// The 1000B payee N3 and N4 segments for <paramref name="address"/> (the
    /// 837 Loop 2010AB pay-to address), or none when there is no address or
    /// it lacks the street line (N301) or city (N401) that N3/N4 require.
    /// Elements are cleaned of X12 delimiters and cut to their X12 maximum
    /// lengths (N3 55, N401 30, N402 2, N403 15, N404 3); trailing empty
    /// elements are dropped.
    /// </summary>
    public static IReadOnlyList<string> PayeeAddressSegments(PayeeAddress? address)
    {
        if (address is null) return Array.Empty<string>();
        var line1 = Element(address.Line1, MaxAddressLineLength);
        var city = Element(address.City, MaxCityLength);
        if (line1.Length == 0 || city.Length == 0) return Array.Empty<string>();

        return
        [
            Segment("N3", line1, Element(address.Line2, MaxAddressLineLength)),
            Segment("N4", city, Element(address.State, 2), Element(address.PostalCode, 15), Element(address.CountryCode, 3)),
        ];
    }

    private static string Element(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var cleaned = value.Replace("*", " ").Replace("~", " ").Replace(":", " ").Replace("\\", " ").Replace("^", " ").Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength].TrimEnd();
    }

    private static string Segment(string id, params string[] elements)
    {
        var used = elements.Length;
        while (used > 0 && elements[used - 1].Length == 0) used--;
        return id + string.Concat(elements.Take(used).Select(e => "*" + e)) + "~";
    }
}
