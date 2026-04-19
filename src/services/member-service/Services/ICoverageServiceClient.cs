using MemberService.Controllers;

namespace MemberService.Services;

/// <summary>
/// Typed client for the coverage-service. PCP lookup/assignment and coverage history
/// are authoritative in coverage-service — this client is how member-service delegates.
/// </summary>
public interface ICoverageServiceClient
{
    Task<MemberPcpResponse> GetPcpAsync(string tenantId, string memberId, CancellationToken ct = default);

    /// <summary>
    /// Assign a PCP. Returns either a populated <see cref="MemberPcpResponse"/> or
    /// a structured <see cref="PcpValidationProblem"/> when coverage-service rejects
    /// the assignment with 400. 503/connectivity issues still throw
    /// <see cref="DownstreamUnavailableException"/>.
    /// </summary>
    Task<AssignPcpOutcome> AssignPcpAsync(
        string tenantId,
        string memberId,
        AssignPcpRequest request,
        CancellationToken ct = default);

    Task<IReadOnlyList<PcpAssignmentHistoryItem>> GetPcpHistoryAsync(
        string tenantId,
        string memberId,
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

/// <summary>
/// Result of a PCP assignment attempt. Exactly one of <see cref="Pcp"/> /
/// <see cref="ValidationError"/> is populated.
/// </summary>
public sealed class AssignPcpOutcome
{
    public MemberPcpResponse? Pcp { get; init; }
    public PcpValidationProblem? ValidationError { get; init; }
    public bool IsSuccess => Pcp != null;
}

/// <summary>
/// Structured PCP validation failure surfaced from coverage-service. Matches the
/// error contract documented in docs/architecture/pcp-assignment.md.
/// </summary>
public sealed class PcpValidationProblem
{
    public string Code { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = "Error";
}

/// <summary>One row in a member's PCP assignment history (mirror of coverage-service shape).</summary>
public sealed class PcpAssignmentHistoryItem
{
    public string Id { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string CoverageId { get; set; } = string.Empty;
    public string ProviderNpi { get; set; } = string.Empty;
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? AssignmentReason { get; set; }
    public string AssignmentSource { get; set; } = "MemberChoice";
    public string NetworkStatusAtAssignment { get; set; } = "Unknown";
    public string? AssignedBy { get; set; }
    public DateTime CreatedDate { get; set; }
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
