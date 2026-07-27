using System.Text.Json;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;
using BenefitPlanService.Repositories;

namespace BenefitPlanService.Services;

/// <summary>
/// Business logic for benefit plan operations
/// </summary>
public interface IBenefitPlanService
{
    Task<IEnumerable<BenefitPlan>> GetPlansAsync(string tenantId, string? payer, string? planType, bool activeOnly);
    Task<BenefitPlan?> GetPlanAsync(string id, string tenantId);
    Task<BenefitPlan> CreatePlanAsync(BenefitPlan plan, string tenantId);
    Task<BenefitPlan?> UpdatePlanAsync(BenefitPlan plan, string tenantId);
    Task<bool> DeletePlanAsync(string id, string tenantId);
    Task<Benefit?> AddBenefitAsync(string planId, string tenantId, Benefit benefit);
    Task<BenefitAppliedResult?> ApplyBenefitRules(string planId, string tenantId, string serviceCategory, string? cptCode, decimal chargeAmount);
    Task<bool> CheckPriorAuthRequirement(string planId, string tenantId, string serviceCategory, string? cptCode);
    Task<MemberCostSharingResult> CalculateMemberCostSharing(string planId, string tenantId, decimal allowedAmount, decimal deductibleAccumulation, decimal oopAccumulation, string serviceCategory, bool inNetwork);

    // ---- Version lifecycle (5.1) -----------------------------------------

    /// <summary>
    /// Persist <paramref name="draft"/> as a brand-new genesis Draft (no
    /// predecessor) for a new <c>PlanId</c>. Sets identity fields.
    /// </summary>
    Task<BenefitPlan> CreateDraftAsync(BenefitPlan draft, string tenantId, string actorId);

    /// <summary>
    /// Move a Draft into <c>Published</c>. If a current Published version
    /// exists for the same <c>PlanId</c>, atomically supersedes it (sets
    /// <c>SupersededAt</c>, <c>SupersededByVersionId</c>) and emits both a
    /// <c>PlanVersionPublished</c> and a <c>PlanVersionSuperseded</c> event.
    /// </summary>
    Task<BenefitPlan> PublishVersionAsync(string planId, string versionId, string tenantId, string actorId, DateTime? effectiveDate = null);

    /// <summary>
    /// Clone the latest Published version of <paramref name="planId"/> into
    /// a new Draft (next <c>VersionNumber</c>, <c>PredecessorVersionId</c>
    /// pointing at the source). The Draft is mutable until published.
    /// </summary>
    Task<BenefitPlan> AmendPublishedPlanAsync(string planId, string tenantId, string actorId);

    /// <summary>
    /// Mark <paramref name="versionId"/> as Superseded. Today this is only
    /// reachable via <see cref="PublishVersionAsync"/> (a successor must
    /// exist); explicit termination without a successor is reserved.
    /// </summary>
    Task<BenefitPlan> SupersedeVersionAsync(string planId, string versionId, string tenantId, string actorId, string reason, DateTime effectiveDate);

    /// <summary>Newest-first list of all versions for a plan, paginated.</summary>
    Task<(IReadOnlyList<BenefitPlan> Items, string? ContinuationToken)> ListVersionsAsync(
        string planId, string tenantId, int pageSize, string? continuationToken);

    /// <summary>Look up a single version.</summary>
    Task<BenefitPlan?> GetVersionAsync(string planId, string versionId, string tenantId);
}

public class BenefitPlanServiceImpl : IBenefitPlanService
{
    private readonly IBenefitPlanRepository _repository;
    private readonly IPlanVersionTransitionRepository _transitions;
    private readonly IPlanVersionEventPublisher _events;
    private readonly INetworkTierSoftValidator _networkTierValidator;
    private readonly IPlanLimitValidator _planLimitValidator;
    private readonly ILogger<BenefitPlanServiceImpl> _logger;

    public BenefitPlanServiceImpl(
        IBenefitPlanRepository repository,
        IPlanVersionTransitionRepository transitions,
        IPlanVersionEventPublisher events,
        INetworkTierSoftValidator networkTierValidator,
        IPlanLimitValidator planLimitValidator,
        ILogger<BenefitPlanServiceImpl> logger)
    {
        _repository = repository;
        _transitions = transitions;
        _events = events;
        _networkTierValidator = networkTierValidator;
        _planLimitValidator = planLimitValidator;
        _logger = logger;
    }

