using System.Collections.Concurrent;
using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Services;

namespace PersonalRepresentativeService.Tests.Fakes;

/// <summary>
/// Captures every publish call so controller tests can assert
/// "N events published, exactly these fields, in this order".
/// </summary>
public sealed class RecordingPersonalRepEventPublisher : IPersonalRepEventPublisher
{
    public readonly ConcurrentQueue<StatusCall> StatusCalls = new();
    public readonly ConcurrentQueue<AssociationCall> AssociationCalls = new();

    public Task PublishStatusChangedAsync(
        PersonalRepresentative rep,
        PersonalRepStatus? fromStatus,
        PersonalRepStatus toStatus,
        IReadOnlyList<string> associatedMemberIds,
        string actor,
        string? correlationId,
        CancellationToken ct = default)
    {
        StatusCalls.Enqueue(new StatusCall(
            PersonalRepId: rep.Id,
            TenantId: rep.TenantId,
            FromStatus: fromStatus,
            ToStatus: toStatus,
            AssociatedMemberIds: associatedMemberIds.ToList(),
            Actor: actor,
            CorrelationId: correlationId));
        return Task.CompletedTask;
    }

    public Task PublishAssociationChangedAsync(
        PersonalRepresentative rep,
        PersonalRepAssociation association,
        PersonalRepEventType eventType,
        string actor,
        string? correlationId,
        CancellationToken ct = default)
    {
        AssociationCalls.Enqueue(new AssociationCall(
            PersonalRepId: rep.Id,
            TenantId: rep.TenantId,
            MemberId: association.MemberId,
            PairId: association.PairId,
            EventType: eventType,
            Actor: actor,
            CorrelationId: correlationId));
        return Task.CompletedTask;
    }

    public sealed record StatusCall(
        string PersonalRepId,
        string TenantId,
        PersonalRepStatus? FromStatus,
        PersonalRepStatus ToStatus,
        List<string> AssociatedMemberIds,
        string Actor,
        string? CorrelationId);

    public sealed record AssociationCall(
        string PersonalRepId,
        string TenantId,
        string MemberId,
        string PairId,
        PersonalRepEventType EventType,
        string Actor,
        string? CorrelationId);
}
