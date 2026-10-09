using AppealsService.Models;
using MongoDB.Driver;

namespace AppealsService.Repositories;

/// <summary>
/// The fields a status transition owns, and the only fields
/// <see cref="IAppealRepository.TransitionStatusAsync"/> writes.
/// </summary>
/// <remarks>
/// The controller builds a transition from a snapshot it read earlier.
/// Every other appeal field is written by some other endpoint (attachments
/// and their acknowledgments, notes, reviewer assignment, the deadline
/// extension, the overdue audit flag) and can change between that read and
/// the transition write, so a transition never copies them from the
/// snapshot. Each repository applies just these fields to the persisted
/// row: Mongo as a <c>$set</c> filtered on the expected status, Cosmos onto
/// a fresh ETag-pinned read, the in-memory fake onto the stored copy.
///
/// The status filter is what makes writing the closure and decision
/// fields safe: only a transition writes them, and a concurrent transition
/// moves the status, so the filter refuses the second writer. A field
/// added to a transition in the controller must be added here, or it is
/// silently not persisted.
/// </remarks>
public static class AppealStatusTransitionFields
{
    /// <summary>Copy the transition-owned fields from <paramref name="source"/> onto <paramref name="target"/>.</summary>
    public static void CopyTo(Appeal source, Appeal target)
    {
        target.Status = source.Status;
        target.UpdatedAt = source.UpdatedAt;
        target.UpdatedBy = source.UpdatedBy;
        target.ClosureReasonCode = source.ClosureReasonCode;
        target.ClosedAt = source.ClosedAt;
        target.ClosedBy = source.ClosedBy;
        target.Decision = source.Decision;
        target.DecisionDate = source.DecisionDate;
    }

    /// <summary>A Mongo update that sets the transition-owned fields from <paramref name="source"/> and nothing else.</summary>
    public static UpdateDefinition<Appeal> ToMongoUpdate(Appeal source) =>
        Builders<Appeal>.Update
            .Set(a => a.Status, source.Status)
            .Set(a => a.UpdatedAt, source.UpdatedAt)
            .Set(a => a.UpdatedBy, source.UpdatedBy)
            .Set(a => a.ClosureReasonCode, source.ClosureReasonCode)
            .Set(a => a.ClosedAt, source.ClosedAt)
            .Set(a => a.ClosedBy, source.ClosedBy)
            .Set(a => a.Decision, source.Decision)
            .Set(a => a.DecisionDate, source.DecisionDate);
}
