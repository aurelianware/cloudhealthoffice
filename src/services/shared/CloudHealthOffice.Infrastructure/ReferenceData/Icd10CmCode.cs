using System.Text.RegularExpressions;

namespace CloudHealthOffice.Infrastructure.ReferenceData;

/// <summary>
/// The two spellings of an ICD-10-CM code. X12 carries codes without the dot
/// (<c>J189</c>); FHIR's <c>http://hl7.org/fhir/sid/icd-10-cm</c> code system
/// uses the dotted form (<c>J18.9</c>), and a validator rejects the undotted
/// one as an unknown code. CHO stores what the 837 sent, so FHIR projections
/// convert on the way out.
/// </summary>
public static partial class Icd10CmCode
{
    /// <summary>
    /// The code as FHIR spells it: upper case, with a dot after the third
    /// character when there are more than three. A value that isn't shaped
    /// like an ICD-10-CM code is returned unchanged.
    /// </summary>
    public static string ToFhir(string code)
    {
        var compact = code.Trim().Replace(".", "", StringComparison.Ordinal).ToUpperInvariant();
        if (!Shape().IsMatch(compact)) return code;
        return compact.Length > 3 ? $"{compact[..3]}.{compact[3..]}" : compact;
    }

    // A letter, a digit, then one to five more alphanumerics (three to seven
    // characters in all).
    [GeneratedRegex("^[A-Z][0-9][0-9A-Z]{1,5}$")]
    private static partial Regex Shape();
}
