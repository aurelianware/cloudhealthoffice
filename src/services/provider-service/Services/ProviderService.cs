using System.Text.Json;
using ProviderService.Models;
using ProviderService.Repositories;

namespace ProviderService.Services;

/// <summary>
/// Lifecycle façade over <see cref="IProviderRepository"/> for the provider
/// version chain. Responsibilities split with the repository:
///
/// <list type="bullet">
///   <item>The repository persists rows and enforces the
///         "Active is read-only" guard at write time.</item>
///   <item>The service layer assigns identity (ULID, version number,
///         predecessor pointer), applies state transitions and
///         timestamps before calling the repo, appends the audit
///         transition, and emits the version event.</item>
/// </list>
/// </summary>
public interface IProviderVersioningService
{
    /// <summary>
    /// Persist <paramref name="draft"/> as a brand-new genesis Draft (no
    /// predecessor) for a new <c>ProviderId</c>. Sets identity fields.
    /// </summary>
    Task<Provider> CreateDraftAsync(Provider draft, string actorId);

    /// <summary>
    /// Move a Draft into <c>Active</c>. If a current head version exists
    /// for the same provider (Active, Suspended, or Terminated), atomically
    /// supersedes it. Emits <c>ProviderVersionActivated</c> and (when
    /// applicable) <c>ProviderVersionSuperseded</c>. When the predecessor
    /// is Suspended or Terminated, also emits <c>ProviderVersionReactivated</c>.
    /// </summary>
    Task<Provider> ActivateVersionAsync(string providerId, string versionId, string actorId);

    /// <summary>
    /// Clone the latest Active version of <paramref name="providerId"/>
    /// into a new Draft (next <c>VersionNumber</c>, predecessor pointer
    /// to the source). The Draft is mutable until activated.
    /// </summary>
    Task<Provider> AmendActiveProviderAsync(string providerId, string actorId);

    /// <summary>
    /// Move the latest Active version into <c>Suspended</c>. Same VersionId
    /// remains addressable; the row is mutated in place. Emits
    /// <c>ProviderVersionSuspended</c>.
    /// </summary>
    Task<Provider> SuspendVersionAsync(string providerId, string versionId, string reason, string actorId);

    /// <summary>
    /// Permanently terminate the latest Active or Suspended version. No
    /// successor is created. Emits <c>ProviderVersionTerminated</c>.
    /// Reactivating a terminated provider goes through
    /// <see cref="ReactivateProviderAsync"/>.
    /// </summary>
    Task<Provider> TerminateVersionAsync(string providerId, string versionId, string reason, string actorId);

    /// <summary>
    /// Lift the chain head out of Suspended or Terminated by creating a
    /// new Active version cloned from the suspended/terminated head and
    /// superseding it. Emits both <c>ProviderVersionActivated</c> and
    /// <c>ProviderVersionReactivated</c>.
    /// </summary>
    Task<Provider> ReactivateProviderAsync(string providerId, string actorId);

    /// <summary>Newest-first list of all versions for a provider, paginated.</summary>
    Task<(IReadOnlyList<Provider> Items, string? ContinuationToken)> ListVersionsAsync(
        string providerId, int pageSize, string? continuationToken);

    /// <summary>Look up a single version.</summary>
    Task<Provider?> GetVersionAsync(string providerId, string versionId);
}

public class ProviderVersioningService : IProviderVersioningService
{
    private static readonly JsonSerializerOptions _cloneOpts = new(JsonSerializerDefaults.Web);

    private readonly IProviderRepository _repository;
    private readonly IProviderTransitionRepository _transitions;
    private readonly IProviderVersionEventPublisher _events;
    private readonly ILogger<ProviderVersioningService> _logger;

    public ProviderVersioningService(
        IProviderRepository repository,
        IProviderTransitionRepository transitions,
        IProviderVersionEventPublisher events,
        ILogger<ProviderVersioningService> logger)
    {
        _repository = repository;
        _transitions = transitions;
        _events = events;
        _logger = logger;
    }

