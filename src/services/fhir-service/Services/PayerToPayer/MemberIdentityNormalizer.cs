using System.Globalization;
using System.Text;

namespace FhirService.Services.PayerToPayer;

/// <summary>
/// Explicit, testable normalization of the identity attributes a member-match
/// (P2P-04) compares. Formatting differences that should be equivalent — casing,
/// surrounding whitespace, accents, phone punctuation, ZIP+4 vs ZIP5 — are
/// collapsed so equal people compare equal. Normalization is deliberately
/// conservative: it never merges values that could belong to distinct people
/// (e.g. it does not transliterate names, drop name parts, or truncate
/// identifiers), so it cannot make two different members look like one.
///
/// Every method returns <c>null</c> for absent input so "not supplied" is never
/// confused with a value.
/// </summary>
public static class MemberIdentityNormalizer
{
    /// <summary>Trim, collapse internal whitespace, strip diacritics, upper-case (invariant).</summary>
    public static string? Name(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var collapsed = CollapseWhitespace(RemoveDiacritics(value.Trim()));
        return collapsed.Length == 0 ? null : collapsed.ToUpperInvariant();
    }

    /// <summary>Canonicalize a date to yyyy-MM-dd; returns null if unparseable.</summary>
    public static string? BirthDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// Map a recognized value to MALE / FEMALE / OTHER. An unknown, empty, or
    /// unparseable value returns <c>null</c> — "not supplied" — so it is never
    /// compared as a contradicting attribute (an invalid gender must not force a
    /// false non-match against a member whose sex is known and whose strong
    /// identifiers agree).
    /// </summary>
    public static string? Gender(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToUpperInvariant() switch
        {
            "M" or "MALE" => "MALE",
            "F" or "FEMALE" => "FEMALE",
            "O" or "OTHER" => "OTHER",
            _ => null,
        };
    }

    /// <summary>
    /// Trim, upper-case, and remove spaces and hyphens from an identifier
    /// (member/subscriber id, SSN, payer id) so "sub-2001" and "SUB 2001" and
    /// "SUB2001" compare equal. The identifier's own characters are preserved.
    /// </summary>
    public static string? Identifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (ch is ' ' or '-' or '‐' or '‑') continue;
            sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>Keep digits only; returns the last 10 digits (US NANP) when longer.</summary>
    public static string? Phone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return null;
        return digits.Length > 10 ? digits[^10..] : digits;
    }

    /// <summary>Normalize a US postal code to its 5-digit prefix (ZIP5).</summary>
    public static string? PostalCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length < 5) return digits.Length == 0 ? null : digits;
        return digits[..5];
    }

    /// <summary>Trim and lower-case an email address.</summary>
    public static string? Email(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var previousWasSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasSpace && sb.Length > 0) sb.Append(' ');
                previousWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                previousWasSpace = false;
            }
        }
        return sb.ToString().TrimEnd();
    }
}
