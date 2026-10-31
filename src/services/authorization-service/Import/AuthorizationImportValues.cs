using System.Globalization;
using AuthorizationService.Models;
using CloudHealthOffice.Infrastructure.Import.Cpcds;

namespace AuthorizationService.Import;

/// <summary>
/// How the values UM systems export are read into CHO's model. Shared by the
/// CSV and X12 278 readers so both read a status, a date or a code the same way.
/// </summary>
public static class AuthorizationImportValues
{
    /// <summary>
    /// A status as a UM system names it, or an X12 HCR01 action code, and the
    /// 278 review decision CHO records with it. Null when not recognized.
    /// </summary>
    public static (AuthorizationStatus Status, string? Decision)? Status(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return CpcdsHeaders.Normalize(value) switch
        {
            "approved" or "approve" or "certified" or "certifiedintotal" or "a1" or "complete" or "completed"
                => (AuthorizationStatus.Approved, "A1"),
            "modified" or "partial" or "partiallyapproved" or "partialapproval" or "certifiedpartial" or "a2" or "a6"
                => (AuthorizationStatus.Modified, "A2"),
            "denied" or "deny" or "notcertified" or "a3"
                => (AuthorizationStatus.Denied, "A3"),
            "pended" or "pending" or "pend" or "a4"
                => (AuthorizationStatus.Pended, "A4"),
            "inreview" or "underreview" or "review" or "inprogress"
                => (AuthorizationStatus.InReview, null),
            "submitted" or "received" or "open" or "new"
                => (AuthorizationStatus.Submitted, null),
            "cancelled" or "canceled" or "withdrawn" or "voided" or "void" or "c"
                => (AuthorizationStatus.Cancelled, null),
            "expired"
                => (AuthorizationStatus.Expired, null),
            _ => null,
        };
    }

    /// <summary>A status that carries a clinical decision.</summary>
    public static bool IsDecided(AuthorizationStatus status)
        => status is AuthorizationStatus.Approved or AuthorizationStatus.Modified or AuthorizationStatus.Denied;

    public static AuthorizationType RequestType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return AuthorizationType.PreAuthorization;
        var key = CpcdsHeaders.Normalize(value);
        if (key.StartsWith("pre", StringComparison.Ordinal) || key.StartsWith("prior", StringComparison.Ordinal)
            || key is "hs" or "ar" or "inpatient" or "outpatient" or "standard")
            return AuthorizationType.PreAuthorization;
        if (key.StartsWith("concurrent", StringComparison.Ordinal))
            return AuthorizationType.ConcurrentReview;
        if (key.StartsWith("retro", StringComparison.Ordinal) || key.StartsWith("post", StringComparison.Ordinal))
            return AuthorizationType.RetrospectiveReview;
        if (key.StartsWith("referral", StringComparison.Ordinal) || key == "sc")
            return AuthorizationType.Referral;
        return AuthorizationType.Unknown;
    }

    /// <summary>U (urgent) or E (standard), the 278 UM04 values CHO stores.</summary>
    public static string? LevelOfService(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return CpcdsHeaders.Normalize(value) switch
        {
            "u" or "urgent" or "expedited" or "emergency" or "03" or "3" => "U",
            "e" or "standard" or "elective" or "routine" or "nonurgent" => "E",
            _ => null,
        };
    }

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyyMMdd", "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "yyyy-MM-dd HH:mm:ss", "yyyyMMddHHmm",
    ];

    /// <summary>A date (or date and time) as UTC. Dates without a zone are taken as UTC.</summary>
    public static DateTime? Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        return DateTime.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, styles, out var date)
            ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
            : null;
    }

    /// <summary>A delimited list (semicolon, comma, space or caret).</summary>
    public static IEnumerable<string> List(string? value)
        => (value ?? string.Empty)
            .Split(new[] { ';', ',', ' ', '^', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.ToUpperInvariant());

    /// <summary>ICD-10-CM codes without dots; the first is the principal diagnosis.</summary>
    public static List<DiagnosisCode> DiagnosisCodes(string? value, List<string> warnings)
    {
        var codes = new List<DiagnosisCode>();
        foreach (var raw in List(value))
        {
            var code = raw.Replace(".", string.Empty, StringComparison.Ordinal);
            if (code.Length is < 3 or > 7 || !char.IsAsciiLetter(code[0]) || !code.All(char.IsAsciiLetterOrDigit))
            {
                warnings.Add($"Diagnosis code '{raw}' is not an ICD-10-CM code; left out.");
                continue;
            }
            if (codes.Any(c => c.Code == code))
                continue;
            codes.Add(new DiagnosisCode { Code = code, CodeQualifier = codes.Count == 0 ? "BK" : "BF" });
        }
        return codes;
    }

    /// <summary>
    /// The coding system of a line's code when it is not a plain CPT/HCPCS
    /// procedure (NDC, RxNorm, a FHIR system URI). Null for CPT and HCPCS.
    /// </summary>
    public static string? CodeSystem(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var key = CpcdsHeaders.Normalize(value);
        return key switch
        {
            "cpt" or "hcpcs" or "hc" or "cpt4" or "hcpcsii" => null,
            "ndc" or "n4" => "NDC",
            "rxnorm" => "RxNorm",
            _ => value.Trim().Length <= 256 ? value.Trim() : null,
        };
    }
}