    public async Task<Provider> CreateDraftAsync(Provider draft, string actorId)
    {
        if (string.IsNullOrEmpty(draft.Id)) draft.Id = Guid.NewGuid().ToString();
        // Genesis draft: chain key (ProviderId) defaults to the doc id so
        // legacy single-row chains and the v1 row use the same identifier.
        if (string.IsNullOrEmpty(draft.ProviderId)) draft.ProviderId = draft.Id;
        draft.VersionId = ProviderVersionId.NewId();
        draft.VersionNumber = 1;
        draft.VersionState = ProviderVersionState.Draft;
        draft.PredecessorVersionId = null;
        draft.ActivatedAt = null;
        draft.ActivatedBy = null;
        draft.SuspendedAt = null;
        draft.SuspensionReason = null;
        draft.SupersededAt = null;
        draft.SupersededByVersionId = null;
        draft.TerminationDate = null;
        draft.TerminationReason = null;
        // Status mirrors VersionState — drafts are Pending in the legacy enum.
        draft.Status = ProviderStatus.Pending;
        draft.CreatedBy = string.IsNullOrEmpty(draft.CreatedBy) ? actorId : draft.CreatedBy;
        draft.CreatedDate = DateTime.UtcNow;
        draft.LastUpdatedDate = DateTime.UtcNow;
        draft.LastUpdatedBy = actorId;

        return await _repository.CreateDraftAsync(draft);
    }

    public async Task<Provider> ActivateVersionAsync(string providerId, string versionId, string actorId)
    {
        var draft = await _repository.GetVersionAsync(providerId, versionId)
            ?? throw new ProviderVersionStateException(providerId, versionId, ProviderVersionState.Draft,
                $"Version {versionId} not found") { IsNotFound = true };

        if (draft.VersionState != ProviderVersionState.Draft)
        {
            throw new ProviderVersionStateException(providerId, versionId, draft.VersionState,
                $"Version {versionId} is {draft.VersionState}; only Draft versions can be activated.");
        }

        // Identify the chain head: the highest-VersionNumber non-Draft
        // row, regardless of its state. Active heads are the common path;
        // Suspended / Terminated heads are the reactivation path.
        var (items, _) = await _repository.ListVersionsAsync(providerId, 50, null);
        var predecessor = items
            .Where(p => p.VersionId != draft.VersionId && p.VersionState != ProviderVersionState.Draft)
            .OrderByDescending(p => p.VersionNumber)
            .FirstOrDefault();

        // Optimistic-concurrency guard: the draft's predecessor pointer
        // and version number must match the chain head. Mirrors
        // BenefitPlanService's PublishVersionAsync invariants.
        var expectedPredecessor = predecessor?.VersionId;
        if (draft.PredecessorVersionId != expectedPredecessor)
        {
            throw new ProviderVersionStateException(providerId, versionId, draft.VersionState,
                $"Draft predecessor '{draft.PredecessorVersionId ?? "<none>"}' does not match the current head version '{expectedPredecessor ?? "<none>"}'. Re-amend from the latest version and retry.");
        }
        var expectedNumber = (predecessor?.VersionNumber ?? 0) + 1;
        if (draft.VersionNumber != expectedNumber)
        {
            throw new ProviderVersionStateException(providerId, versionId, draft.VersionState,
                $"Draft version number {draft.VersionNumber} does not match the expected next number {expectedNumber}. Re-amend from the latest version and retry.");
        }

        var now = DateTime.UtcNow;
        draft.VersionState = ProviderVersionState.Active;
        draft.ActivatedAt = now;
        draft.ActivatedBy = actorId;
        draft.Status = ProviderStatus.Active;
        draft.LastUpdatedBy = actorId;

        var supersededFromState = predecessor?.VersionState;
        if (predecessor != null)
        {
            predecessor.VersionState = ProviderVersionState.Superseded;
            predecessor.SupersededAt = now;
            predecessor.SupersededByVersionId = draft.VersionId;
            predecessor.Status = ProviderStatus.Inactive;
        }

        await _repository.ActivateAndSupersedeAsync(draft, predecessor);

        await _transitions.AppendAsync(new ProviderTransition
        {
            TenantId = draft.TenantId,
            ProviderId = providerId,
            FromVersionId = predecessor?.VersionId,
            ToVersionId = draft.VersionId,
            TransitionType = predecessor == null ? ProviderTransitionType.Activate : ProviderTransitionType.Supersede,
            OccurredAt = now,
            ActorId = actorId
        });

        await _events.PublishVersionActivatedAsync(draft, actorId, correlationId: null);
        if (predecessor != null)
        {
            await _events.PublishVersionSupersededAsync(predecessor, draft, reason: null, actorId, correlationId: null);

            // If the predecessor was Suspended or Terminated, this is
            // also a reactivation — emit the dedicated event so
            // observability matches the state-machine intent.
            if (supersededFromState == ProviderVersionState.Suspended ||
                supersededFromState == ProviderVersionState.Terminated)
            {
                await _events.PublishVersionReactivatedAsync(draft, predecessor, actorId, correlationId: null);
                await _transitions.AppendAsync(new ProviderTransition
                {
                    TenantId = draft.TenantId,
                    ProviderId = providerId,
                    FromVersionId = predecessor.VersionId,
                    ToVersionId = draft.VersionId,
                    TransitionType = ProviderTransitionType.Reactivate,
                    OccurredAt = now,
                    ActorId = actorId
                });
            }
        }

        return draft;
    }