    public async Task<IEnumerable<BenefitPlan>> GetPlansAsync(
        string tenantId,
        string? payer,
        string? planType,
        bool activeOnly)
    {
        var plans = await _repository.SearchAsync(tenantId, null, planType, null, 1, 500);

        if (!string.IsNullOrEmpty(payer))
        {
            plans = plans.Where(p => string.Equals(p.Payer, payer, StringComparison.OrdinalIgnoreCase));
        }

        if (activeOnly)
        {
            plans = plans.Where(p => p.IsActive);
        }

        return plans;
    }

    public Task<BenefitPlan?> GetPlanAsync(string id, string tenantId)
    {
        // Despite the parameter name, every real caller (claims-service's
        // benefit-plan resolver, ChoBenefitPlanAdapter, GetAccumulation)
        // passes the plan's business-key PlanId here, not the internal
        // auto-generated Id -- GetByIdAsync filters on the wrong field and
        // 404s on a plan that was just created successfully.
        return _repository.GetByPlanIdAsync(id, tenantId);
    }

    public async Task<BenefitPlan> CreatePlanAsync(BenefitPlan plan, string tenantId)
    {
        plan.TenantId = tenantId;
        plan.CreatedAt = DateTime.UtcNow;
        plan.UpdatedAt = DateTime.UtcNow;

        // Legacy create-and-go semantics: plans created via the original
        // POST endpoint are published as v1 immediately. Callers that want
        // an explicit draft → publish workflow use CreateDraftAsync /
        // PublishVersionAsync instead.
        if (string.IsNullOrEmpty(plan.VersionId)) plan.VersionId = PlanVersionId.NewId();
        if (plan.VersionNumber <= 0) plan.VersionNumber = 1;
        plan.VersionState = PlanVersionState.Published;
        plan.PublishedAt = DateTime.UtcNow;

        _networkTierValidator.Inspect(plan, NetworkTierWriteCaller.CreatePlan);
        _planLimitValidator.Validate(plan, PlanLimitWriteCaller.CreatePlan);
        return await _repository.CreateAsync(plan);
    }

    public async Task<BenefitPlan?> UpdatePlanAsync(BenefitPlan plan, string tenantId)
    {
        var existing = await _repository.GetByIdAsync(plan.Id, tenantId);
        if (existing == null)
        {
            return null;
        }

        plan.TenantId = tenantId;
        plan.UpdatedAt = DateTime.UtcNow;
        _networkTierValidator.Inspect(plan, NetworkTierWriteCaller.UpdatePlan);
        _planLimitValidator.Validate(plan, PlanLimitWriteCaller.UpdatePlan);
        // Repository raises PlanVersionStateException for Published/Superseded;
        // controller maps to 409.
        return await _repository.UpdateAsync(plan);
    }

    public async Task<bool> DeletePlanAsync(string id, string tenantId)
    {
        var existing = await _repository.GetByIdAsync(id, tenantId);
        if (existing == null)
        {
            return false;
        }

        existing.IsActive = false;
        existing.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(existing);
        return true;
    }

    public async Task<Benefit?> AddBenefitAsync(string planId, string tenantId, Benefit benefit)
    {
        var plan = await _repository.GetByIdAsync(planId, tenantId);
        if (plan == null)
        {
            return null;
        }

        plan.Benefits.Add(benefit);
        plan.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(plan);
        return benefit;
    }

    /// <summary>
    /// Apply benefit rules for a service to get copay, coinsurance, deductible
    /// </summary>
    public async Task<BenefitAppliedResult?> ApplyBenefitRules(
        string planId,
        string tenantId,
        string serviceCategory,
        string? cptCode,
        decimal chargeAmount)
    {
        var benefits = await _repository.GetBenefitsAsync(planId, tenantId, serviceCategory);
        var benefit = benefits.FirstOrDefault();

        if (benefit == null)
        {
            _logger.LogWarning("No benefit found for plan {PlanId}, category {Category}", SanitizeForLog(planId), SanitizeForLog(serviceCategory));
            return null;
        }

        // Check if specific CPT code is covered
        if (!string.IsNullOrEmpty(cptCode) && benefit.CptCodes != null && benefit.CptCodes.Any())
        {
            if (!benefit.CptCodes.Contains(cptCode))
            {
                _logger.LogWarning("CPT code {CptCode} not covered in benefit", SanitizeForLog(cptCode));
                return new BenefitAppliedResult
                {
                    IsCovered = false,
                    DenialReason = $"CPT code {cptCode} not covered under {serviceCategory} benefits"
                };
            }
        }

        return new BenefitAppliedResult
        {
            IsCovered = true,
            ServiceCategory = benefit.ServiceCategory,
            CopayAmount = benefit.CopayAmount,
            CoinsurancePercentage = benefit.CoinsurancePercentage,
            DeductibleApplies = benefit.DeductibleApplies,
            RequiresPriorAuth = benefit.RequiresPriorAuth,
            VisitLimit = benefit.VisitLimit,
            VisitLimitPeriod = benefit.VisitLimitPeriod
        };
    }

