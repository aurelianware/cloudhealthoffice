using BenefitPlanService.Repositories;
using CloudHealthOffice.BenefitEngine.Domain;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace BenefitPlanService.Tests.Repositories;

public sealed class ChoServiceCategoryMappingRepositoryMongoTests
{
    [Fact]
    public void MappingDocument_Serializes_Rules_With_Bson_Safe_Guid_Ids()
    {
        var mappingId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var mapping = new ServiceCategoryMapping
        {
            Id = mappingId,
            TenantId = "tenant-a",
            BenefitPlanId = planId,
            ServiceTypeCode = "MH",
            ServiceTypeDescription = "Behavioral Health",
            Rules =
            [
                new ProcedureCodeRule
                {
                    Id = ruleId,
                    Priority = 10,
                    CodeType = "CPT",
                    CodePattern = "90785",
                    CodeRangeEnd = "90899",
                    PlaceOfServiceCode = "11",
                    RequiredModifier = "GT",
                    RevenueCode = "0900",
                }
            ],
            EffectiveStart = new DateOnly(2026, 1, 1),
            EffectiveEnd = new DateOnly(2026, 12, 31),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var doc = ChoServiceCategoryMappingRepositoryMongo.MappingDocument.From(mapping);

        var bson = doc.ToBsonDocument();
        var persistedRule = bson["rules"].AsBsonArray.Single().AsBsonDocument;
        persistedRule["id"].AsString.Should().Be(ruleId.ToString());

        var roundTripped = BsonSerializer
            .Deserialize<ChoServiceCategoryMappingRepositoryMongo.MappingDocument>(bson)
            .ToEntity();

        roundTripped.Id.Should().Be(mappingId);
        roundTripped.BenefitPlanId.Should().Be(planId);
        roundTripped.Rules.Should().ContainSingle();
        roundTripped.Rules.Single().Id.Should().Be(ruleId);
        roundTripped.Rules.Single().CodePattern.Should().Be("90785");
        roundTripped.Rules.Single().CodeRangeEnd.Should().Be("90899");
        roundTripped.Rules.Single().PlaceOfServiceCode.Should().Be("11");
        roundTripped.Rules.Single().RequiredModifier.Should().Be("GT");
        roundTripped.Rules.Single().RevenueCode.Should().Be("0900");
    }
}
