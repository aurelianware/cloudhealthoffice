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
    public IReadOnlyList<string> ValidationErrors { get; }

    public FmmisValidationException(IEnumerable<string> validationErrors)
        : base($"FMMIS validation failed with {validationErrors.Count()} error(s): " +
               string.Join("; ", validationErrors))
    {
        ValidationErrors = validationErrors.ToList().AsReadOnly();
    }

    public FmmisValidationException(IEnumerable<string> validationErrors, Exception innerException)
        : base($"FMMIS validation failed with {validationErrors.Count()} error(s): " +
               string.Join("; ", validationErrors), innerException)
    {
        ValidationErrors = validationErrors.ToList().AsReadOnly();
    }
}