    public async Task<Provider> AmendActiveProviderAsync(string providerId, string actorId)
    {
        var current = await _repository.GetLatestActiveAsync(providerId, DateTime.UtcNow)
            ?? throw new ProviderVersionStateException(providerId, string.Empty, ProviderVersionState.Active,
                $"No Active version of provider {providerId} exists to amend") { IsNotFound = true };

        var draft = Clone(current);
        // The new draft is a separate document with a fresh per-row Id;
        // ProviderId is preserved so the chain stays addressable under
        // the same persistent key existing consumers already use.
        draft.Id = Guid.NewGuid().ToString();
        draft.ProviderId = current.ProviderId;
        draft.VersionId = ProviderVersionId.NewId();
        draft.VersionNumber = current.VersionNumber + 1;
        draft.VersionState = ProviderVersionState.Draft;
        draft.PredecessorVersionId = current.VersionId;
        draft.ActivatedAt = null;
        draft.ActivatedBy = null;
        draft.SuspendedAt = null;
        draft.SuspensionReason = null;
        draft.SupersededAt = null;
        draft.SupersededByVersionId = null;
        draft.TerminationDate = null;
        draft.TerminationReason = null;
        draft.Status = ProviderStatus.Pending;
        draft.CreatedBy = actorId;
        draft.CreatedDate = DateTime.UtcNow;
        draft.LastUpdatedDate = DateTime.UtcNow;
        draft.LastUpdatedBy = actorId;

        var stored = await _repository.CreateDraftAsync(draft);

        await _transitions.AppendAsync(new ProviderTransition
        {
            TenantId = current.TenantId,
            ProviderId = providerId,
            FromVersionId = current.VersionId,
            ToVersionId = stored.VersionId,
            TransitionType = ProviderTransitionType.Amend,
            OccurredAt = DateTime.UtcNow,
            ActorId = actorId
        });

        return stored;
    }

    public async Task<Provider> SuspendVersionAsync(string providerId, string versionId, string reason, string actorId)
    {
        var target = await _repository.GetVersionAsync(providerId, versionId)
            ?? throw new ProviderVersionStateException(providerId, versionId, ProviderVersionState.Active,
                $"Version {versionId} not found") { IsNotFound = true };

        if (target.VersionState != ProviderVersionState.Active)
        {
            throw new ProviderVersionStateException(providerId, versionId, target.VersionState,
                $"Version {versionId} is {target.VersionState}; only Active versions can be suspended.");
        }

        var now = DateTime.UtcNow;
        target.VersionState = ProviderVersionState.Suspended;
        target.SuspendedAt = now;
        target.SuspensionReason = reason;
        target.Status = ProviderStatus.Inactive;
        target.LastUpdatedBy = actorId;

        await _repository.ReplaceVersionRowAsync(target);

        await _transitions.AppendAsync(new ProviderTransition
        {
            TenantId = target.TenantId,
            ProviderId = providerId,
            FromVersionId = versionId,
            ToVersionId = versionId,
            TransitionType = ProviderTransitionType.Suspend,
            Reason = reason,
            OccurredAt = now,
            ActorId = actorId
        });

        await _events.PublishVersionSuspendedAsync(target, reason, actorId, correlationId: null);

        return target;
    }