    /// <summary>
    /// Check if prior authorization is required for a service
    /// </summary>
    public async Task<bool> CheckPriorAuthRequirement(
        string planId,
        string tenantId,
        string serviceCategory,
        string? cptCode)
    {
        var result = await ApplyBenefitRules(planId, tenantId, serviceCategory, cptCode, 0);
        return result?.RequiresPriorAuth ?? false;
    }

    /// <summary>
    /// Calculate member cost-sharing (deductible, coinsurance, copay, OOP max)
    /// </summary>
    public async Task<MemberCostSharingResult> CalculateMemberCostSharing(
        string planId,
        string tenantId,
        decimal allowedAmount,
        decimal deductibleAccumulation,
        decimal oopAccumulation,
        string serviceCategory,
        bool inNetwork)
    {
        var plan = await _repository.GetByPlanIdAsync(planId, tenantId);
        if (plan == null)
        {
            throw new InvalidOperationException($"Plan {planId} not found");
        }

        var benefit = (await _repository.GetBenefitsAsync(planId, tenantId, serviceCategory)).FirstOrDefault();
        if (benefit == null)
        {
            throw new InvalidOperationException($"No benefit found for {serviceCategory}");
        }

        var result = new MemberCostSharingResult
        {
            AllowedAmount = allowedAmount
        };

        // Get applicable cost-sharing limits based on network status
        var costSharing = plan.CostSharing;
        if (costSharing == null)
        {
            throw new InvalidOperationException($"No cost-sharing defined for plan {planId}");
        }

        decimal deductible = inNetwork ? costSharing.InNetworkDeductible : costSharing.OutOfNetworkDeductible;
        decimal oopMax = inNetwork ? costSharing.InNetworkOutOfPocketMax : costSharing.OutOfNetworkOutOfPocketMax;

        // Calculate deductible portion
        decimal deductibleAmount = 0;
        decimal remainingDeductible = deductible - deductibleAccumulation;

        if (benefit.DeductibleApplies && remainingDeductible > 0)
        {
            // Member pays deductible up to remaining amount
            deductibleAmount = Math.Min(allowedAmount, remainingDeductible);
            result.DeductibleAmount = deductibleAmount;
        }

        // Calculate coinsurance/copay after deductible
        decimal amountAfterDeductible = allowedAmount - deductibleAmount;
        decimal coinsuranceOrCopay = 0;

        if (benefit.CopayAmount.HasValue && benefit.CopayAmount.Value > 0)
        {
            // Fixed copay
            coinsuranceOrCopay = benefit.CopayAmount.Value;
            result.CopayAmount = coinsuranceOrCopay;
        }
        else if (benefit.CoinsurancePercentage.HasValue && benefit.CoinsurancePercentage.Value > 0)
        {
            // Percentage coinsurance
            coinsuranceOrCopay = amountAfterDeductible * (benefit.CoinsurancePercentage.Value / 100m);
            result.CoinsuranceAmount = coinsuranceOrCopay;
        }

        // Calculate total patient responsibility before OOP max
        decimal totalPatientResponsibility = deductibleAmount + coinsuranceOrCopay;

        // Check out-of-pocket maximum
        decimal remainingOop = oopMax - oopAccumulation;
        if (remainingOop <= 0)
        {
            // Member has reached OOP max - no patient responsibility
            totalPatientResponsibility = 0;
            result.DeductibleAmount = 0;
            result.CoinsuranceAmount = 0;
            result.CopayAmount = 0;
            result.OopMaxReached = true;
        }
        else if (totalPatientResponsibility > remainingOop)
        {
            // This claim will hit the OOP max
            totalPatientResponsibility = remainingOop;
            result.OopMaxReached = true;
        }

        result.PatientResponsibility = totalPatientResponsibility;
        result.PayerResponsibility = allowedAmount - totalPatientResponsibility;

        return result;
    }

