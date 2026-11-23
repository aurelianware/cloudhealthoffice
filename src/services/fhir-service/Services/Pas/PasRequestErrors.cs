namespace FhirService.Services.Pas;

/// <summary>
/// One error in a PAS request, as the 278 response's AAA segment states it:
/// the X12 reject reason (code list 901, AAA03), what the submitter should do
/// (code list 889, AAA04), the X12 element it is about, and the FHIRPath into
/// the request Bundle (PAS <c>extension-errorPath</c>).
/// </summary>
public sealed record PasRequestError(
    string Code, string FollowUpAction, string? X12Element, string ErrorPath, int? ItemSequence, string Message);

/// <summary>
/// Turns what CHO refuses in a request into PAS errors. The X12 codes are
/// chosen by the element the problem is in; anything unrecognised is
/// "33 Input Errors". A submitter can correct every one of these and resubmit
/// (889 <c>C</c>), except an authorization CHO does not hold.
/// </summary>
public static class PasRequestErrors
{
    public const string RejectReasonSystem = "https://codesystem.x12.org/005010/901";
    public const string FollowUpActionSystem = "https://codesystem.x12.org/005010/889";

    /// <summary>889 C: please correct and resubmit.</summary>
    public const string CorrectAndResubmit = "C";

    /// <summary>901 AA: authorization number not found.</summary>
    public const string AuthorizationNotFound = "AA";

    /// <summary>901 33: input errors, for a problem no more specific code describes.</summary>
    public const string InputErrors = "33";

    public static PasRequestError FromMapping(PasMappingError error)
    {
        var (code, element) = CodeFor(error.Expression);
        var (path, sequence) = PathFor(error.Expression);
        return new PasRequestError(code, CorrectAndResubmit, element, path, sequence, error.Message);
    }

    /// <summary>
    /// The single answer to an inquiry CHO cannot answer from a record: not
    /// held, held for another tenant, or held for another provider look alike,
    /// so the identifier space cannot be probed.
    /// </summary>
    public static PasRequestError InquiryNotMatched() => new(
        AuthorizationNotFound, CorrectAndResubmit, "2000E-TRN02",
        "Bundle.entry.resource.ofType(Claim).identifier",
        null, "No prior authorization matching the supplied identifiers is available.");

    private static (string Code, string? Element) CodeFor(string expression) => expression switch
    {
        _ when expression.StartsWith("Patient.birthDate", StringComparison.Ordinal) => ("58", "2010C-DMG02"),
        _ when expression.StartsWith("Patient.name", StringComparison.Ordinal) => ("73", "2010C-NM103"),
        _ when expression.StartsWith("Patient.identifier", StringComparison.Ordinal) => ("72", "2010C-NM109"),
        _ when expression.StartsWith("Claim.patient", StringComparison.Ordinal) => ("72", "2010C-NM109"),
        _ when expression.StartsWith("Claim.insurance", StringComparison.Ordinal) => ("72", "2010C-NM109"),
        _ when expression.StartsWith("Claim.provider", StringComparison.Ordinal) => ("43", "2010B-NM109"),
        _ when expression.StartsWith("Claim.item.serviced", StringComparison.Ordinal) => ("57", "2000E-DTP03"),
        _ when expression.Contains(".quantity", StringComparison.Ordinal) => (InputErrors, "2000F-HSD02"),
        _ when expression.StartsWith("Claim.item", StringComparison.Ordinal) => ("AG", "2000F-SV101"),
        _ when expression.StartsWith("Claim.diagnosis", StringComparison.Ordinal) => ("AF", "2000E-HI01"),
        _ when expression.StartsWith("Claim.supportingInfo", StringComparison.Ordinal) => (InputErrors, "2000E-PWK01"),
        _ => (InputErrors, null),
    };

    /// <summary>
    /// The mapper's expression as a FHIRPath from the Bundle (PAS: "The
    /// expression will start with the Bundle"), and the line it names.
    /// <c>Claim.item[2].quantity</c> names the item whose sequence is 2.
    /// </summary>
    private static (string Path, int? Sequence) PathFor(string expression)
    {
        var dot = expression.IndexOf('.');
        var type = dot < 0 ? expression : expression[..dot];
        var rest = dot < 0 ? string.Empty : expression[dot..];

        int? sequence = null;
        var match = System.Text.RegularExpressions.Regex.Match(rest, @"^\.(item|diagnosis|supportingInfo)\[(\d+)\]");
        if (match.Success && int.TryParse(match.Groups[2].Value, out var n))
        {
            rest = $".{match.Groups[1].Value}.where(sequence = {n})" + rest[match.Length..];
            if (match.Groups[1].Value == "item") sequence = n;
        }

        return ($"Bundle.entry.resource.ofType({type}){rest}", sequence);
    }
}
