using System;
using System.Threading;
using System.Threading.Tasks;
using MemberService.Models;
using Microsoft.Extensions.Logging;

namespace MemberService.Services;

/// <summary>
/// Bridges legacy <c>Member.SubscriberMemberId</c> / <c>Member.RelationshipCode</c> writes
/// (from the 834 enrollment-import path) onto the new symmetric-graph model, creating a
/// <see cref="FamilyRelationship"/> pair when a dependent is written.
///
/// <para>
/// <b>Idempotency:</b> calling <see cref="EnsureRelationshipAsync"/> repeatedly for the same
/// (subscriber, dependent) pair is a no-op once the active pair exists. Safe to re-run
/// against the same member (e.g. replayed 834 batches, backfill dry-runs).
/// </para>
///
/// <para>
/// <b>Same-tenant only:</b> the shim silently skips relationships where the subscriber
/// lookup returns null in the dependent's tenant. That is the correct behavior for 834
/// imports, which always enroll both parties in the same tenant.
/// </para>
/// </summary>
public interface IRelationshipShim
{
    /// <summary>
    /// Create (or no-op) the symmetric relationship pair implied by a legacy
    /// <c>Member.SubscriberMemberId</c> / <c>Member.RelationshipCode</c> write.
    /// </summary>
    Task EnsureRelationshipAsync(Member dependent, string? actor, CancellationToken ct = default);
}

public sealed class RelationshipShim : IRelationshipShim
{
    private readonly IFamilyRelationshipService _service;
    private readonly ILogger<RelationshipShim> _logger;

    public RelationshipShim(IFamilyRelationshipService service, ILogger<RelationshipShim> logger)
    {
        _service = service;
        _logger = logger;
    }

    public async Task EnsureRelationshipAsync(Member dependent, string? actor, CancellationToken ct = default)
    {
        if (dependent == null) throw new ArgumentNullException(nameof(dependent));

        if (dependent.IsSubscriber) return;
#pragma warning disable CS0618 // The shim's entire purpose is to migrate the obsolete FK to the graph.
        var legacySubscriberId = dependent.SubscriberMemberId;
        if (string.IsNullOrWhiteSpace(legacySubscriberId)) return;

        var code = !string.IsNullOrWhiteSpace(dependent.RelationshipCode) &&
                   FamilyRelationshipCodes.IsValid(dependent.RelationshipCode!)
            ? dependent.RelationshipCode!
            : RelationshipCodes.Child;
#pragma warning restore CS0618

        var req = new CreateFamilyRelationshipRequest
        {
            SubjectMemberId = dependent.MemberId,
            RelatedMemberId = legacySubscriberId!,
            RelationshipCode = code,
            StartDate = dependent.EffectiveDate == default ? DateTime.UtcNow : dependent.EffectiveDate,
            EndDate = dependent.TerminationDate,
            IsCustodial = false,
        };

        try
        {
            await _service.CreateAsync(dependent.TenantId, req, actor ?? "834-import", ct);
        }
        catch (FamilyRelationshipValidationException ex) when (
            ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "RelationshipShim no-op: pair already exists for {Dependent} → {Subscriber}",
                dependent.MemberId, legacySubscriberId);
        }
        catch (FamilyRelationshipValidationException ex)
        {
            // Log and swallow — the shim must never block a legitimate 834 write.
            // Graph-model completeness can be reconciled by the backfill tool.
            _logger.LogWarning(ex,
                "RelationshipShim: skipped edge for {Dependent} → {Subscriber}: {Reason}",
                dependent.MemberId, legacySubscriberId, ex.Message);
        }
    }
}