    public async Task<BenefitPlan> CreateDraftAsync(BenefitPlan draft, string tenantId, string actorId)
    {
        draft.TenantId = tenantId;
        draft.CreatedBy = string.IsNullOrEmpty(draft.CreatedBy) ? actorId : draft.CreatedBy;
        draft.CreatedAt = DateTime.UtcNow;
        draft.UpdatedAt = DateTime.UtcNow;
        draft.VersionId = PlanVersionId.NewId();
        draft.VersionNumber = 1;
        draft.VersionState = PlanVersionState.Draft;
        draft.PredecessorVersionId = null;
        draft.PublishedAt = null;
        draft.PublishedBy = null;
        draft.SupersededAt = null;
        draft.SupersededByVersionId = null;
        // Legacy IsActive semantics ⇒ Drafts are not active.
        draft.IsActive = false;

        _networkTierValidator.Inspect(draft, NetworkTierWriteCaller.CreateDraft);
        _planLimitValidator.Validate(draft, PlanLimitWriteCaller.CreateDraft);
        return await _repository.CreateDraftAsync(draft);
    }

    public async Task<BenefitPlan> PublishVersionAsync(string planId, string versionId, string tenantId, string actorId, DateTime? effectiveDate = null)
    {
        var draft = await _repository.GetVersionAsync(planId, versionId, tenantId)
            ?? throw new PlanVersionStateException(planId, versionId, PlanVersionState.Draft,
                $"Version {versionId} not found") { IsNotFound = true };

        if (draft.VersionState != PlanVersionState.Draft)
        {
            throw new PlanVersionStateException(planId, versionId, draft.VersionState,
                $"Version {versionId} is {draft.VersionState}; only Draft versions can be published.");
        }

        var current = await _repository.GetLatestPublishedAsync(planId, tenantId, DateTime.UtcNow);

        // Optimistic-concurrency: the draft must point at the version that
        // is currently Published (or null on a genesis publish). If something
        // got published in between (or the draft was created from a stale
        // snapshot) the predecessor / version-number invariants no longer
        // hold — surface 409 and force the caller to re-amend from latest.
        var expectedPredecessor = current?.VersionId;
        if (draft.PredecessorVersionId != expectedPredecessor)
        {
            throw new PlanVersionStateException(planId, versionId, draft.VersionState,
                $"Draft predecessor '{draft.PredecessorVersionId ?? "<none>"}' does not match the current Published version '{expectedPredecessor ?? "<none>"}'. Re-amend from the latest version and retry.");
        }
        // Belt-and-suspenders: also validate VersionNumber so that a draft
        // whose VersionNumber was patched directly (e.g. via UpdateDraftAsync
        // on a manipulated payload) cannot sneak through when its predecessor
        // pointer happens to match.
        var expectedNumber = (current?.VersionNumber ?? 0) + 1;
        if (draft.VersionNumber != expectedNumber)
        {
            throw new PlanVersionStateException(planId, versionId, draft.VersionState,
                $"Draft version number {draft.VersionNumber} does not match the expected next number {expectedNumber}. Re-amend from the latest version and retry.");
        }

        var now = DateTime.UtcNow;
        draft.VersionState = PlanVersionState.Published;
        draft.PublishedAt = now;
        draft.PublishedBy = actorId;
        draft.IsActive = true;
        if (effectiveDate.HasValue) draft.EffectiveDate = effectiveDate.Value;

        BenefitPlan? predecessor = null;
        if (current != null && current.VersionId != draft.VersionId)
        {
            predecessor = current;
            predecessor.VersionState = PlanVersionState.Superseded;
            predecessor.SupersededAt = now;
            predecessor.SupersededByVersionId = draft.VersionId;
            predecessor.IsActive = false;
        }

        _networkTierValidator.Inspect(draft, NetworkTierWriteCaller.PublishAndSupersede);
        _planLimitValidator.Validate(draft, PlanLimitWriteCaller.PublishAndSupersede);
        await _repository.PublishAndSupersedeAsync(draft, predecessor);

        await _transitions.AppendAsync(new PlanVersionTransition
        {
            TenantId = tenantId,
            PlanId = planId,
            FromVersionId = predecessor?.VersionId,
            ToVersionId = draft.VersionId,
            TransitionType = predecessor == null ? PlanVersionTransitionType.Publish : PlanVersionTransitionType.Supersede,
            EffectiveDate = draft.EffectiveDate,
            OccurredAt = now,
            ActorId = actorId
        });

        await _events.PublishVersionPublishedAsync(draft, actorId, correlationId: null);
        if (predecessor != null)
        {
            await _events.PublishVersionSupersededAsync(predecessor, draft, reason: null, actorId, correlationId: null);
        }

        return draft;
    }

