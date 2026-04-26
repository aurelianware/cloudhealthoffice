using BenefitPlanService.Models;
using BenefitPlanService.Repositories;

namespace BenefitPlanService.Tests.Fakes;

/// <summary>
/// In-memory fake of <see cref="IBenefitPlanRepository"/> with full
/// version-chain semantics. Used by service- and controller-level tests
/// to avoid requiring a live Mongo/Cosmos.
/// </summary>
public sealed class InMemoryBenefitPlanRepository : IBenefitPlanRepository
{
    private readonly List<BenefitPlan> _docs = new();
    public IReadOnlyList<BenefitPlan> Docs => _docs;

    /// <summary>Set true to simulate a transactional batch failure.</summary>
    public bool FailNextPublish { get; set; }

    public Task<BenefitPlan?> GetByIdAsync(string id, string tenantId)
        => Task.FromResult(_docs.FirstOrDefault(d => d.Id == id && d.TenantId == tenantId));

    public Task<BenefitPlan?> GetByPlanIdAsync(string planId, string tenantId)
        => GetLatestPublishedAsync(planId, tenantId, DateTime.UtcNow);

    public Task<BenefitPlan?> GetLatestPublishedAsync(string planId, string tenantId, DateTime asOf)
    {
        var match = _docs
            .Where(d => d.TenantId == tenantId
                && d.PlanId == planId
                && d.VersionState == PlanVersionState.Published
                && d.EffectiveDate <= asOf
                && (d.TerminationDate == null || d.TerminationDate >= asOf))
            .OrderByDescending(d => d.VersionNumber)
            .FirstOrDefault();
        return Task.FromResult<BenefitPlan?>(match);
    }

    public Task<BenefitPlan?> GetVersionAsync(string planId, string versionId, string tenantId)
    {
        var match = _docs.FirstOrDefault(d => d.TenantId == tenantId && d.PlanId == planId && d.VersionId == versionId);
        return Task.FromResult<BenefitPlan?>(match);
    }

    public Task<(IReadOnlyList<BenefitPlan> Items, string? ContinuationToken)> ListVersionsAsync(
        string planId, string tenantId, int pageSize, string? continuationToken)
    {
        var skip = 0;
        if (!string.IsNullOrEmpty(continuationToken) && int.TryParse(continuationToken, out var parsed))
            skip = parsed;

        var ordered = _docs
            .Where(d => d.TenantId == tenantId && d.PlanId == planId)
            .OrderByDescending(d => d.VersionNumber)
            .Skip(skip)
            .ToList();

        var slice = ordered.Take(pageSize).ToList();
        var next = ordered.Count > pageSize ? (skip + pageSize).ToString() : null;
        return Task.FromResult<(IReadOnlyList<BenefitPlan>, string?)>((slice, next));
    }

    public Task<IEnumerable<BenefitPlan>> SearchAsync(
        string tenantId, string? lineOfBusiness, string? planType, string? metalLevel, int page, int pageSize)
    {
        IEnumerable<BenefitPlan> q = _docs.Where(d => d.TenantId == tenantId);
        if (!string.IsNullOrEmpty(lineOfBusiness) && Enum.TryParse<LineOfBusiness>(lineOfBusiness, true, out var lob))
            q = q.Where(d => d.LineOfBusiness == lob);
        if (!string.IsNullOrEmpty(planType) && Enum.TryParse<PlanType>(planType, true, out var pt))
            q = q.Where(d => d.PlanType == pt);
        if (!string.IsNullOrEmpty(metalLevel) && Enum.TryParse<MetalLevel>(metalLevel, true, out var ml))
            q = q.Where(d => d.MetalLevel == ml);
        return Task.FromResult(q.OrderBy(d => d.PlanName).Skip((page - 1) * pageSize).Take(pageSize));
    }

    public Task<IEnumerable<Benefit>> GetBenefitsAsync(string planId, string tenantId, string? serviceCategory)
    {
        var plan = _docs.Where(d => d.TenantId == tenantId && d.PlanId == planId
                && d.VersionState == PlanVersionState.Published)
            .OrderByDescending(d => d.VersionNumber)
            .FirstOrDefault();
        if (plan == null) return Task.FromResult(Enumerable.Empty<Benefit>());
        var benefits = plan.Benefits.AsEnumerable();
        if (!string.IsNullOrEmpty(serviceCategory))
            benefits = benefits.Where(b => b.ServiceCategory == serviceCategory);
        return Task.FromResult(benefits);
    }

    public Task<BenefitPlan> CreateAsync(BenefitPlan plan)
    {
        if (string.IsNullOrEmpty(plan.Id)) plan.Id = Guid.NewGuid().ToString();
        _docs.Add(plan);
        return Task.FromResult(plan);
    }

