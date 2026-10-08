namespace CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;

using System.Globalization;
using System.Text;

/// <summary>
/// Name normalization shared by the sync (stored match keys) and the screen
/// (query keys), so both sides of a comparison are normalized identically.
/// </summary>
public static class ExclusionNameNormalizer
{
    private static readonly HashSet<string> PersonSuffixes = new(StringComparer.Ordinal)
    {
        "JR", "SR", "II", "III", "IV", "V", "MD", "DO", "DDS", "DMD", "PHD", "RN", "NP", "PA", "ESQ"
    };

    private static readonly HashSet<string> BusinessSuffixes = new(StringComparer.Ordinal)
    {
        "THE", "LLC", "LLP", "LP", "LTD", "LIMITED", "INC", "INCORPORATED", "CORP", "CORPORATION",
        "CO", "COMPANY", "PC", "PA", "PLLC", "PLC", "PSC", "SC", "DBA"
    };

    /// <summary>
    /// Upper-case, strip diacritics, keep letters only (spaces, hyphens and
    /// apostrophes removed: "O'Brien-Smith" → "OBRIENSMITH"). Trailing
    /// generational/credential suffixes are dropped from last names.
    /// </summary>
    public static string NormalizePersonName(string? value, bool stripSuffixes = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var tokens = Tokenize(value);
        if (stripSuffixes)
        {
            while (tokens.Count > 1 && PersonSuffixes.Contains(tokens[^1]))
                tokens.RemoveAt(tokens.Count - 1);
        }

        return string.Concat(tokens);
    }

    /// <summary>
    /// Upper-case, strip diacritics and punctuation, "&amp;" → AND, drop entity
    /// suffixes (LLC, INC, CORP, ...) and a leading THE, collapse whitespace.
    /// "The Acme Clinic, L.L.C." and "ACME CLINIC LLC" both → "ACME CLINIC".
    /// </summary>
    public static string NormalizeBusinessName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // "L.L.C." / "P.C." → "LLC" / "PC" before tokenizing on punctuation.
        var compacted = value.Replace("&", " AND ").Replace(".", string.Empty);
        var tokens = Tokenize(compacted);
        if (tokens.Count > 1 && tokens[0] == "THE")
            tokens.RemoveAt(0);
        // Drop a trailing run of entity suffixes ("... HEALTH CO INC").
        while (tokens.Count > 1 && BusinessSuffixes.Contains(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);
        // "L L C" spelled with spaces.
        if (tokens.Count > 3 && tokens[^3] == "L" && tokens[^2] == "L" && tokens[^1] == "C")
            tokens.RemoveRange(tokens.Count - 3, 3);

        return string.Join(' ', tokens);
    }

    /// <summary>10-digit NPI or null (empty, all zeros, or malformed).</summary>
    public static string? NormalizeNpi(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length != 10 || digits.All(c => c == '0'))
            return null;
        return digits;
    }

    private static List<string> Tokenize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var tokens = new List<string>();
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (c is '\'' or '’')
                continue; // O'BRIEN → OBRIEN
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
            else if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0)
            tokens.Add(sb.ToString());
        return tokens;
    }
}
