using System.Text.Json;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;

namespace BenefitPlanService.Tests.Models.Benefits;

/// <summary>
/// Typed benefits through System.Text.Json options that do not register
/// <see cref="BenefitJsonConverter"/>: the property-level
/// <see cref="BenefitListJsonConverter"/> on <see cref="BenefitPlan.Benefits"/>
/// keeps the discriminator and the type-specific facets.
/// </summary>
public class BenefitListJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TypedBenefits_RoundTrip_WithoutTheConverterRegistered()
    {
        var plan = new BenefitPlan
        {
            TenantId = "t1",
            PlanId = "p1",
            Benefits =
            {
                new PharmacyBenefit { ServiceCategory = "Pharmacy", FormularyTier = "Tier 2", DaysSupply = 90 },
                new DentalBenefit { ServiceCategory = "Dental", IsOrthodontic = true },
            },
        };

        var json = JsonSerializer.Serialize(plan, Options);
        using var doc = JsonDocument.Parse(json);
        var pharmacy = doc.RootElement.GetProperty("benefits")[0];
        Assert.Equal("pharmacy", pharmacy.GetProperty("benefitType").GetString());
        Assert.Equal("Tier 2", pharmacy.GetProperty("formularyTier").GetString());

        var read = JsonSerializer.Deserialize<BenefitPlan>(json, Options)!;
        Assert.Collection(read.Benefits,
            b => Assert.Equal(90, Assert.IsType<PharmacyBenefit>(b).DaysSupply),
            b => Assert.True(Assert.IsType<DentalBenefit>(b).IsOrthodontic));
    }

    [Fact]
    public void LegacyBenefit_WithoutDiscriminator_HydratesAsMedical()
    {
        var json = """{"id":"x","tenantId":"t1","planId":"p1","benefits":[{"serviceCategory":"Primary Care","copayAmount":25}]}""";

        var read = JsonSerializer.Deserialize<BenefitPlan>(json, Options)!;

        var benefit = Assert.IsType<MedicalBenefit>(Assert.Single(read.Benefits));
        Assert.Equal(25m, benefit.CopayAmount);
    }
}
