using MemberService.Controllers;

namespace MemberService.Services;

/// <summary>
/// Typed client for the coverage-service. PCP lookup/assignment and coverage history
/// are authoritative in coverage-service — this client is how member-service delegates.
/// </summary>
public interface ICoverageServiceClient
{
    Task<MemberPcpResponse> GetPcpAsync(string tenantId, string memberId, CancellationToken ct = default);

    Task<MemberPcpResponse> AssignPcpAsync(
        string tenantId,
        string memberId,
        AssignPcpRequest request,
        CancellationToken ct = default);

    Task<IReadOnlyList<CoverageHistoryEvent>> GetCoverageHistoryAsync(
        string tenantId,
        string memberId,
        CancellationToken ct = default);

    Task TerminateCoverageAsync(
        string tenantId,
        string memberId,
        TerminateMemberRequest request,
        CancellationToken ct = default);
}

/// <summary>Typed client for the enrollment-import-service (834 transaction history + event stream).</summary>
public interface IEnrollmentImportServiceClient
{
    Task<IReadOnlyList<Enrollment834Record>> Get834TransactionsAsync(
        string tenantId,
        string memberId,
        CancellationToken ct = default);

    /// <summary>
    /// List enrollment events for a member, newest first. Optional filters apply at the
    /// downstream service. Continuation token is opaque and round-trips to the caller.
    /// </summary>
    Task<EnrollmentEventListResponse> GetEnrollmentEventsAsync(
        string tenantId,
        string memberId,
        string? type = null,
        DateTime? from = null,
        DateTime? to = null,
        int limit = 50,
        string? continuationToken = null,
        CancellationToken ct = default);
}

/// <summary>Typed client for the accumulator service (deductible + OOP balances).</summary>
public interface IAccumulatorServiceClient
{
    Task<MemberAccumulatorsResponse> GetAccumulatorsAsync(
        string tenantId,
        string memberId,
        CancellationToken ct = default);
}
