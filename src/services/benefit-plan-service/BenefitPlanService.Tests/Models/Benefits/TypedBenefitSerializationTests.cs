using System.Text.Json;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;
using BenefitRulePredicate = CloudHealthOffice.BenefitEngine.Domain.BenefitRulePredicate;
using BenefitMemberGender = CloudHealthOffice.BenefitEngine.Domain.BenefitMemberGender;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace BenefitPlanService.Tests.Models.Benefits;

/// <summary>
/// Round-trip every typed benefit subclass through System.Text.Json with the
/// Web defaults (the same options used by repositories and the in-memory
/// fake). Each test asserts that the discriminator <c>"benefitType"</c> is
/// emitted on write and dispatched on read so the concrete subclass and
/// every type-specific facet survive the round-trip.
/// </summary>
public class TypedBenefitSerializationTests
{
    private static readonly JsonSerializerOptions Opts = BuildOpts();
    private static JsonSerializerOptions BuildOpts()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new BenefitJsonConverter());
        return o;
    }

    [Fact]
    public void MedicalBenefit_round_trips_with_medical_discriminator()
    {
        Benefit src = new MedicalBenefit
        {
            Id = "b1",
            ServiceCategory = "Primary Care",
            InNetworkCopay = 25m,
            DeductibleApplies = false,
        };

        var json = JsonSerializer.Serialize(src, Opts);
        json.Should().Contain("\"benefitType\":\"medical\"");

        var back = JsonSerializer.Deserialize<Benefit>(json, Opts);
        back.Should().BeOfType<MedicalBenefit>();
        back!.Id.Should().Be("b1");
        back.InNetworkCopay.Should().Be(25m);
    }

    [Fact]
    public void MedicalBenefit_round_trips_explicit_exclusion()
    {
        Benefit src = new MedicalBenefit
        {
            Id = "excluded-1",
            ServiceCategory = "COSMETIC",
            Description = "Cosmetic Procedures",
            IsCovered = false,
            CptCodes = ["15819", "15820"],
        };

        var json = JsonSerializer.Serialize(src, Opts);
        var back = JsonSerializer.Deserialize<Benefit>(json, Opts);

        back.Should().NotBeNull();
        back!.IsCovered.Should().BeFalse();
        back.CptCodes.Should().Equal("15819", "15820");
    }

    [Fact]
    public void MedicalBenefit_mongo_round_trip_preserves_explicit_exclusion()
    {
        var src = new MedicalBenefit
        {
            ServiceCategory = "COSMETIC",
            Description = "Cosmetic Procedures",
            IsCovered = false,
        };

        var document = src.ToBsonDocument();
        var back = BsonSerializer.Deserialize<MedicalBenefit>(document);

        back.IsCovered.Should().BeFalse();
    }

    [Fact]
    public void DentalBenefit_round_trips_orthodontic_facets()
    {
        Benefit src = new DentalBenefit
        {
            ServiceCategory = "Orthodontics",
            IsOrthodontic = true,
            IsImplant = false,
            LifetimeBenefitMaximum = 1500m,
            CopayAmount = 0m,
        };

        var json = JsonSerializer.Serialize(src, Opts);
        json.Should().Contain("\"benefitType\":\"dental\"");
        json.Should().Contain("\"lifetimeBenefitMaximum\":1500");

        var back = JsonSerializer.Deserialize<Benefit>(json, Opts);
        back.Should().BeOfType<DentalBenefit>();
        var dental = (DentalBenefit)back!;
        dental.IsOrthodontic.Should().BeTrue();
        dental.LifetimeBenefitMaximum.Should().Be(1500m);
    }

    [Fact]
    public void PharmacyBenefit_round_trips_formulary_facets()
    {
        Benefit src = new PharmacyBenefit
        {
            ServiceCategory = "Pharmacy",
            FormularyTier = "Tier 2",
            IsSpecialtyDrug = true,
            RequiresStepTherapy = true,
            QuantityLimit = 30,
            DaysSupply = 90,
        };

        var json = JsonSerializer.Serialize(src, Opts);
        json.Should().Contain("\"benefitType\":\"pharmacy\"");

        var back = JsonSerializer.Deserialize<Benefit>(json, Opts);
        back.Should().BeOfType<PharmacyBenefit>();
        var pharmacy = (PharmacyBenefit)back!;
        pharmacy.FormularyTier.Should().Be("Tier 2");
        pharmacy.IsSpecialtyDrug.Should().BeTrue();
        pharmacy.RequiresStepTherapy.Should().BeTrue();
        pharmacy.QuantityLimit.Should().Be(30);
        pharmacy.DaysSupply.Should().Be(90);
    }

    [Fact]
    public void PharmacyBenefit_with_null_FormularyTier_is_still_pharmacy()
    {
        Benefit src = new PharmacyBenefit
        {
            ServiceCategory = "Pharmacy",
            FormularyTier = null,
        };

        var json = JsonSerializer.Serialize(src, Opts);
        var back = JsonSerializer.Deserialize<Benefit>(json, Opts);

        back.Should().BeOfType<PharmacyBenefit>();
        ((PharmacyBenefit)back!).FormularyTier.Should().BeNull();
    }

    [Fact]
    public void BehavioralHealthBenefit_defaults_parity_to_true()
    {
        var src = new BehavioralHealthBenefit
        {
            ServiceCategory = "Mental Health",
            ParityCategory = "OutpatientInNetwork",
        };
        src.IsParityProtected.Should().BeTrue();

        var json = JsonSerializer.Serialize<Benefit>(src, Opts);
        json.Should().Contain("\"benefitType\":\"behavioralHealth\"");

        var back = (BehavioralHealthBenefit)JsonSerializer.Deserialize<Benefit>(json, Opts)!;
        back.IsParityProtected.Should().BeTrue();
        back.ParityCategory.Should().Be("OutpatientInNetwork");
    }

    [Fact]
    public void VisionBenefit_round_trips_frame_allowance()
    {
        Benefit src = new VisionBenefit
        {
            ServiceCategory = "Vision",
            IsRoutineExam = true,
            FrameAllowance = 200m,
            LensCoverageType = "Progressive",
        };

        var json = JsonSerializer.Serialize(src, Opts);
        var back = JsonSerializer.Deserialize<Benefit>(json, Opts);

        back.Should().BeOfType<VisionBenefit>();
        var vision = (VisionBenefit)back!;
        vision.IsRoutineExam.Should().BeTrue();
        vision.FrameAllowance.Should().Be(200m);
        vision.LensCoverageType.Should().Be("Progressive");
    }

    [Fact]
    public void DMEBenefit_round_trips_rental_facets()
    {
        Benefit src = new DMEBenefit
        {
            ServiceCategory = "DME",
            RequiresFitting = true,
            FittingPeriodDays = 14,
            IsRental = true,
            MaxRentalMonths = 12,
        };

        var json = JsonSerializer.Serialize(src, Opts);
        var back = JsonSerializer.Deserialize<Benefit>(json, Opts);

        back.Should().BeOfType<DMEBenefit>();
        var dme = (DMEBenefit)back!;
        dme.RequiresFitting.Should().BeTrue();
        dme.FittingPeriodDays.Should().Be(14);
        dme.IsRental.Should().BeTrue();
        dme.MaxRentalMonths.Should().Be(12);
    }

    [Fact]
    public void MaternityBenefit_round_trips_episode_flags()
    {
        Benefit src = new MaternityBenefit
        {
            ServiceCategory = "Maternity",
            CoversPrenatal = true,
            CoversDelivery = true,
            CoversPostpartum = true,
            CoversNICU = false,
        };

        var json = JsonSerializer.Serialize(src, Opts);
        var back = JsonSerializer.Deserialize<Benefit>(json, Opts);

        back.Should().BeOfType<MaternityBenefit>();
        var maternity = (MaternityBenefit)back!;
        maternity.CoversPrenatal.Should().BeTrue();
        maternity.CoversDelivery.Should().BeTrue();
        maternity.CoversPostpartum.Should().BeTrue();
        maternity.CoversNICU.Should().BeFalse();
    }

    [Fact]
    public void PreventiveBenefit_round_trips_aca_grade()
    {
        Benefit src = new PreventiveBenefit
        {
            ServiceCategory = "Preventive",
            IsAcaPreventive = true,
            UspstfRecommendationGrade = "A",
        };

        var json = JsonSerializer.Serialize(src, Opts);
        var back = JsonSerializer.Deserialize<Benefit>(json, Opts);

        back.Should().BeOfType<PreventiveBenefit>();
        var prev = (PreventiveBenefit)back!;
        prev.IsAcaPreventive.Should().BeTrue();
        prev.UspstfRecommendationGrade.Should().Be("A");
    }

    [Fact]
    public void Mixed_benefits_in_a_plan_each_round_trip_to_their_concrete_type()
    {
        var plan = new BenefitPlan
        {
            TenantId = "t",
            PlanId = "p",
            PlanName = "Mixed",
            Payer = "Acme",
            EffectiveDate = new DateTime(2026, 1, 1),
            PlanType = PlanType.PPO,
            Benefits =
            {
                new MedicalBenefit { ServiceCategory = "Primary Care", CopayAmount = 25m },
                new PharmacyBenefit { ServiceCategory = "Pharmacy", FormularyTier = "Tier 1" },
                new BehavioralHealthBenefit { ServiceCategory = "Mental Health", ParityCategory = "Outpatient" },
                new PreventiveBenefit { ServiceCategory = "Preventive", IsAcaPreventive = true, UspstfRecommendationGrade = "A" },
            }
        };

        var json = JsonSerializer.Serialize(plan, Opts);
        var back = JsonSerializer.Deserialize<BenefitPlan>(json, Opts)!;

        back.Benefits.Should().HaveCount(4);
        back.Benefits[0].Should().BeOfType<MedicalBenefit>();
        back.Benefits[1].Should().BeOfType<PharmacyBenefit>();
        back.Benefits[2].Should().BeOfType<BehavioralHealthBenefit>();
        back.Benefits[3].Should().BeOfType<PreventiveBenefit>();

        ((PharmacyBenefit)back.Benefits[1]).FormularyTier.Should().Be("Tier 1");
        ((PreventiveBenefit)back.Benefits[3]).UspstfRecommendationGrade.Should().Be("A");
    }

    [Fact]
    public void Rules_list_round_trips_on_base_class()
    {
        Benefit src = new MedicalBenefit
        {
            ServiceCategory = "Primary Care",
            Rules = new List<BenefitRulePredicate>
            {
                new() { MemberAgeMin = 18, MemberAgeMax = 64, MemberGender = BenefitMemberGender.Any }
            }
        };

        var json = JsonSerializer.Serialize(src, Opts);
        var back = JsonSerializer.Deserialize<Benefit>(json, Opts)!;

        back.Rules.Should().NotBeNull();
        back.Rules!.Should().HaveCount(1);
        back.Rules[0].MemberAgeMin.Should().Be(18);
        back.Rules[0].MemberAgeMax.Should().Be(64);
    }
}
