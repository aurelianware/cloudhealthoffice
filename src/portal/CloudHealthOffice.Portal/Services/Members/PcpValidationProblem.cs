namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Mirror of coverage-service's <c>PcpValidationError</c>. <see cref="Code"/>
/// values are stable — see docs/architecture/pcp-assignment.md "Validation
/// ladder" for the canonical list.
/// </summary>
public class PcpValidationProblem
{
    public string Code { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = "Error";
}
