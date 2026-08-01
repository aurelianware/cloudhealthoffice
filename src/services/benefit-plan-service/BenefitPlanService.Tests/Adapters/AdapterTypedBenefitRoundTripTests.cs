using System.Text.Json;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;

namespace BenefitPlanService.Tests.Adapters;

/// <summary>
/// Lossless round-trip across the adapter seam:
/// <c>typed Benefit ⇒ AdapterBenefit.From ⇒ ToBenefit ⇒ typed Benefit</c>.
/// External adapters (today CHO; tomorrow QNXT, Facets, HealthEdge) emit
/// <see cref="AdapterBenefit"/> on their member-view APIs, so the
/// discriminator and every type-specific facet must survive both directions.
/// </summary>
public class AdapterTypedBenefitRoundTripTests
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    [Fact]
    public void From_dispatches_pharmacy_to_AdapterPharmacyBenefit()
    {
        Benefit src = new PharmacyBenefit
        {
            Id = "p1",
            ServiceCategory = "Pharmacy",
            FormularyTier = "Tier 2",
            IsSpecialtyDrug = true,
            QuantityLimit = 30,
            DaysSupply = 90,
            CopayAmount = 15m,
        };

        var dto = AdapterBenefit.From(src);

        dto.Should().BeOfType<AdapterPharmacyBenefit>();
        var pharmacy = (AdapterPharmacyBenefit)dto;
        pharmacy.FormularyTier.Should().Be("Tier 2");
        pharmacy.IsSpecialtyDrug.Should().BeTrue();
        pharmacy.QuantityLimit.Should().Be(30);
        pharmacy.DaysSupply.Should().Be(90);
        pharmacy.CopayAmount.Should().Be(15m);
    }

    [Fact]
    public void Base_Benefit_From_returns_AdapterMedicalBenefit_legacy_default()
    {
        var src = new Benefit { Id = "leg", ServiceCategory = "Primary Care", InNetworkCopay = 25m };

        var dto = AdapterBenefit.From(src);

        dto.Should().BeOfType<AdapterMedicalBenefit>();
        dto.InNetworkCopay.Should().Be(25m);
    }

    [Theory]
    [MemberData(nameof(EachTypedBenefit))]
    public void Typed_benefit_round_trips_through_adapter_without_field_loss(Benefit src)
    {
        var dto = AdapterBenefit.From(src);
        var back = dto.ToBenefit();

        back.Should().BeOfType(src.GetType());
        back.ServiceCategory.Should().Be(src.ServiceCategory);
        back.Id.Should().Be(src.Id);
        back.IsCovered.Should().Be(src.IsCovered);
    }

    public static IEnumerable<object[]> EachTypedBenefit() => new[]
    {
        new object[] { new MedicalBenefit { Id = "m", ServiceCategory = "COSMETIC", IsCovered = false } },
        new object[] { new DentalBenefit { Id = "d", ServiceCategory = "Dental", IsOrthodontic = true, LifetimeBenefitMaximum = 1500m } },
        new object[] { new PharmacyBenefit { Id = "p", ServiceCategory = "Pharmacy", FormularyTier = "Tier 1", DaysSupply = 30 } },
        new object[] { new BehavioralHealthBenefit { Id = "bh", ServiceCategory = "Mental Health", ParityCategory = "Outpatient" } },
        new object[] { new VisionBenefit { Id = "v", ServiceCategory = "Vision", FrameAllowance = 200m, LensCoverageType = "Single Vision" } },
        new object[] { new DMEBenefit { Id = "dme", ServiceCategory = "DME", IsRental = true, MaxRentalMonths = 12 } },
        new object[] { new MaternityBenefit { Id = "mat", ServiceCategory = "Maternity", CoversPrenatal = true, CoversDelivery = true } },
        new object[] { new PreventiveBenefit { Id = "prev", ServiceCategory = "Preventive", IsAcaPreventive = true, UspstfRecommendationGrade = "A" } },
    };

    [Fact]
    public void AdapterPharmacyBenefit_serializes_with_pharmacy_discriminator()
    {
        AdapterBenefit dto = AdapterBenefit.From(new PharmacyBenefit
        {
            ServiceCategory = "Pharmacy",
            FormularyTier = "Tier 1",
        });

        var json = JsonSerializer.Serialize(dto, Opts);

        json.Should().Contain("\"benefitType\":\"pharmacy\"");
        json.Should().Contain("\"formularyTier\":\"Tier 1\"");
    }

    [Fact]
    public void AdapterBenefit_deserializes_to_correct_subclass_by_discriminator()
    {
        const string json = """
        {
            "benefitType": "behavioralHealth",
            "id": "bh1",
            "serviceCategory": "Mental Health",
            "isParityProtected": true,
            "parityCategory": "InpatientInNetwork"
        }
        """;

        var dto = JsonSerializer.Deserialize<AdapterBenefit>(json, Opts);

        dto.Should().BeOfType<AdapterBehavioralHealthBenefit>();
        var bh = (AdapterBehavioralHealthBenefit)dto!;
        bh.IsParityProtected.Should().BeTrue();
        bh.ParityCategory.Should().Be("InpatientInNetwork");
    }

    [Fact]
    public void AdapterBenefit_round_trip_via_Json_preserves_typed_facets()
    {
        Benefit src = new DMEBenefit
        {
            ServiceCategory = "DME",
            RequiresFitting = true,
            FittingPeriodDays = 14,
            IsRental = true,
            MaxRentalMonths = 6,
            CoinsurancePercentage = 20m,
        };

        var dto = AdapterBenefit.From(src);
        var json = JsonSerializer.Serialize(dto, Opts);
        var rebuilt = JsonSerializer.Deserialize<AdapterBenefit>(json, Opts);
        var back = rebuilt!.ToBenefit();

        back.Should().BeOfType<DMEBenefit>();
        var dme = (DMEBenefit)back;
        dme.RequiresFitting.Should().BeTrue();
        dme.FittingPeriodDays.Should().Be(14);
        dme.IsRental.Should().BeTrue();
        dme.MaxRentalMonths.Should().Be(6);
        dme.CoinsurancePercentage.Should().Be(20m);
    }

    [Fact]
    public void AdapterBenefitPlan_From_preserves_mixed_typed_benefits()
    {
        var plan = new BenefitPlan
        {
            Id = "plan-x",
            TenantId = "t",
            PlanId = "P",
            PlanName = "Mixed",
            Payer = "Acme",
            EffectiveDate = new DateTime(2026, 1, 1),
            PlanType = PlanType.PPO,
            Benefits =
            {
                new MedicalBenefit { ServiceCategory = "Primary Care", CopayAmount = 25m },
                new PharmacyBenefit { ServiceCategory = "Pharmacy", FormularyTier = "Tier 1" },
                new PreventiveBenefit { ServiceCategory = "Preventive", IsAcaPreventive = true },
            }
        };

        var dto = AdapterBenefitPlan.From(plan);

        dto.Benefits.Should().HaveCount(3);
        dto.Benefits[0].Should().BeOfType<AdapterMedicalBenefit>();
        dto.Benefits[1].Should().BeOfType<AdapterPharmacyBenefit>();
        dto.Benefits[2].Should().BeOfType<AdapterPreventiveBenefit>();

        var back = dto.ToBenefitPlan();
        back.Benefits[0].Should().BeOfType<MedicalBenefit>();
        back.Benefits[1].Should().BeOfType<PharmacyBenefit>();
        back.Benefits[2].Should().BeOfType<PreventiveBenefit>();
        ((PharmacyBenefit)back.Benefits[1]).FormularyTier.Should().Be("Tier 1");
    }

    [Fact]
    public void AdapterBenefitPlan_round_trip_preserves_administrative_cost_sharing()
    {
        var plan = new BenefitPlan
        {
            TenantId = "t",
            PlanId = "P",
            PlanName = "Premium plan",
            Payer = "Acme",
            EffectiveDate = new DateTime(2026, 1, 1),
            PlanType = PlanType.PPO,
            CostSharing = new CostSharing
            {
                MonthlyPremium = 475m,
                Coinsurance = 20m,
                IndividualDeductible = 1500m,
            },
        };

        var back = AdapterBenefitPlan.From(plan).ToBenefitPlan();

        back.CostSharing.MonthlyPremium.Should().Be(475m);
        back.CostSharing.Coinsurance.Should().Be(20m);
        back.CostSharing.IndividualDeductible.Should().Be(1500m);
    }
}