    public async Task<Provider> TerminateVersionAsync(string providerId, string versionId, string reason, string actorId)
    {
        var target = await _repository.GetVersionAsync(providerId, versionId)
            ?? throw new ProviderVersionStateException(providerId, versionId, ProviderVersionState.Active,
                $"Version {versionId} not found") { IsNotFound = true };

        if (target.VersionState != ProviderVersionState.Active &&
            target.VersionState != ProviderVersionState.Suspended)
        {
            throw new ProviderVersionStateException(providerId, versionId, target.VersionState,
                $"Version {versionId} is {target.VersionState}; only Active or Suspended versions can be terminated.");
        }

        var now = DateTime.UtcNow;
        target.VersionState = ProviderVersionState.Terminated;
        target.TerminationDate = now;
        target.TerminationReason = reason;
        target.Status = ProviderStatus.Terminated;
        target.LastUpdatedBy = actorId;

        await _repository.ReplaceVersionRowAsync(target);

        await _transitions.AppendAsync(new ProviderTransition
        {
            TenantId = target.TenantId,
            ProviderId = providerId,
            FromVersionId = versionId,
            ToVersionId = null,
            TransitionType = ProviderTransitionType.Terminate,
            Reason = reason,
            EffectiveDate = now,
            OccurredAt = now,
            ActorId = actorId
        });

        await _events.PublishVersionTerminatedAsync(target, reason, actorId, correlationId: null);

        return target;
    }

    public async Task<Provider> ReactivateProviderAsync(string providerId, string actorId)
    {
        // Find the most recent Suspended or Terminated head. We page
        // through the chain newest-first until we find a non-Active,
        // non-Draft, non-Superseded row to use as the predecessor.
        var (items, _) = await _repository.ListVersionsAsync(providerId, 50, null);
        var head = items.FirstOrDefault(p =>
            p.VersionState == ProviderVersionState.Suspended ||
            p.VersionState == ProviderVersionState.Terminated);

        if (head == null)
        {
            throw new ProviderVersionStateException(providerId, string.Empty, ProviderVersionState.Active,
                $"No Suspended or Terminated head found for provider {providerId} to reactivate") { IsNotFound = true };
        }

        // Build a fresh draft cloned from the head, then take it
        // through the standard activate path so the supersede + event
        // emission stays consistent with amend → activate.
        var draft = Clone(head);
        draft.Id = Guid.NewGuid().ToString();
        draft.ProviderId = head.ProviderId;
        draft.VersionId = ProviderVersionId.NewId();
        draft.VersionNumber = head.VersionNumber + 1;
        draft.VersionState = ProviderVersionState.Draft;
        draft.PredecessorVersionId = head.VersionId;
        draft.ActivatedAt = null;
        draft.ActivatedBy = null;
        draft.SuspendedAt = null;
        draft.SuspensionReason = null;
        draft.SupersededAt = null;
        draft.SupersededByVersionId = null;
        draft.TerminationDate = null;
        draft.TerminationReason = null;
        draft.Status = ProviderStatus.Pending;
        draft.CreatedBy = actorId;
        draft.CreatedDate = DateTime.UtcNow;
        draft.LastUpdatedDate = DateTime.UtcNow;
        draft.LastUpdatedBy = actorId;

        var stored = await _repository.CreateDraftAsync(draft);

        await _transitions.AppendAsync(new ProviderTransition
        {
            TenantId = head.TenantId,
            ProviderId = providerId,
            FromVersionId = head.VersionId,
            ToVersionId = stored.VersionId,
            TransitionType = ProviderTransitionType.Amend,
            OccurredAt = DateTime.UtcNow,
            ActorId = actorId
        });

        return await ActivateVersionAsync(providerId, stored.VersionId, actorId);
    }

    public Task<(IReadOnlyList<Provider> Items, string? ContinuationToken)> ListVersionsAsync(
        string providerId, int pageSize, string? continuationToken)
        => _repository.ListVersionsAsync(providerId, pageSize, continuationToken);

    public Task<Provider?> GetVersionAsync(string providerId, string versionId)
        => _repository.GetVersionAsync(providerId, versionId);

    /// <summary>
    /// Deep-clones a Provider row through a JSON round-trip so amend /
    /// reactivate paths produce a fully independent draft (the Mongo
    /// driver returns shared object refs that would otherwise mutate the
    /// stored predecessor).
    /// </summary>
    private static Provider Clone(Provider src)
        => JsonSerializer.Deserialize<Provider>(JsonSerializer.Serialize(src, _cloneOpts), _cloneOpts)!;
}
