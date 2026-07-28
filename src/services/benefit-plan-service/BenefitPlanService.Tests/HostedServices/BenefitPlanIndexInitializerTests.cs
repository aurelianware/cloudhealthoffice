using BenefitPlanService.HostedServices;
using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace BenefitPlanService.Tests.HostedServices;

public class BenefitPlanIndexInitializerTests
{
    [Fact]
    public void BuildIndexes_covers_every_sorted_benefit_plan_query()
    {
        var serializer = BsonSerializer.SerializerRegistry.GetSerializer<BenefitPlan>();
        var rendered = BenefitPlanIndexInitializer.BuildIndexes()
            .ToDictionary(
                index => index.Options.Name!,
                index => index.Keys.Render(
                    new RenderArgs<BenefitPlan>(
                        serializer,
                        BsonSerializer.SerializerRegistry)));

        rendered["ix_benefitplans_VersionNumber_desc"]["VersionNumber"].AsInt32
            .Should().Be(-1);
        rendered["ix_benefitplans_PlanName_asc"]["PlanName"].AsInt32
            .Should().Be(1);
    }

    [Fact]
    public void ServiceCategoryMappingIndexes_cover_newest_first_reads()
    {
        var serializer = BsonSerializer.SerializerRegistry
            .GetSerializer<ChoServiceCategoryMappingRepositoryMongo.MappingDocument>();
        var index = ServiceCategoryMappingIndexInitializer.BuildIndexes().Single();
        var rendered = index.Keys.Render(
            new RenderArgs<ChoServiceCategoryMappingRepositoryMongo.MappingDocument>(
                serializer,
                BsonSerializer.SerializerRegistry));

        index.Options.Name.Should().Be("ix_service_category_mappings_createdAt_desc");
        rendered["createdAt"].AsInt32.Should().Be(-1);
    }
}
