using MongoDB.Bson.Serialization.Attributes;

namespace CHO.TerminologyService.Models;

// Dated code sets: which codes of a code system were valid on a given date of
// service. Built from the public-domain CMS releases (ICD-10-CM and ICD-10-PCS
// order files, the HCPCS Level II file), so no license is needed. Shared by
// every tenant, like the NLM maps: loading needs platform:admin, reads need
// terminology:read. Stored in code_set_releases and code_validity_periods.

/// <summary>The code systems a dated code set can be loaded for.</summary>
public static class CodeSetSystems
{
    public const string Icd10Cm = "http://hl7.org/fhir/sid/icd-10-cm";
    public const string Icd10Pcs = "http://www.cms.gov/Medicare/Coding/ICD10";
    public const string Hcpcs = "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets";

    public static readonly IReadOnlyList<string> All = [Icd10Cm, Icd10Pcs, Hcpcs];

    /// <summary>
    /// The canonical URI for a system, or null when it is not one a code set can be
    /// loaded for. Accepts http or https and a trailing slash, since both appear on
    /// claims and in CHO's own constants.
    /// </summary>
    public static string? Canonical(string? system)
    {
        if (string.IsNullOrWhiteSpace(system)) return null;
        var s = system.Trim().TrimEnd('/');
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = "http://" + s["https://".Length..];
        return All.FirstOrDefault(a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The code as stored: trimmed, upper case, without the dot ICD-10-CM codes are
    /// often written with (E11.65 and E1165 are the same code).
    /// </summary>
    public static string NormalizeCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? string.Empty : code.Trim().Replace(".", string.Empty).ToUpperInvariant();
}

/// <summary>
/// One loaded release of a code system (for example ICD-10-CM FY2026). It speaks
/// for dates from <see cref="EffectiveFrom"/> through <see cref="EffectiveTo"/>;
/// a later release that starts inside that window ends it the day before. A date
/// no release covers is not checked: the answer is "unknown", never "invalid".
/// </summary>
[BsonIgnoreExtraElements]
public class CodeSetRelease
{
    public string Id { get; set; } = string.Empty;
    public string System { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;

    /// <summary>First covered date (UTC midnight).</summary>
    public DateTime EffectiveFrom { get; set; }

    /// <summary>Last covered date (UTC midnight), inclusive; null while open-ended.</summary>
    public DateTime? EffectiveTo { get; set; }

    public int CodeCount { get; set; }
    public DateTime LoadedAtUtc { get; set; } = DateTime.UtcNow;
    public string? LoadedBy { get; set; }

    public bool Covers(DateTime date) =>
        EffectiveFrom <= date && (EffectiveTo is null || date <= EffectiveTo);
}

/// <summary>
/// One code's validity inside one release: valid from <see cref="EffectiveFrom"/>
/// through <see cref="EffectiveTo"/> (inclusive), both kept inside the release's window.
/// </summary>
[BsonIgnoreExtraElements]
public class CodeValidityPeriod
{
    public string Id { get; set; } = string.Empty;
    public string System { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Display { get; set; } = string.Empty;

    /// <summary>
    /// False for an ICD-10 header (category) code, which is in the code set but
    /// cannot be billed; a claim needs a more specific code.
    /// </summary>
    public bool Billable { get; set; } = true;

    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string ReleaseId { get; set; } = string.Empty;

    public bool Covers(DateTime date) =>
        EffectiveFrom <= date && (EffectiveTo is null || date <= EffectiveTo);
}

/// <summary>One parsed row of a release file, before it is placed in a release window.</summary>
public sealed record ParsedCode(
    string Code,
    string Display,
    bool Billable = true,
    DateTime? EffectiveFrom = null,
    DateTime? EffectiveTo = null);

/// <summary>Validity answers.</summary>
public static class CodeValidityStatus
{
    public const string Valid = "valid";
    public const string Invalid = "invalid";

    /// <summary>Not checked: no release covers the date, or the system has no code set loaded.</summary>
    public const string Unknown = "unknown";
}

/// <summary>Why a code is invalid or unknown. Stable strings that callers map to edits and CARCs.</summary>
public static class CodeValidityReason
{
    public const string NotInCodeSet = "not-in-code-set";
    public const string NotYetEffective = "not-yet-effective";
    public const string Terminated = "terminated";
    public const string NotBillable = "not-billable";
    public const string NoReleaseForDate = "no-release-for-date";
    public const string CodeSystemNotLoaded = "code-system-not-loaded";
}

public class CodeValidityRequest
{
    /// <summary>Code system URI (FHIR's <c>url</c> parameter).</summary>
    public string System { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;

    /// <summary>Date of service the code must be valid on. Defaults to today (UTC).</summary>
    public DateOnly? Date { get; set; }
}

/// <summary>
/// Compact answer for CodeSystem/$validate-code with a date. <see cref="Result"/>
/// is true or false when the code was checked and null when it could not be
/// (<see cref="Status"/> "unknown"); a caller must not treat unknown as invalid.
/// </summary>
public class CodeValidityResponse
{
    public bool? Result { get; set; }
    public string Status { get; set; } = CodeValidityStatus.Unknown;
    public string System { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public string? Display { get; set; }

    /// <summary>A <see cref="CodeValidityReason"/> value when not valid.</summary>
    public string? Reason { get; set; }

    /// <summary>Plain-language explanation, for a work queue or a shadow report.</summary>
    public string? Message { get; set; }

    /// <summary>The code's own validity window that the answer rests on, when there is one.</summary>
    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }

    /// <summary>The release that covers the date (for example "FY2026").</summary>
    public string? ReleaseVersion { get; set; }
}

public class CodeSetLoadResult
{
    public bool Success { get; set; }
    public string? ReleaseId { get; set; }
    public string System { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public int CodesLoaded { get; set; }
    public int RowsSkipped { get; set; }

    /// <summary>Releases this load ended early or replaced, as "version: what happened".</summary>
    public List<string> AdjustedReleases { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}