    public async Task<BenefitPlan> AmendPublishedPlanAsync(string planId, string tenantId, string actorId)
    {
        var current = await _repository.GetLatestPublishedAsync(planId, tenantId, DateTime.UtcNow)
            ?? throw new PlanVersionStateException(planId, string.Empty, PlanVersionState.Published,
                $"No Published version of plan {planId} exists to amend") { IsNotFound = true };

        var draft = Clone(current);
        draft.Id = Guid.NewGuid().ToString();
        draft.VersionId = PlanVersionId.NewId();
        draft.VersionNumber = current.VersionNumber + 1;
        draft.VersionState = PlanVersionState.Draft;
        draft.PredecessorVersionId = current.VersionId;
        draft.PublishedAt = null;
        draft.PublishedBy = null;
        draft.SupersededAt = null;
        draft.SupersededByVersionId = null;
        draft.IsActive = false;
        draft.CreatedBy = actorId;
        draft.CreatedAt = DateTime.UtcNow;
        draft.UpdatedAt = DateTime.UtcNow;

        _networkTierValidator.Inspect(draft, NetworkTierWriteCaller.AmendPublished);
        _planLimitValidator.Validate(draft, PlanLimitWriteCaller.AmendPublished);
        var stored = await _repository.CreateDraftAsync(draft);

        await _transitions.AppendAsync(new PlanVersionTransition
        {
            TenantId = tenantId,
            PlanId = planId,
            FromVersionId = current.VersionId,
            ToVersionId = stored.VersionId,
            TransitionType = PlanVersionTransitionType.Amend,
            OccurredAt = DateTime.UtcNow,
            ActorId = actorId
        });

        return stored;
    }

    public async Task<BenefitPlan> SupersedeVersionAsync(string planId, string versionId, string tenantId, string actorId, string reason, DateTime effectiveDate)
    {
        var target = await _repository.GetVersionAsync(planId, versionId, tenantId)
            ?? throw new PlanVersionStateException(planId, versionId, PlanVersionState.Published,
                $"Version {versionId} not found") { IsNotFound = true };

        if (target.VersionState != PlanVersionState.Published)
        {
            throw new PlanVersionStateException(planId, versionId, target.VersionState,
                $"Version {versionId} is {target.VersionState}; only Published versions can be superseded.");
        }

        // Standalone supersede paths (without a successor) are reserved.
        // Today, supersession always happens via PublishVersionAsync — this
        // method exists so callers can request an explicit transition log
        // without the side-effect of publishing a new version.
        throw new InvalidOperationException(
            "Standalone supersede (without a successor Published version) is not supported in this release. " +
            "Create an amendment and publish it to supersede the current version.");
    }

    public Task<(IReadOnlyList<BenefitPlan> Items, string? ContinuationToken)> ListVersionsAsync(
        string planId, string tenantId, int pageSize, string? continuationToken)
        => _repository.ListVersionsAsync(planId, tenantId, pageSize, continuationToken);

    public Task<BenefitPlan?> GetVersionAsync(string planId, string versionId, string tenantId)
        => _repository.GetVersionAsync(planId, versionId, tenantId);

    private static BenefitPlan Clone(BenefitPlan src) => new()
    {
        TenantId = src.TenantId,
        PlanId = src.PlanId,
        PlanName = src.PlanName,
        Payer = src.Payer,
        EffectiveDate = src.EffectiveDate,
        TerminationDate = src.TerminationDate,
        PlanType = src.PlanType,
        MetalLevel = src.MetalLevel,
        LineOfBusiness = src.LineOfBusiness,
        FamilyAccumulatorModel = src.FamilyAccumulatorModel,
        Benefits = src.Benefits.Select(CloneBenefit).ToList(),
        NetworkTiers = src.NetworkTiers.Select(CloneNetworkTier).ToList(),
        CostSharing = CloneCostSharing(src.CostSharing),
        Documents = src.Documents.Select(CloneDocument).ToList(),
    };

