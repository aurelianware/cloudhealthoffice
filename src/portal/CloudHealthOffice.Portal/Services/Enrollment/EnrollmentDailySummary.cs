namespace CloudHealthOffice.Portal.Services;

public class EnrollmentDailySummary
{
    public int FilesReceived { get; set; }
    public int TotalTransactions { get; set; }
    public int MembersAdded { get; set; }
    public int MembersTermed { get; set; }
    public int MembersChanged { get; set; }
    public int ErrorCount { get; set; }
}
