using CareManagementService.Models;

namespace CareManagementService.Services.Work;

/// <summary>A care activity, as the work queue needs to see it.</summary>
public sealed record CareWorkItemCommand
{
    public required string TenantId     { get; init; }
    public required string CaseId       { get; init; }
    public required string CaseNumber   { get; init; }
    public required string MemberId     { get; init; }
    public required CareActivity Activity { get; init; }
    public required CareProgram Program { get; init; }
    public required CareAcuity Acuity   { get; init; }
}

/// <summary>
/// Pushes a care activity into work-management-service, which already routes,
/// assigns and ages work for the rest of the platform.
///
/// Every method answers null rather than throwing when the queue is
/// unreachable. The activity is recorded on the case either way: the care
/// record is the system of record, and it does not depend on a work queue
/// being up.
/// </summary>
public interface ICareWorkItemGateway
{
    /// <summary>Queues the activity and returns the work item's id, or null if it could not be queued.</summary>
    Task<string?> EnqueueAsync(CareWorkItemCommand command, CancellationToken ct = default);

    /// <summary>Closes the work item behind an activity. Silent when there is nothing to close.</summary>
    Task CompleteAsync(string tenantId, string workItemId, string? outcome, CancellationToken ct = default);
}

/// <summary>
/// Used when no work-management-service is configured. Activities are still
/// recorded on the case; they simply do not appear in anyone's queue, which a
/// single-service deployment or a test does not need them to.
/// </summary>
public sealed class NullCareWorkItemGateway : ICareWorkItemGateway
{
    public Task<string?> EnqueueAsync(CareWorkItemCommand command, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task CompleteAsync(string tenantId, string workItemId, string? outcome, CancellationToken ct = default) =>
        Task.CompletedTask;
}
