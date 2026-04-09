namespace ClaimsService.EDI.Florida.Models;

/// <summary>
/// Thrown when a claim fails FMMIS Companion Guide compliance validation.
/// Contains all validation errors so callers can surface them to the submitter
/// in a single pass rather than one-at-a-time.
/// </summary>
public class FmmisValidationException : Exception
{
    /// <summary>
    /// Individual validation errors describing each Companion Guide violation.
    /// </summary>
    public IReadOnlyList<string> Errors { get; }

    public FmmisValidationException(IEnumerable<string> errors)
        : base($"FMMIS validation failed with {errors.Count()} error(s): {string.Join("; ", errors)}")
    {
        Errors = errors.ToList().AsReadOnly();
    }

    public FmmisValidationException(IEnumerable<string> errors, Exception innerException)
        : base($"FMMIS validation failed with {errors.Count()} error(s): {string.Join("; ", errors)}", innerException)
    {
        Errors = errors.ToList().AsReadOnly();
    }
}
