using BenefitPlanService.Models;
using BenefitPlanService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudHealthOffice.BenefitPlanService.Tests;

public class BenefitViewServiceTests
{
    private const string Tenant = "tenant-1";

    private readonly IBenefitPlanService _plans = Substitute.For<IBenefitPlanService>();

    private BenefitViewService BuildService() =>
        new(_plans, NullLogger<BenefitViewService>.Instance);

    private static BenefitPlan SamplePlan(params Benefit[] benefits) => new()
    {
        Id = "plan-guid",
        PlanId = "PLAN-001",
        PlanName = "CHO Gold HMO",
        Payer = "CHO",
        PlanType = PlanType.HMO,
        MetalLevel = MetalLevel.Gold,
        LineOfBusiness = LineOfBusiness.Commercial,
        EffectiveDate = new DateTime(2026, 1, 1),
        TerminationDate = new DateTime(2026, 12, 31),
        TenantId = Tenant,
        NetworkTiers = new List<NetworkTier>
        {
            new() { TierName = "Preferred", TierLevel = 1 },
            new() { TierName = "Out-of-Network", TierLevel = 2 },
        },
        CostSharing = new CostSharing
        {
            IndividualDeductible = 1500m,
            FamilyDeductible = 3000m,
            IndividualOutOfPocketMax = 6000m,
            FamilyOutOfPocketMax = 12000m,
        },
        Benefits = benefits.ToList(),
        UpdatedAt = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task Returns_null_when_plan_not_found()
    {
        _plans.GetPlanAsync("missing", Tenant).Returns((BenefitPlan?)null);
        var sut = BuildService();

        var view = await sut.GetMemberViewAsync("missing", Tenant, new DateTime(2026, 4, 18));

        Assert.Null(view);
    }

    [Fact]
    public async Task Maps_known_service_categories_to_canonical_keys()
    {
        var plan = SamplePlan(
            new Benefit { ServiceCategory = "Primary Care", InNetworkCopay = 25m },
            new Benefit { ServiceCategory = "Specialist", InNetworkCopay = 50m },
            new Benefit { ServiceCategory = "Emergency Room", InNetworkCoinsurance = 0.20m },
            new Benefit { ServiceCategory = "Urgent Care", InNetworkCopay = 75m },
            new Benefit { ServiceCategory = "Inpatient Hospital", InNetworkCoinsurance = 0.20m, PriorAuthRequired = true },
            new Benefit { ServiceCategory = "DME", InNetworkCoinsurance = 0.20m },
            new Benefit { ServiceCategory = "Mental Health", InNetworkCopay = 25m },
            new Benefit { ServiceCategory = "Maternity", DeductibleApplies = true },
            new Benefit { ServiceCategory = "Preventive", DeductibleApplies = false, InNetworkCopay = 0m });
        _plans.GetPlanAsync("plan-guid", Tenant).Returns(plan);

        var view = await BuildService().GetMemberViewAsync("plan-guid", Tenant, new DateTime(2026, 4, 18));

        Assert.NotNull(view);
        var categories = view!.Categories.Select(c => c.Category).ToList();
        Assert.Contains(BenefitCategoryMap.PrimaryCare, categories);
        Assert.Contains(BenefitCategoryMap.Specialist, categories);
        Assert.Contains(BenefitCategoryMap.EmergencyRoom, categories);
        Assert.Contains(BenefitCategoryMap.UrgentCare, categories);
        Assert.Contains(BenefitCategoryMap.Hospital, categories);
        Assert.Contains(BenefitCategoryMap.DurableMedical, categories);
        Assert.Contains(BenefitCategoryMap.MentalHealth, categories);
        Assert.Contains(BenefitCategoryMap.Maternity, categories);
        Assert.Contains(BenefitCategoryMap.Preventive, categories);
    }

    [Fact]
    public async Task Unknown_service_category_falls_through_to_Other()
    {
        var plan = SamplePlan(new Benefit { ServiceCategory = "Acupuncture" });
        _plans.GetPlanAsync("plan-guid", Tenant).Returns(plan);

        var view = await BuildService().GetMemberViewAsync("plan-guid", Tenant, new DateTime(2026, 4, 18));

        var only = Assert.Single(view!.Categories);
        Assert.Equal(BenefitCategoryMap.Other, only.Category);
        Assert.Equal("Acupuncture", only.ServiceCategory);
    }

    [Fact]
    public async Task Pharmacy_tier_detail_preserves_verbatim_label_for_non_specialty()
    {
        var plan = SamplePlan(new Benefit { ServiceCategory = "Tier 1", InNetworkCopay = 10m });
        _plans.GetPlanAsync("plan-guid", Tenant).Returns(plan);

        var view = await BuildService().GetMemberViewAsync("plan-guid", Tenant, new DateTime(2026, 4, 18));

        var tier1 = Assert.Single(view!.Categories);
        Assert.Equal(BenefitCategoryMap.Pharmacy, tier1.Category);
        Assert.NotNull(tier1.Pharmacy);
        // TierLabel is the plan's original string, trimmed only.
        Assert.Equal("Tier 1", tier1.Pharmacy!.TierLabel);
        // CanonicalTier is the normalized bucket for analytics.
        Assert.Equal("Tier1", tier1.Pharmacy.CanonicalTier);
        Assert.False(tier1.Pharmacy.IsSpecialty);
    }

    [Fact]
    public async Task Pharmacy_tier_detail_preserves_original_label_for_specialty_drug()
    {
        // The original "Specialty Drug" label must survive — it used to be
        // collapsed to just "Specialty", which silently lost plan wording.
        var plan = SamplePlan(new Benefit { ServiceCategory = "Specialty Drug", InNetworkCoinsurance = 0.30m });
        _plans.GetPlanAsync("plan-guid", Tenant).Returns(plan);

        var view = await BuildService().GetMemberViewAsync("plan-guid", Tenant, new DateTime(2026, 4, 18));

        var specialty = Assert.Single(view!.Categories);
        Assert.NotNull(specialty.Pharmacy);
        Assert.Equal("Specialty Drug", specialty.Pharmacy!.TierLabel);
        Assert.Equal("Specialty", specialty.Pharmacy.CanonicalTier);
        Assert.True(specialty.Pharmacy.IsSpecialty);
    }

    [Fact]
    public async Task OutOfNetwork_tier_is_populated_when_benefit_has_out_values()
    {
        var plan = SamplePlan(new Benefit
        {
            ServiceCategory = "Specialist",
            InNetworkCopay = 50m,
            OutNetworkCoinsurance = 0.40m,
        });
        _plans.GetPlanAsync("plan-guid", Tenant).Returns(plan);

        var view = await BuildService().GetMemberViewAsync("plan-guid", Tenant, new DateTime(2026, 4, 18));

        var cat = Assert.Single(view!.Categories);
        Assert.NotNull(cat.OutOfNetwork);
        Assert.Equal(0.40m, cat.OutOfNetwork!.Coinsurance);
    }

    // Base64 SHA-256 of the empty string — recognizable, deterministic,
    // exactly 32 decoded bytes.
    private const string EmptyStringSha256Base64 = "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=";

    [Fact]
    public async Task Documents_are_projected_with_forward_compatible_fields()
    {
        var plan = SamplePlan();
        plan.Documents.Add(new PlanDocumentReference
        {
            DocType = PlanDocumentType.SBC,
            Location = "https://cdn.example/sbc-2026.pdf",
            ContentType = "application/pdf",
            Size = 182_304,
            ContentHashSha256 = EmptyStringSha256Base64,
            Version = "2026.01",
            EffectiveDate = new DateTime(2026, 1, 1),
            DisplayName = "Summary of Benefits and Coverage",
        });
        _plans.GetPlanAsync("plan-guid", Tenant).Returns(plan);

        var view = await BuildService().GetMemberViewAsync("plan-guid", Tenant, new DateTime(2026, 4, 18));

        var doc = Assert.Single(view!.Documents);
        Assert.Equal("SBC", doc.DocType);
        Assert.Equal("https://cdn.example/sbc-2026.pdf", doc.Location);
        Assert.Equal("application/pdf", doc.ContentType);
        Assert.Equal(182_304, doc.Size);
        Assert.Equal(EmptyStringSha256Base64, doc.ContentHashSha256);
    }

    [Fact]
    public async Task PlanVersion_uses_ModifiedDate_when_set_else_UpdatedAt()
    {
        var plan = SamplePlan(new Benefit { ServiceCategory = "Primary Care", InNetworkCopay = 20m });
        plan.ModifiedDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        _plans.GetPlanAsync("plan-guid", Tenant).Returns(plan);

        var view = await BuildService().GetMemberViewAsync("plan-guid", Tenant, new DateTime(2026, 7, 1));

        Assert.Equal("20260601T000000Z", view!.PlanVersion);
    }

    [Fact]
    public async Task AsOfDate_echoes_requested_service_date()
    {
        var plan = SamplePlan(new Benefit { ServiceCategory = "Primary Care", InNetworkCopay = 20m });
        _plans.GetPlanAsync("plan-guid", Tenant).Returns(plan);

        var view = await BuildService().GetMemberViewAsync("plan-guid", Tenant, new DateTime(2026, 8, 14));

        Assert.Equal(new DateTime(2026, 8, 14), view!.AsOfDate);
    }
}
