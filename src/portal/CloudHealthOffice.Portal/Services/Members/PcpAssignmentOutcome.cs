namespace CloudHealthOffice.Portal.Services;

public class PcpAssignmentOutcome
{
    public MemberPcp? Pcp { get; set; }
    public PcpValidationProblem? ValidationError { get; set; }
    public bool IsSuccess => Pcp != null;
}