    // Shared options for the polymorphic benefit clone path. JsonSerializerDefaults.Web
    // matches the wire format used by repositories and the in-memory fake, so a clone
    // through these options preserves the subclass discriminator end-to-end.
    // BenefitJsonConverter is registered here (not via [JsonConverter] on Benefit)
    // to prevent infinite recursion. When the attribute is on the base class, STJ
    // inherits it on all subclasses, causing BenefitJsonConverter to be invoked
    // again during the inner Serialize call in Write(), even after WithoutSelf()
    // removes it from options.Converters.
    private static readonly JsonSerializerOptions _benefitCloneOpts = BuildBenefitCloneOpts();
    private static JsonSerializerOptions BuildBenefitCloneOpts()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new BenefitJsonConverter());
        return o;
    }

    /// <summary>
    /// Deep-clones a <see cref="Benefit"/> through a polymorphic JSON
    /// round-trip so typed subclasses (<c>PharmacyBenefit</c>,
    /// <c>BehavioralHealthBenefit</c>, …) survive the
    /// <c>AmendPublishedPlanAsync</c> flow. A manual property-by-property
    /// clone would silently downgrade every subclass to base
    /// <see cref="Benefit"/> on amendment.
    /// </summary>
    private static Benefit CloneBenefit(Benefit b)
        => JsonSerializer.Deserialize<Benefit>(JsonSerializer.Serialize(b, _benefitCloneOpts), _benefitCloneOpts)!;

#pragma warning disable CS0618 // Cloning ProviderNpis preserves the legacy field during the 5.5 migration window
    private static NetworkTier CloneNetworkTier(NetworkTier n) => new()
    {
        TierName = n.TierName,
        TierLevel = n.TierLevel,
        NetworkId = n.NetworkId,
        ProviderNpis = n.ProviderNpis.ToList()
    };
#pragma warning restore CS0618

    private static CostSharing CloneCostSharing(CostSharing c) => new()
    {
        IndividualDeductible = c.IndividualDeductible,
        FamilyDeductible = c.FamilyDeductible,
        IndividualOutOfPocketMax = c.IndividualOutOfPocketMax,
        FamilyOutOfPocketMax = c.FamilyOutOfPocketMax,
        InNetworkDeductible = c.InNetworkDeductible,
        OutOfNetworkDeductible = c.OutOfNetworkDeductible,
        InNetworkOutOfPocketMax = c.InNetworkOutOfPocketMax,
        OutOfNetworkOutOfPocketMax = c.OutOfNetworkOutOfPocketMax,
        OutNetworkIndividualDeductible = c.OutNetworkIndividualDeductible,
        OutNetworkFamilyDeductible = c.OutNetworkFamilyDeductible,
        OutNetworkIndividualOutOfPocketMax = c.OutNetworkIndividualOutOfPocketMax,
        OutNetworkFamilyOutOfPocketMax = c.OutNetworkFamilyOutOfPocketMax
    };

    private static PlanDocumentReference CloneDocument(PlanDocumentReference d) => new()
    {
        DocType = d.DocType,
        Location = d.Location,
        ContentType = d.ContentType,
        Size = d.Size,
        ContentHashSha256 = d.ContentHashSha256,
        Version = d.Version,
        EffectiveDate = d.EffectiveDate,
        DisplayName = d.DisplayName
    };

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>
/// Result of applying benefit rules
/// </summary>
public class BenefitAppliedResult
{
    public bool IsCovered { get; set; }
    public string? DenialReason { get; set; }
    public string? ServiceCategory { get; set; }
    public decimal? CopayAmount { get; set; }
    public decimal? CoinsurancePercentage { get; set; }
    public bool DeductibleApplies { get; set; }
    public bool RequiresPriorAuth { get; set; }
    public int? VisitLimit { get; set; }
    public string? VisitLimitPeriod { get; set; }
}

/// <summary>
/// Result of member cost-sharing calculation
/// </summary>
public class MemberCostSharingResult
{
    public decimal AllowedAmount { get; set; }
    public decimal DeductibleAmount { get; set; }
    public decimal CoinsuranceAmount { get; set; }
    public decimal CopayAmount { get; set; }
    public decimal PatientResponsibility { get; set; }
    public decimal PayerResponsibility { get; set; }
    public bool OopMaxReached { get; set; }
}