    public Task<BenefitPlan> UpdateAsync(BenefitPlan plan)
    {
        var existing = _docs.FirstOrDefault(d => d.Id == plan.Id && d.TenantId == plan.TenantId);
        if (existing != null && existing.VersionState == PlanVersionState.Published)
            throw new PlanVersionStateException(existing.PlanId, existing.VersionId, existing.VersionState,
                $"Plan version {existing.VersionId} is Published and cannot be updated. Create an amendment via POST /amend.");
        if (existing != null && existing.VersionState == PlanVersionState.Superseded)
            throw new PlanVersionStateException(existing.PlanId, existing.VersionId, existing.VersionState,
                $"Plan version {existing.VersionId} is Superseded and is read-only.");

        if (existing != null) _docs.Remove(existing);
        _docs.Add(plan);
        return Task.FromResult(plan);
    }

    public Task DeleteAsync(string id, string tenantId)
    {
        _docs.RemoveAll(d => d.Id == id && d.TenantId == tenantId);
        return Task.CompletedTask;
    }

    public Task<BenefitPlan> CreateDraftAsync(BenefitPlan draft)
    {
        if (string.IsNullOrEmpty(draft.Id)) draft.Id = Guid.NewGuid().ToString();
        draft.VersionState = PlanVersionState.Draft;
        _docs.Add(draft);
        return Task.FromResult(draft);
    }

    public Task<BenefitPlan> UpdateDraftAsync(BenefitPlan draft)
    {
        var existing = _docs.FirstOrDefault(d => d.Id == draft.Id && d.TenantId == draft.TenantId)
            ?? throw new PlanVersionStateException(draft.PlanId, draft.VersionId, PlanVersionState.Draft,
                $"Draft {draft.VersionId} not found") { IsNotFound = true };
        if (existing.VersionState != PlanVersionState.Draft)
            throw new PlanVersionStateException(existing.PlanId, existing.VersionId, existing.VersionState,
                $"Plan version {existing.VersionId} is {existing.VersionState} and cannot be edited.");
        _docs.Remove(existing);
        _docs.Add(draft);
        return Task.FromResult(draft);
    }

    public Task<BenefitPlan> PublishAndSupersedeAsync(BenefitPlan draftToPublish, BenefitPlan? predecessor)
    {
        if (FailNextPublish)
        {
            FailNextPublish = false;
            throw new PlanVersionStateException(
                draftToPublish.PlanId, draftToPublish.VersionId, draftToPublish.VersionState,
                "Simulated transactional batch failure");
        }

        var snapshot = _docs.ToList();
        try
        {
            var existingDraft = _docs.FirstOrDefault(d => d.Id == draftToPublish.Id);
            if (existingDraft != null) _docs.Remove(existingDraft);
            _docs.Add(draftToPublish);

            if (predecessor != null)
            {
                var existingPred = _docs.FirstOrDefault(d => d.Id == predecessor.Id);
                if (existingPred != null) _docs.Remove(existingPred);
                _docs.Add(predecessor);
            }
            return Task.FromResult(draftToPublish);
        }
        catch
        {
            _docs.Clear();
            _docs.AddRange(snapshot);
            throw;
        }
    }
}

public sealed class InMemoryPlanVersionTransitionRepository : IPlanVersionTransitionRepository
{
    public List<PlanVersionTransition> Items { get; } = new();

    public Task<PlanVersionTransition> AppendAsync(PlanVersionTransition transition, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(transition.Id)) transition.Id = Guid.NewGuid().ToString();
        Items.Add(transition);
        return Task.FromResult(transition);
    }

    public Task<IReadOnlyList<PlanVersionTransition>> ListAsync(string planId, string tenantId, CancellationToken ct = default)
    {
        var matches = Items
            .Where(x => x.PlanId == planId && x.TenantId == tenantId)
            .OrderByDescending(x => x.OccurredAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<PlanVersionTransition>>(matches);
    }
}

public sealed class FakePlanVersionEventPublisher : Services.IPlanVersionEventPublisher
{
    public List<PlanVersionEvent> Events { get; } = new();

    public Task<PlanVersionEvent> PublishVersionPublishedAsync(BenefitPlan version, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var e = new PlanVersionEvent
        {
            EventId = $"published:{version.VersionId}",
            EventType = PlanVersionEventType.PlanVersionPublished,
            TenantId = version.TenantId,
            PlanId = version.PlanId,
            VersionId = version.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId,
            Version = Events.Count(x => x.PlanId == version.PlanId && x.TenantId == version.TenantId) + 1,
            OccurredAt = DateTime.UtcNow
        };
        Events.Add(e);
        return Task.FromResult(e);
    }

    public Task<PlanVersionEvent> PublishVersionSupersededAsync(BenefitPlan from, BenefitPlan to, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var e = new PlanVersionEvent
        {
            EventId = $"superseded:{from.VersionId}->{to.VersionId}",
            EventType = PlanVersionEventType.PlanVersionSuperseded,
            TenantId = from.TenantId,
            PlanId = from.PlanId,
            VersionId = from.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId,
            Version = Events.Count(x => x.PlanId == from.PlanId && x.TenantId == from.TenantId) + 1,
            OccurredAt = DateTime.UtcNow
        };
        Events.Add(e);
        return Task.FromResult(e);
    }
}
