using MemberService.Models;

namespace MemberService.Services;

/// <summary>
/// Publishes <see cref="MemberEvent"/>s. Authoritative store in this PR is the Cosmos
/// <c>member-events</c> container (<see cref="CosmosMemberEventPublisher"/>). Downstream
/// fan-out (bus publishers) can be layered via a decorator/composite without touching
/// call sites.
///
/// Payload rules:
///   - <see cref="MemberEventType.MemberCreated"/> MUST contain the full member snapshot.
///   - All other events SHOULD contain a diff of changed fields.
/// </summary>
public interface IMemberEventPublisher
{
    /// <summary>
    /// Append an event. Idempotent on <see cref="MemberEvent.EventId"/>; callers
    /// may safely retry. Populates <see cref="MemberEvent.Version"/>,
    /// <see cref="MemberEvent.PartitionKey"/>, and <see cref="MemberEvent.OccurredAt"/>
    /// if not set.
    /// </summary>
    Task<MemberEvent> PublishAsync(MemberEvent evt, CancellationToken ct = default);
}
