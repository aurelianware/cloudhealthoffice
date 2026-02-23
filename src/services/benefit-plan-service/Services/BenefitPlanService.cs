using BenefitPlanService.Models;
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
}

public class BenefitPlanServiceImpl : IBenefitPlanService
{
    private readonly IBenefitPlanRepository _repository;
    private readonly ILogger<BenefitPlanServiceImpl> _logger;

    public BenefitPlanServiceImpl(
        IBenefitPlanRepository repository,
        ILogger<BenefitPlanServiceImpl> logger)
    {
        _repository = repository;
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
        return _repository.GetByIdAsync(id, tenantId);
    }

    public async Task<BenefitPlan> CreatePlanAsync(BenefitPlan plan, string tenantId)
    {
        plan.TenantId = tenantId;
        plan.CreatedAt = DateTime.UtcNow;
        plan.UpdatedAt = DateTime.UtcNow;
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
            _logger.LogWarning("No benefit found for plan {PlanId}, category {Category}", planId, serviceCategory);
            return null;
        }

        // Check if specific CPT code is covered
        if (!string.IsNullOrEmpty(cptCode) && benefit.CptCodes != null && benefit.CptCodes.Any())
        {
            if (!benefit.CptCodes.Contains(cptCode))
            {
                _logger.LogWarning("CPT code {CptCode} not covered in benefit", cptCode);
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
