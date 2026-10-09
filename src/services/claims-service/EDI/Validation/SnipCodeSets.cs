using System.Text.RegularExpressions;

namespace ClaimsService.EDI.Validation;

/// <summary>
/// Optional lookup against loaded reference data (e.g. reference-data-service
/// code tables). Return true/false when the code system is loaded, null when
/// it is not — the SNIP validator then falls back to a format check only.
/// </summary>
public interface ISnipCodeSetReference
{
    /// <param name="codeSystem">One of the <see cref="SnipCodeSystems"/> constants.</param>
    /// <param name="code">The code as sent in X12 (no decimal point).</param>
    /// <param name="serviceDate">Date of service the code must be valid on, when known.</param>
    bool? IsValid(string codeSystem, string code, DateOnly? serviceDate);
}

public static class SnipCodeSystems
{
    public const string Icd10Cm = "ICD10CM";
    public const string Icd10Pcs = "ICD10PCS";
    public const string Cpt = "CPT";
    public const string Hcpcs = "HCPCS";
    public const string PlaceOfService = "POS";
    public const string RevenueCode = "REV";
}

/// <summary>
/// Built-in code-set knowledge for SNIP level 5: code formats, plus the full
/// CMS place-of-service list (a small, stable, public table). ICD-10, CPT and
/// HCPCS membership needs licensed/quarterly tables, so without an
/// <see cref="ISnipCodeSetReference"/> those are format checks.
/// </summary>
public static partial class SnipCodeSets
{
    /// <summary>
    /// CMS Place of Service code set (https://www.cms.gov/medicare/coding-billing/place-of-service-codes/code-sets).
    /// </summary>
    public static readonly IReadOnlySet<string> PlaceOfServiceCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "01", "02", "03", "04", "05", "06", "07", "08", "09", "10",
        "11", "12", "13", "14", "15", "16", "17", "18", "19", "20",
        "21", "22", "23", "24", "25", "26", "27",
        "31", "32", "33", "34",
        "41", "42",
        "49", "50", "51", "52", "53", "54", "55", "56", "57", "58",
        "60", "61", "62", "65", "66",
        "71", "72",
        "81", "99",
    };

    /// <summary>ICD-10-CM as sent in X12: letter (not U), digit, then 1–5 alphanumerics, no decimal point.</summary>
    public static bool IsIcd10CmFormat(string code) => Icd10CmRegex().IsMatch(code);

    /// <summary>ICD-10-PCS: exactly 7 characters, digits and letters other than I and O.</summary>
    public static bool IsIcd10PcsFormat(string code) => Icd10PcsRegex().IsMatch(code);

    /// <summary>CPT: five digits, or four digits plus F (Category II), T (Category III) or U (PLA).</summary>
    public static bool IsCptFormat(string code) => CptRegex().IsMatch(code);

    /// <summary>HCPCS Level II: a letter A–V followed by four digits.</summary>
    public static bool IsHcpcsFormat(string code) => HcpcsRegex().IsMatch(code);

    public static bool IsModifierFormat(string code) => ModifierRegex().IsMatch(code);

    /// <summary>NUBC revenue code: four digits.</summary>
    public static bool IsRevenueCodeFormat(string code) => RevenueRegex().IsMatch(code);

    /// <summary>
    /// NPI check digit: Luhn over the 9-digit base prefixed with 80840
    /// (the NPI's ISO card-issuer prefix), per the CMS NPI standard.
    /// </summary>
    public static bool IsValidNpi(string? npi)
    {
        if (npi is null || npi.Length != 10 || !npi.All(char.IsAsciiDigit))
            return false;

        var prefixed = "80840" + npi;
        var sum = 0;
        var alternate = false;
        for (var i = prefixed.Length - 1; i >= 0; i--)
        {
            var digit = prefixed[i] - '0';
            if (alternate)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }

    [GeneratedRegex(@"^[A-TV-Z][0-9][0-9A-Z]{1,5}$")]
    private static partial Regex Icd10CmRegex();

    [GeneratedRegex(@"^[0-9A-HJ-NP-Z]{7}$")]
    private static partial Regex Icd10PcsRegex();

    [GeneratedRegex(@"^[0-9]{4}[0-9FTU]$")]
    private static partial Regex CptRegex();

    [GeneratedRegex(@"^[A-V][0-9]{4}$")]
    private static partial Regex HcpcsRegex();

    [GeneratedRegex(@"^[A-Z0-9]{2}$")]
    private static partial Regex ModifierRegex();

    [GeneratedRegex(@"^[0-9]{4}$")]
    private static partial Regex RevenueRegex();
}
