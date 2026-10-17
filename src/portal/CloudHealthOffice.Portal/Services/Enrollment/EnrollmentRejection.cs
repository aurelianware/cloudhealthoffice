namespace CloudHealthOffice.Portal.Services;

public class EnrollmentRejection
{
    public string MemberId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorDescription { get; set; } = string.Empty;
    public string RawSegmentReference { get; set; } = string.Empty;
}
