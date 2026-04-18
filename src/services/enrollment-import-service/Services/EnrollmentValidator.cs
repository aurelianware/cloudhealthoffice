using EnrollmentImportService.Models;

namespace EnrollmentImportService.Services;

public interface IEnrollmentValidator
{
    EnrollmentValidationResult Validate(MemberEnrollment? enrollment);
}

/// <summary>
/// Structured validation error. <see cref="Field"/> uses dot-notation paths so callers
/// can map errors to field-keyed envelopes (e.g. <c>ValidationProblemDetails</c>).
/// <see cref="Code"/> is a stable machine-readable identifier; <see cref="Message"/>
/// is human-facing.
/// </summary>
public sealed record EnrollmentValidationError(string Field, string Code, string Message);

public sealed record EnrollmentValidationResult(bool IsValid, IReadOnlyList<EnrollmentValidationError> Errors)
{
    public static EnrollmentValidationResult Ok() =>
        new(true, Array.Empty<EnrollmentValidationError>());

    public static EnrollmentValidationResult Fail(IEnumerable<EnrollmentValidationError> errors) =>
        new(false, errors.ToList());

    /// <summary>Convenience for log lines and free-text envelopes.</summary>
    public IEnumerable<string> ToFlatStrings() =>
        Errors.Select(e => $"{e.Field}: {e.Message}");
}

/// <summary>
/// Shared validation for both 834-ingested and manually-entered enrollments. Keeping this
/// in one place ensures the manual API rejects exactly what the 834 pipeline would reject.
///
/// Errors are returned as structured <see cref="EnrollmentValidationError"/>s with field
/// paths and stable codes so callers can build their own error envelopes
/// (ValidationProblemDetails, audit log, RFC7807, etc.) without parsing free text.
/// </summary>
public sealed class EnrollmentValidator : IEnrollmentValidator
{
    private static readonly HashSet<string> AllowedMaintenance = new(StringComparer.Ordinal)
    {
        "001", // Change
        "021", // Addition
        "024", // Termination
        "025", // Reinstatement
        "030"  // Audit/Compare
    };

    private static readonly HashSet<string> AllowedRelationship = new(StringComparer.Ordinal)
    {
        "18", "01", "19", "G8", "17", "10"
    };

    private static readonly HashSet<string> AllowedBenefitStatus = new(StringComparer.Ordinal)
    {
        "A", "C", "T"
    };

    public EnrollmentValidationResult Validate(MemberEnrollment? enrollment)
    {
        if (enrollment is null)
        {
            return EnrollmentValidationResult.Fail(new[]
            {
                new EnrollmentValidationError("$", "enrollment.required", "enrollment payload is required")
            });
        }

        var errors = new List<EnrollmentValidationError>();

        if (string.IsNullOrWhiteSpace(enrollment.MaintenanceType))
            errors.Add(new("maintenanceType", "maintenanceType.required",
                "maintenanceType is required (021/001/024/025/030)"));
        else if (!AllowedMaintenance.Contains(enrollment.MaintenanceType))
            errors.Add(new("maintenanceType", "maintenanceType.unsupported",
                $"maintenanceType '{enrollment.MaintenanceType}' is not supported"));

        if (string.IsNullOrWhiteSpace(enrollment.BenefitStatus))
            errors.Add(new("benefitStatus", "benefitStatus.required",
                "benefitStatus is required (A/C/T)"));
        else if (!AllowedBenefitStatus.Contains(enrollment.BenefitStatus))
            errors.Add(new("benefitStatus", "benefitStatus.unsupported",
                $"benefitStatus '{enrollment.BenefitStatus}' is not supported"));

        if (string.IsNullOrWhiteSpace(enrollment.Relationship))
            errors.Add(new("relationship", "relationship.required",
                "relationship is required (18/01/19/...)"));
        else if (!AllowedRelationship.Contains(enrollment.Relationship))
            errors.Add(new("relationship", "relationship.unsupported",
                $"relationship '{enrollment.Relationship}' is not supported"));

        if (string.IsNullOrWhiteSpace(enrollment.SubscriberId))
            errors.Add(new("subscriberId", "subscriberId.required", "subscriberId is required"));

        if (enrollment.Demographics == null)
        {
            errors.Add(new("demographics", "demographics.required", "demographics is required"));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(enrollment.Demographics.FirstName))
                errors.Add(new("demographics.firstName", "demographics.firstName.required",
                    "demographics.firstName is required"));
            if (string.IsNullOrWhiteSpace(enrollment.Demographics.LastName))
                errors.Add(new("demographics.lastName", "demographics.lastName.required",
                    "demographics.lastName is required"));
        }

        if (enrollment.MaintenanceType == "024" && string.IsNullOrWhiteSpace(enrollment.TerminationDate))
            errors.Add(new("terminationDate", "terminationDate.requiredForTermination",
                "terminationDate is required for maintenanceType=024"));

        if (enrollment.MaintenanceType == "021"
            && string.IsNullOrWhiteSpace(enrollment.EnrollmentDate)
            && (enrollment.Coverage == null || enrollment.Coverage.Count == 0))
        {
            errors.Add(new("enrollmentDate", "enrollmentDate.requiredForAddition",
                "enrollmentDate or coverage[] is required for maintenanceType=021"));
        }

        return errors.Count == 0
            ? EnrollmentValidationResult.Ok()
            : EnrollmentValidationResult.Fail(errors);
    }
}
