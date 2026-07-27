using System.Text.Json;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;
using BenefitPlanService.Repositories;
using BenefitPlanService.Services;

namespace BenefitPlanService.Tests.Fakes;

/// <summary>
/// In-memory fake of <see cref="IBenefitPlanRepository"/> with full
/// version-chain semantics. Used by service- and controller-level tests
/// to avoid requiring a live Mongo/Cosmos.
/// </summary>
/// <remarks>
/// All store and fetch operations deep-clone via JSON round-trip so that
/// external mutations of a returned object cannot corrupt the stored document
/// — matching the behaviour of real Cosmos / Mongo round-trips and preventing
/// tests from becoming order-dependent due to shared object references.
/// </remarks>
public sealed class InMemoryBenefitPlanRepository : IBenefitPlanRepository
{
    // BenefitJsonConverter registered here (not via [JsonConverter] on Benefit) so
    // that WithoutSelf() can strip it from a copy when serializing concrete subtypes,
    // preventing the attribute-inheritance stack overflow.
    private static readonly JsonSerializerOptions _jsonOpts = BuildJsonOpts();
    private static JsonSerializerOptions BuildJsonOpts()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new BenefitJsonConverter());
        return o;
    }
    private readonly List<BenefitPlan> _docs = new();
    public IReadOnlyList<BenefitPlan> Docs => _docs;

    /// <summary>Set true to simulate a transactional batch failure.</summary>
    public bool FailNextPublish { get; set; }

    private static BenefitPlan Clone(BenefitPlan plan)
        => JsonSerializer.Deserialize<BenefitPlan>(JsonSerializer.Serialize(plan, _jsonOpts), _jsonOpts)!;

    public Task<BenefitPlan?> GetByIdAsync(string id, string tenantId)
    {
        var doc = _docs.FirstOrDefault(d => d.Id == id && d.TenantId == tenantId);
        return Task.FromResult(doc is null ? null : Clone(doc));
    }

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
        return Task.FromResult(match is null ? null : Clone(match));
    }

    public Task<BenefitPlan?> GetVersionAsync(string planId, string versionId, string tenantId)
    {
        var match = _docs.FirstOrDefault(d => d.TenantId == tenantId && d.PlanId == planId && d.VersionId == versionId);
        return Task.FromResult(match is null ? null : Clone(match));
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

        var slice = ordered.Take(pageSize).Select(Clone).ToList();
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
        return Task.FromResult(q.OrderBy(d => d.PlanName).Skip((page - 1) * pageSize).Take(pageSize).Select(Clone));
    }

    public Task<IEnumerable<Benefit>> GetBenefitsAsync(string planId, string tenantId, string? serviceCategory)
    {
        var plan = _docs.Where(d => d.TenantId == tenantId && d.PlanId == planId
                && d.VersionState == PlanVersionState.Published)
            .OrderByDescending(d => d.VersionNumber)
            .FirstOrDefault();
        if (plan == null) return Task.FromResult(Enumerable.Empty<Benefit>());
        var cloned = Clone(plan);
        var benefits = cloned.Benefits.AsEnumerable();
        if (!string.IsNullOrEmpty(serviceCategory))
            benefits = benefits.Where(b => b.ServiceCategory == serviceCategory);
        return Task.FromResult(benefits);
    }

    public Task<BenefitPlan> CreateAsync(BenefitPlan plan)
    {
        if (string.IsNullOrEmpty(plan.Id)) plan.Id = Guid.NewGuid().ToString();
        _docs.Add(Clone(plan));
        return Task.FromResult(Clone(plan));
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
        _docs.Add(Clone(plan));
        return Task.FromResult(Clone(plan));
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
        _docs.Add(Clone(draft));
        return Task.FromResult(Clone(draft));
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
        _docs.Add(Clone(draft));
        return Task.FromResult(Clone(draft));
    }

    public Task<bool> UpdateNetworkTiersAsync(
        string tenantId,
        string planId,
        IReadOnlyList<NetworkTier> tiers,
        CancellationToken ct = default)
    {
        var asOf = DateTime.UtcNow;
        var head = _docs
            .Where(d => d.TenantId == tenantId
                && d.PlanId == planId
                && d.VersionState == PlanVersionState.Published
                && d.EffectiveDate <= asOf
                && (d.TerminationDate == null || d.TerminationDate >= asOf))
            .OrderByDescending(d => d.VersionNumber)
            .FirstOrDefault();

        if (head is null) return Task.FromResult(false);

        head.NetworkTiers = tiers.ToList();
        head.ModifiedDate = DateTime.UtcNow;
        return Task.FromResult(true);
    }

    public Task<bool> TerminateVersionAsync(BenefitPlan version)
    {
        if (version.VersionState != PlanVersionState.Superseded)
        {
            throw new InvalidOperationException(
                "TerminateVersionAsync expects version to already have VersionState=Superseded applied by the service layer.");
        }

        var existing = _docs.FirstOrDefault(d => d.Id == version.Id && d.TenantId == version.TenantId);
        if (existing is null) return Task.FromResult(false);

        _docs.Remove(existing);
        version.ModifiedDate = DateTime.UtcNow;
        _docs.Add(Clone(version));
        return Task.FromResult(true);
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
            _docs.Add(Clone(draftToPublish));

            if (predecessor != null)
            {
                var existingPred = _docs.FirstOrDefault(d => d.Id == predecessor.Id);
                if (existingPred != null) _docs.Remove(existingPred);
                _docs.Add(Clone(predecessor));
            }
            return Task.FromResult(Clone(draftToPublish));
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

/// <summary>
/// In-memory <see cref="IPlanYearTransitionPublisher"/> with the same
/// idempotency contract as the Mongo implementation: re-emitting an
/// EventId is a no-op rather than a duplicate row.
/// </summary>
public sealed class FakePlanYearTransitionPublisher : IPlanYearTransitionPublisher
{
    public List<PlanYearTransitionEvent> Events { get; } = new();

    public Task<PlanYearTransitionEvent> PublishApproachingAsync(
        BenefitPlan plan, DateTime planYearEnd, DateTime nextPlanYearStart,
        string? actorId, string? correlationId, CancellationToken ct = default)
        => AppendOrReturnExisting(Build(plan, PlanYearTransitionType.ApproachingTransition,
            planYearEnd, nextPlanYearStart, actorId, correlationId));

    public Task<PlanYearTransitionEvent> PublishTransitionAsync(
        BenefitPlan plan, DateTime planYearEnd, DateTime nextPlanYearStart,
        string? actorId, string? correlationId, CancellationToken ct = default)
        => AppendOrReturnExisting(Build(plan, PlanYearTransitionType.Transition,
            planYearEnd, nextPlanYearStart, actorId, correlationId));

    private Task<PlanYearTransitionEvent> AppendOrReturnExisting(PlanYearTransitionEvent e)
    {
        var existing = Events.FirstOrDefault(x =>
            x.TenantId == e.TenantId && x.PlanId == e.PlanId && x.EventId == e.EventId);
        if (existing != null) return Task.FromResult(existing);

        e.Version = Events.Count(x => x.TenantId == e.TenantId && x.PlanId == e.PlanId) + 1;
        Events.Add(e);
        return Task.FromResult(e);
    }

    private static PlanYearTransitionEvent Build(
        BenefitPlan plan, PlanYearTransitionType type,
        DateTime planYearEnd, DateTime nextPlanYearStart,
        string? actorId, string? correlationId) => new()
        {
            EventId = PlanYearTransitionEvent.BuildEventId(type, plan.TenantId, plan.PlanId, planYearEnd),
            TransitionType = type,
            TenantId = plan.TenantId,
            PlanId = plan.PlanId,
            FromPlanYearEnd = planYearEnd,
            ToPlanYearStart = nextPlanYearStart,
            ActorId = actorId,
            CorrelationId = correlationId,
            OccurredAt = DateTime.UtcNow
        };
}

/// <summary>
/// No-op <see cref="INetworkTierSoftValidator"/> for service-level
/// tests that don't exercise the soft-validation telemetry path. Tests
/// that do exercise it construct <see cref="NetworkTierSoftValidator"/>
/// directly with their own logger / options.
/// </summary>
public sealed class NoOpNetworkTierSoftValidator : INetworkTierSoftValidator
{
    public void Inspect(BenefitPlan plan, NetworkTierWriteCaller caller) { }
}

/// <summary>
/// No-op <see cref="IPlanLimitValidator"/> for tests that don't exercise
/// ACA-cap validation directly. Tests that do exercise it (see
/// <c>PlanLimitValidatorTests</c>) construct
/// <see cref="PlanLimitValidator"/> with stubbed limits and resolver.
/// </summary>
public sealed class NoOpPlanLimitValidator : IPlanLimitValidator
{
    public void Validate(BenefitPlan plan, PlanLimitWriteCaller caller) { }
}

public sealed class FakePlanYearScheduleSource : IPlanYearScheduleSource
{
    public List<BenefitPlan> Plans { get; } = new();

    public async IAsyncEnumerable<BenefitPlan> EnumeratePlansAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var p in Plans)
        {
            ct.ThrowIfCancellationRequested();
            yield return p;
            await Task.Yield();
        }
    }
}

public sealed class FakePlanVersionEventPublisher : IPlanVersionEventPublisher
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

    public Task<PlanVersionEvent> PublishVersionTerminatedAsync(BenefitPlan version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
    {
        var e = new PlanVersionEvent
        {
            EventId = $"terminated:{version.VersionId}",
            EventType = PlanVersionEventType.PlanVersionTerminated,
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
}
