using System.Text.Json;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;
using CloudHealthOffice.Infrastructure.Serialization;

namespace BenefitPlanService.Tests.Models.Benefits;

/// <summary>
/// Typed benefits through the shared Cosmos serializer, which does not
/// register <see cref="BenefitJsonConverter"/>: the property-level
/// <see cref="BenefitListJsonConverter"/> on <see cref="BenefitPlan.Benefits"/>
/// keeps the discriminator and the type-specific facets.
/// </summary>
public class SharedCosmosSerializerBenefitTests
{
    [Fact]
    public void TypedBenefits_RoundTrip_ThroughCosmosSystemTextJsonSerializer()
    {
        var serializer = new CosmosSystemTextJsonSerializer();
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

        using var stream = serializer.ToStream(plan);
        using var doc = JsonDocument.Parse(((MemoryStream)stream).ToArray());
        var pharmacy = doc.RootElement.GetProperty("benefits")[0];
        Assert.Equal("pharmacy", pharmacy.GetProperty("benefitType").GetString());
        Assert.Equal("Tier 2", pharmacy.GetProperty("formularyTier").GetString());

        stream.Position = 0;
        var read = serializer.FromStream<BenefitPlan>(stream);
        Assert.Collection(read.Benefits,
            b => Assert.Equal(90, Assert.IsType<PharmacyBenefit>(b).DaysSupply),
            b => Assert.True(Assert.IsType<DentalBenefit>(b).IsOrthodontic));
    }

    [Fact]
    public void LegacyBenefit_WithoutDiscriminator_HydratesAsMedical()
    {
        var serializer = new CosmosSystemTextJsonSerializer();
        var json = """{"id":"x","tenantId":"t1","planId":"p1","benefits":[{"serviceCategory":"Primary Care","copayAmount":25}]}""";

        var read = serializer.FromStream<BenefitPlan>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));

        var benefit = Assert.IsType<MedicalBenefit>(Assert.Single(read.Benefits));
        Assert.Equal(25m, benefit.CopayAmount);
    }
}
