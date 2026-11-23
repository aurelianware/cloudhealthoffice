using MongoDB.Bson.Serialization.Attributes;

namespace ClaimsService.Services.Adjudication.CodeValidity;

/// <summary>
/// A diagnosis or procedure code on a claim that was not valid on its date of
/// service, as terminology-service's dated CMS code sets answered. Kept on the
/// adjudication context and copied onto a shadow result.
/// </summary>
[BsonIgnoreExtraElements]
public class CodeValidityFinding
{
    public const string DiagnosisKind = "Diagnosis";
    public const string ProcedureKind = "Procedure";

    /// <summary><see cref="DiagnosisKind"/> or <see cref="ProcedureKind"/>.</summary>
    public string Kind { get; set; } = DiagnosisKind;

    /// <summary>The line, for a line procedure code; null for a claim-level code.</summary>
    public int? LineNumber { get; set; }

    /// <summary>ICD-10-CM, ICD-10-PCS or HCPCS.</summary>
    public string CodeSystem { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public DateTime DateOfService { get; set; }

    /// <summary>terminology-service's reason: not-in-code-set, not-yet-effective, terminated, not-billable.</summary>
    public string Reason { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? ReleaseVersion { get; set; }

    /// <summary>"Line 2 HCPCS J9999: terminated" or "Diagnosis ICD-10-CM E11: not-billable".</summary>
    public string Label() =>
        (LineNumber is { } line ? $"Line {line} " : $"{Kind} ") + $"{CodeSystem} {Code}: {Reason}";
}
