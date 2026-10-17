namespace CloudHealthOffice.Portal.Services;

public interface IMemberService
{
    Task<List<MemberSummary>> SearchMembersAsync(string searchTerm);
    Task<MemberDetails?> GetMemberByIdAsync(string memberId);
    Task<MemberPcp?> GetMemberPcpAsync(string memberId);

    /// <summary>
    /// Assign a PCP. Returns a populated <see cref="PcpAssignmentOutcome"/> — on
    /// validation failure (400), <see cref="PcpAssignmentOutcome.ValidationError"/>
    /// is set with the structured error code from coverage-service.
    /// </summary>
    Task<PcpAssignmentOutcome> AssignPcpAsync(AssignPcpRequest request);

    Task<List<PcpAssignmentHistoryItem>> GetMemberPcpHistoryAsync(string memberId);
    Task<List<CoverageHistoryEvent>> GetCoverageHistoryAsync(string memberId);
    Task<List<Enrollment834Record>> GetMember834TransactionsAsync(string memberId);
    Task<EnrollmentEventPage> GetEnrollmentEventsAsync(
        string memberId,
        EnrollmentEventFilter filter);
    Task TerminateEnrollmentAsync(TerminateEnrollmentRequest request);
    Task<MemberAccumulators> GetAccumulatorsAsync(string memberId);
}
