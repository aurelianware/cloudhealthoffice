using BenefitPlanService.Models;
using BenefitPlanService.Services;

namespace BenefitPlanService.Adapters;

/// <summary>
/// Default benefit-plan adapter using CHO's internal services
/// (<see cref="IBenefitPlanService"/> + <see cref="IBenefitViewService"/>).
/// Preserves existing behavior — for the current set of tenants the factory
/// always resolves to this adapter.
/// </summary>
public class ChoBenefitPlanAdapter : IBenefitPlanAdapter
{
    private readonly IBenefitPlanService _planService;
    private readonly IBenefitViewService _viewService;
    private readonly ILogger<ChoBenefitPlanAdapter> _logger;

    public string Platform => "cho";

    public ChoBenefitPlanAdapter(
        IBenefitPlanService planService,
        IBenefitViewService viewService,
        ILogger<ChoBenefitPlanAdapter> logger)
    {
        _planService = planService;
        _viewService = viewService;
        _logger = logger;
    }

    public async Task<BenefitPlanAdapterResponse> GetPlanAsync(
        BenefitPlanAdapterRequest request, CancellationToken ct = default)
    {
        var plan = await _planService.GetPlanAsync(request.PlanId, request.TenantId);
        return new BenefitPlanAdapterResponse
        {
            Platform = Platform,
            Plan = plan is null ? null : AdapterBenefitPlan.From(plan),
        };
    }

    public async Task<BenefitPlanAdapterResponse> GetPlanVersionAsync(
        BenefitPlanAdapterRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.VersionId))
        {
            throw new ArgumentException(
                "VersionId is required for GetPlanVersionAsync.", nameof(request));
        }

        var version = await _planService.GetVersionAsync(
            request.PlanId, request.VersionId, request.TenantId);
        return new BenefitPlanAdapterResponse
        {
            Platform = Platform,
            Plan = version is null ? null : AdapterBenefitPlan.From(version),
        };
    }

    public async Task<MemberBenefitViewAdapterResponse> GetMemberBenefitViewAsync(
        BenefitPlanAdapterRequest request, CancellationToken ct = default)
    {
        var asOf = (request.ServiceDate ?? DateTime.UtcNow).Date;
        var view = await _viewService.GetMemberViewAsync(request.PlanId, request.TenantId, asOf);
        return new MemberBenefitViewAdapterResponse
        {
            Platform = Platform,
            View = view is null ? null : AdapterMemberBenefitView.From(view),
        };
    }
}
