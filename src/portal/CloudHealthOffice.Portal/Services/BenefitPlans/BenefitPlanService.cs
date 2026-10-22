using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using MongoDB.Driver;

namespace CloudHealthOffice.Portal.Services;

public class BenefitPlanService : IBenefitPlanService
{
    private static readonly JsonSerializerOptions BenefitPlanJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BenefitPlanService> _logger;

    public BenefitPlanService(HttpClient httpClient, IConfiguration configuration, ILogger<BenefitPlanService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<BenefitPlan>> GetBenefitPlansAsync()
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var plans = await _httpClient.GetFromJsonAsync<List<BenefitPlan>>($"{baseUrl}/v1/plans");
            return plans ?? new List<BenefitPlan>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<List<BenefitPlanListItem>> SearchBenefitPlansAsync(string? sponsorId = null, string? productType = null)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var query = $"{baseUrl}/v1/plans?";
            if (!string.IsNullOrEmpty(sponsorId))
                query += $"payer={sponsorId}&";
            if (!string.IsNullOrEmpty(productType))
                query += $"planType={productType}";

            var plans = await _httpClient.GetFromJsonAsync<List<BenefitPlanApiResponse>>(
                query,
                BenefitPlanJsonOptions);
            return plans?.Select(MapListItem).ToList() ?? new List<BenefitPlanListItem>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<BenefitPlanDetails?> GetBenefitPlanByIdAsync(string planId)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var plan = await _httpClient.GetFromJsonAsync<BenefitPlanApiResponse>(
                $"{baseUrl}/v1/plans/{Uri.EscapeDataString(planId)}",
                BenefitPlanJsonOptions);
            return plan == null ? null : MapDetails(plan);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<string> CreateBenefitPlanAsync(CreateBenefitPlanRequest request)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            // Map portal fields to service model fields
            var payload = new
            {
                tenantId = "placeholder",  // overwritten by service from X-Tenant-ID header
                planId = $"PLAN-{Guid.NewGuid().ToString()[..8].ToUpper()}",
                planName = request.PlanName,
                payer = request.SponsorId,
                planType = request.ProductType,
                metalLevel = request.MetalTier,
                effectiveDate = request.EffectiveDate == default ? DateTime.UtcNow : request.EffectiveDate,
                lineOfBusiness = "Commercial",
                networkTiers = string.IsNullOrWhiteSpace(request.Network)
                    ? Array.Empty<object>()
                    : new[]
                    {
                        new
                        {
                            tierName = request.Network,
                            tierLevel = 1,
                            networkId = request.Network
                        }
                    },
                costSharing = new
                {
                    individualDeductible = request.IndividualDeductible,
                    familyDeductible = request.FamilyDeductible,
                    individualOutOfPocketMax = request.IndividualOOPMax,
                    familyOutOfPocketMax = request.FamilyOOPMax,
                    coinsurance = request.Coinsurance,
                    monthlyPremium = request.MonthlyPremium
                }
            };
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/v1/plans", payload);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<CreateBenefitPlanResponse>();
            return result?.PlanId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task UpdateBenefitPlanAsync(string planId, UpdateBenefitPlanRequest request)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/v1/plans/{planId}", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public Task AddBenefitAsync(string planId, UpsertPlanBenefitRequest request)
        => WriteBenefitAsync(HttpMethod.Post, planId, benefitId: null, request);

    public Task UpdateBenefitAsync(
        string planId,
        string benefitId,
        UpsertPlanBenefitRequest request)
        => WriteBenefitAsync(HttpMethod.Put, planId, benefitId, request);

    public async Task ReplaceNetworkTiersAsync(
        string planId,
        IReadOnlyList<PlanNetworkTier> networkTiers)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        var url = $"{baseUrl}/v1/plans/{Uri.EscapeDataString(planId)}/network-tiers";
        var payload = networkTiers.Select(tier => new
        {
            id = tier.Id,
            tierName = tier.TierName.Trim(),
            tierLevel = tier.TierLevel,
            networkId = tier.NetworkId.Trim(),
        });

        try
        {
            var response = await _httpClient.PutAsJsonAsync(url, payload);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    private async Task WriteBenefitAsync(
        HttpMethod method,
        string planId,
        string? benefitId,
        UpsertPlanBenefitRequest request)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        var escapedPlanId = Uri.EscapeDataString(planId);
        var url = benefitId == null
            ? $"{baseUrl}/v1/plans/{escapedPlanId}/benefits"
            : $"{baseUrl}/v1/plans/{escapedPlanId}/benefits/{Uri.EscapeDataString(benefitId)}";
        var payload = new
        {
            benefitType = request.BenefitType,
            serviceCategory = request.ServiceCategory,
            description = request.Description,
            isCovered = request.IsCovered,
            cptCodes = request.CptCodes,
            inNetworkCopay = request.InNetworkCopay,
            outNetworkCopay = request.OutNetworkCopay,
            inNetworkCoinsurance = NormalizeApiPercent(request.InNetworkCoinsurancePercent),
            outNetworkCoinsurance = NormalizeApiPercent(request.OutNetworkCoinsurancePercent),
            deductibleApplies = request.DeductibleApplies,
            oopApplies = request.OopApplies,
            priorAuthRequired = request.PriorAuthRequired,
            visitLimit = request.VisitLimit,
            visitLimitPeriod = request.VisitLimitPeriod,
        };

        try
        {
            using var message = new HttpRequestMessage(method, url)
            {
                Content = JsonContent.Create(payload),
            };
            using var response = await _httpClient.SendAsync(message);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<MemberBenefitView?> GetMemberViewAsync(string planId, DateTime serviceDate)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        var url = $"{baseUrl}/v1/benefit-plans/{Uri.EscapeDataString(planId)}/member-view?serviceDate={serviceDate:yyyy-MM-dd}";
        try
        {
            var response = await _httpClient.GetAsync(url);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MemberBenefitView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    private class CreateBenefitPlanResponse
    {
        public string PlanId { get; set; } = string.Empty;
    }

    private static BenefitPlanListItem MapListItem(BenefitPlanApiResponse plan)
    {
        var primaryNetwork = plan.NetworkTiers
            .OrderBy(tier => tier.TierLevel)
            .FirstOrDefault();

        return new BenefitPlanListItem
        {
            PlanId = plan.PlanId,
            PlanName = plan.PlanName,
            SponsorId = plan.Payer,
            SponsorName = plan.Payer,
            ProductType = plan.PlanType,
            Network = primaryNetwork?.NetworkId ?? primaryNetwork?.TierName ?? string.Empty,
            EnrolledMembers = 0,
            AssignedBenefits = plan.Benefits.Count(IsCovered),
            MonthlyPremium = plan.CostSharing.MonthlyPremium,
            Status = MapStatus(plan),
            EffectiveDate = plan.EffectiveDate,
            TerminationDate = plan.TerminationDate
        };
    }

    private static BenefitPlanDetails MapDetails(BenefitPlanApiResponse plan)
    {
        var listItem = MapListItem(plan);
        return new BenefitPlanDetails
        {
            PlanId = listItem.PlanId,
            VersionId = plan.VersionId,
            VersionNumber = plan.VersionNumber,
            VersionState = plan.VersionState,
            PlanName = listItem.PlanName,
            SponsorId = listItem.SponsorId,
            SponsorName = listItem.SponsorName,
            ProductType = listItem.ProductType,
            Network = listItem.Network,
            EnrolledMembers = listItem.EnrolledMembers,
            AssignedBenefits = listItem.AssignedBenefits,
            MonthlyPremium = listItem.MonthlyPremium,
            Status = listItem.Status,
            EffectiveDate = listItem.EffectiveDate,
            TerminationDate = listItem.TerminationDate,
            MetalTier = plan.MetalLevel ?? string.Empty,
            IndividualDeductible = plan.CostSharing.IndividualDeductible,
            FamilyDeductible = plan.CostSharing.FamilyDeductible,
            IndividualOOPMax = plan.CostSharing.IndividualOutOfPocketMax,
            FamilyOOPMax = plan.CostSharing.FamilyOutOfPocketMax,
            Coinsurance = plan.CostSharing.Coinsurance,
            PlanYear = plan.EffectiveDate.Year.ToString(),
            Benefits = plan.Benefits.Where(IsCovered).Select(MapBenefit).ToList(),
            Exclusions = plan.Benefits.Where(benefit => !IsCovered(benefit)).Select(MapBenefit).ToList(),
            NetworkTiers = plan.NetworkTiers
                .OrderBy(tier => tier.TierLevel)
                .Select(tier => new PlanNetworkTier
                {
                    Id = tier.Id,
                    TierName = tier.TierName,
                    TierLevel = tier.TierLevel,
                    NetworkId = tier.NetworkId ?? string.Empty,
                }).ToList()
        };
    }

    private static bool IsCovered(BenefitPlanApiBenefit benefit) => benefit.IsCovered != false;

    private static PlanBenefit MapBenefit(BenefitPlanApiBenefit benefit)
    {
        var coinsurance = benefit.InNetworkCoinsurance ?? benefit.CoinsurancePercentage;
        var normalizedCoinsurance = NormalizePercent(coinsurance);

        return new PlanBenefit
        {
            BenefitId = benefit.Id,
            BenefitType = string.IsNullOrWhiteSpace(benefit.BenefitType) ? "medical" : benefit.BenefitType,
            ServiceCategory = benefit.ServiceCategory,
            Description = benefit.Description,
            IsCovered = IsCovered(benefit),
            ServiceType = string.IsNullOrWhiteSpace(benefit.Description)
                ? benefit.ServiceCategory
                : benefit.Description,
            Category = string.IsNullOrWhiteSpace(benefit.BenefitType)
                ? "Medical"
                : char.ToUpperInvariant(benefit.BenefitType[0]) + benefit.BenefitType[1..],
            Copay = benefit.InNetworkCopay ?? benefit.CopayAmount,
            CoinsurancePercent = normalizedCoinsurance,
            OutNetworkCopay = benefit.OutNetworkCopay,
            OutNetworkCoinsurancePercent = NormalizePercent(benefit.OutNetworkCoinsurance),
            CoveragePercent = normalizedCoinsurance.HasValue ? 100m - normalizedCoinsurance.Value : null,
            AnnualLimit = benefit.VisitLimit,
            VisitLimitPeriod = benefit.VisitLimitPeriod,
            DeductibleApplies = benefit.DeductibleApplies,
            OopApplies = benefit.OopApplies,
            PriorAuthRequired = benefit.PriorAuthRequired || benefit.RequiresPriorAuth,
            CptCodes = benefit.CptCodes,
        };
    }

    private static decimal? NormalizeApiPercent(decimal? value)
        => value.HasValue ? value.Value / 100m : null;

    private static decimal? NormalizePercent(decimal? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value is >= 0m and <= 1m ? value.Value * 100m : value.Value;
    }

    private static string MapStatus(BenefitPlanApiResponse plan) => plan.VersionState switch
    {
        "Draft" => "Pending",
        "Published" => plan.IsActive ? "Active" : "Inactive",
        "Superseded" => "Inactive",
        _ => plan.IsActive ? "Active" : "Inactive"
    };

    private sealed class BenefitPlanApiResponse
    {
        public string PlanId { get; set; } = string.Empty;
        public string VersionId { get; set; } = string.Empty;
        public int VersionNumber { get; set; }
        public string PlanName { get; set; } = string.Empty;
        public string Payer { get; set; } = string.Empty;
        public string PlanType { get; set; } = string.Empty;
        public string? MetalLevel { get; set; }
        public DateTime EffectiveDate { get; set; }
        public DateTime? TerminationDate { get; set; }
        public bool IsActive { get; set; }
        public string VersionState { get; set; } = string.Empty;
        public List<BenefitPlanApiNetworkTier> NetworkTiers { get; set; } = new();
        public BenefitPlanApiCostSharing CostSharing { get; set; } = new();
        public List<BenefitPlanApiBenefit> Benefits { get; set; } = new();
    }

    private sealed class BenefitPlanApiNetworkTier
    {
        public string Id { get; set; } = string.Empty;
        public string TierName { get; set; } = string.Empty;
        public int TierLevel { get; set; }
        public string? NetworkId { get; set; }
    }

    private sealed class BenefitPlanApiCostSharing
    {
        public decimal IndividualDeductible { get; set; }
        public decimal FamilyDeductible { get; set; }
        public decimal IndividualOutOfPocketMax { get; set; }
        public decimal FamilyOutOfPocketMax { get; set; }
        public decimal Coinsurance { get; set; }
        public decimal MonthlyPremium { get; set; }
    }

    private sealed class BenefitPlanApiBenefit
    {
        public string Id { get; set; } = string.Empty;
        public string BenefitType { get; set; } = string.Empty;
        public string ServiceCategory { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool? IsCovered { get; set; }
        public List<string> CptCodes { get; set; } = new();
        public decimal? InNetworkCopay { get; set; }
        public decimal? OutNetworkCopay { get; set; }
        public decimal? InNetworkCoinsurance { get; set; }
        public decimal? OutNetworkCoinsurance { get; set; }
        public decimal? CopayAmount { get; set; }
        public decimal? CoinsurancePercentage { get; set; }
        public bool DeductibleApplies { get; set; }
        public bool OopApplies { get; set; } = true;
        public bool PriorAuthRequired { get; set; }
        public bool RequiresPriorAuth { get; set; }
        public int? VisitLimit { get; set; }
        public string? VisitLimitPeriod { get; set; }
    }
}
