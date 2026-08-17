using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace BenefitPlanService.Tests.Repositories;

public sealed class BenefitMongoSerializationTests
{
    [Fact]
    public void Bson_round_trip_preserves_dental_benefit_subtype()
    {
        var plan = new BenefitPlan
        {
            Benefits =
            {
                new DentalBenefit
                {
                    ServiceCategory = "Orthodontics",
                    IsOrthodontic = true,
                    LifetimeBenefitMaximum = 1_500m,
                },
            },
        };

        var document = plan.ToBsonDocument();
        var reloaded = BsonSerializer.Deserialize<BenefitPlan>(document);

        var benefit = reloaded.Benefits.Should().ContainSingle().Subject
            .Should().BeOfType<DentalBenefit>().Subject;
        benefit.IsOrthodontic.Should().BeTrue();
        benefit.LifetimeBenefitMaximum.Should().Be(1_500m);
    }

    [Fact]
    public void Legacy_benefit_without_bson_discriminator_ignores_derived_fields()
    {
        var document = new BsonDocument
        {
            ["Benefits"] = new BsonArray
            {
                new BsonDocument
                {
                    ["Id"] = "legacy-benefit",
                    ["ServiceCategory"] = "Preventive",
                    ["IsCovered"] = true,
                    ["IsOrthodontic"] = false,
                    ["LifetimeBenefitMaximum"] = BsonNull.Value,
                },
            },
        };

        var reloaded = BsonSerializer.Deserialize<BenefitPlan>(document);

        reloaded.Benefits.Should().ContainSingle()
            .Which.ServiceCategory.Should().Be("Preventive");
    }
}
